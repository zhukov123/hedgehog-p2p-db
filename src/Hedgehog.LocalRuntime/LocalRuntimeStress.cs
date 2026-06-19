using System.Text;

namespace Hedgehog.LocalRuntime;

public sealed record LocalRuntimeStressOptions(
    string RuntimeRoot,
    int TenantCount = 3,
    int ObjectsPerTenant = 12,
    int PayloadBytes = 512)
{
    public static LocalRuntimeStressOptions CreateDefault(string runtimeRoot) => new(runtimeRoot);
}

public sealed record LocalRuntimeStressResult(
    string RuntimeRoot,
    int TenantCount,
    int StorageNodeCount,
    int HeadCount,
    int ObjectsWritten,
    int ReadsVerified,
    int DeletesVerified,
    long MetadataObjectRows,
    long MetadataVersionRows,
    long HealthyReplicaRows,
    long DeleteMarkerRows);

public static class LocalRuntimeStress
{
    public static async Task<LocalRuntimeStressResult> RunAsync(
        LocalRuntimeStressOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.TenantCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Tenant count must be positive.");
        }

        if (options.ObjectsPerTenant <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Objects per tenant must be positive.");
        }

        if (options.PayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Payload bytes must be positive.");
        }

        await using var cluster = new LocalCluster(LocalClusterOptions.CreateDefault(options.RuntimeRoot));
        await cluster.StartAsync(cancellationToken).ConfigureAwait(false);

        var tenants = Enumerable.Range(1, options.TenantCount)
            .Select(index => (TenantId: $"tenant-stress-{index:D2}", DatasetId: "dataset-stress"))
            .ToArray();
        foreach (var (tenantId, datasetId) in tenants)
        {
            await cluster.AddTenantAsync(tenantId, datasetId, cancellationToken).ConfigureAwait(false);
        }

        var writes = tenants
            .SelectMany((tenant, tenantIndex) => Enumerable.Range(1, options.ObjectsPerTenant)
                .Select(objectIndex => new StressObject(
                    tenant.TenantId,
                    tenant.DatasetId,
                    ClientId: $"writer-{tenantIndex + 1:D2}-{objectIndex:D3}",
                    Name: $"load/object-{objectIndex:D3}.txt",
                    Text: BuildPayload(tenant.TenantId, objectIndex, options.PayloadBytes),
                    PreferLastHead: objectIndex % 2 == 0)))
            .ToArray();

        await Task.WhenAll(writes.Select(item => PutAsync(cluster, item, cancellationToken))).ConfigureAwait(false);
        await Task.WhenAll(writes.Select(item => VerifyReadAsync(cluster, item, $"reader-{item.ClientId}", cancellationToken))).ConfigureAwait(false);

        var deleteTargets = writes.Where((_, index) => index % 4 == 0).ToArray();
        await Task.WhenAll(deleteTargets.Select(item => DeleteAsync(cluster, item, cancellationToken))).ConfigureAwait(false);
        var deleteVerifications = await Task.WhenAll(deleteTargets.Select(item => VerifyDeletedAsync(cluster, item, cancellationToken)))
            .ConfigureAwait(false);

        var survivors = writes.Except(deleteTargets).ToArray();
        await Task.WhenAll(survivors.Select(item => VerifyReadAsync(cluster, item, $"post-delete-reader-{item.ClientId}", cancellationToken)))
            .ConfigureAwait(false);

        var snapshot = await cluster.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var objectRows = await cluster.ScalarLongAsync("SELECT COUNT(*) FROM objects;", cancellationToken).ConfigureAwait(false);
        var versionRows = await cluster.ScalarLongAsync("SELECT COUNT(*) FROM object_versions;", cancellationToken).ConfigureAwait(false);
        var healthyReplicaRows = await cluster.ScalarLongAsync("SELECT COUNT(*) FROM replicas WHERE state = 'healthy';", cancellationToken).ConfigureAwait(false);
        var deleteMarkerRows = await cluster.ScalarLongAsync("SELECT COUNT(*) FROM object_versions WHERE state = 'delete_marker';", cancellationToken).ConfigureAwait(false);
        var leakedNames = await cluster.ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM objects
            WHERE object_id LIKE '%load/%'
               OR object_id LIKE '%object-%'
               OR object_id LIKE '%tenant-stress%';
            """,
            cancellationToken).ConfigureAwait(false);

        if (leakedNames != 0)
        {
            throw new InvalidOperationException("Stress test found plaintext names in metadata object ids.");
        }

        return new LocalRuntimeStressResult(
            options.RuntimeRoot,
            options.TenantCount,
            snapshot.StorageNodes.Count,
            snapshot.Heads.Count,
            writes.Length,
            ReadsVerified: writes.Length + survivors.Length,
            DeletesVerified: deleteVerifications.Count(deleted => deleted),
            MetadataObjectRows: objectRows,
            MetadataVersionRows: versionRows,
            HealthyReplicaRows: healthyReplicaRows,
            DeleteMarkerRows: deleteMarkerRows);
    }

    private static async Task PutAsync(
        LocalCluster cluster,
        StressObject item,
        CancellationToken cancellationToken)
    {
        var client = cluster.CreateClientForTenant(item.TenantId, item.DatasetId, item.ClientId, item.PreferLastHead);
        var result = await client.PutTextAsync(item.Name, item.Text, cancellationToken).ConfigureAwait(false);
        if (result.ReplicaCount != 3)
        {
            throw new InvalidOperationException($"Expected 3 replicas for {item.Name}, got {result.ReplicaCount}.");
        }
    }

    private static async Task VerifyReadAsync(
        LocalCluster cluster,
        StressObject item,
        string clientId,
        CancellationToken cancellationToken)
    {
        var client = cluster.CreateClientForTenant(item.TenantId, item.DatasetId, clientId, !item.PreferLastHead);
        var text = await client.GetTextAsync(item.Name, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(item.Text, text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Read payload mismatch for {item.TenantId}/{item.Name}.");
        }
    }

    private static async Task DeleteAsync(
        LocalCluster cluster,
        StressObject item,
        CancellationToken cancellationToken)
    {
        var client = cluster.CreateClientForTenant(item.TenantId, item.DatasetId, $"deleter-{item.ClientId}", item.PreferLastHead);
        await client.DeleteAsync(item.Name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyDeletedAsync(
        LocalCluster cluster,
        StressObject item,
        CancellationToken cancellationToken)
    {
        var client = cluster.CreateClientForTenant(item.TenantId, item.DatasetId, $"delete-check-{item.ClientId}", !item.PreferLastHead);
        try
        {
            await client.GetTextAsync(item.Name, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string BuildPayload(string tenantId, int objectIndex, int payloadBytes)
    {
        var prefix = $"{tenantId}:object-{objectIndex:D3}:";
        var builder = new StringBuilder(payloadBytes + prefix.Length);
        while (Encoding.UTF8.GetByteCount(builder.ToString()) < payloadBytes)
        {
            builder.Append(prefix);
        }

        return builder.ToString();
    }

    private sealed record StressObject(
        string TenantId,
        string DatasetId,
        string ClientId,
        string Name,
        string Text,
        bool PreferLastHead);
}
