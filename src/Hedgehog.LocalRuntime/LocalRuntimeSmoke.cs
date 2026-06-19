using System.Text;

namespace Hedgehog.LocalRuntime;

public sealed record LocalRuntimeSmokeResult(
    string RuntimeRoot,
    int HeadCount,
    int StorageNodeCount,
    int PublishedObjects,
    int VerifiedRetrievals,
    bool DeleteVerified,
    long MetadataObjectRows,
    long HealthyReplicaRows);

public static class LocalRuntimeSmoke
{
    public static async Task<LocalRuntimeSmokeResult> RunAsync(
        LocalClusterOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var cluster = new LocalCluster(options);
        await cluster.StartAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = await cluster.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Heads.Count < 2 || snapshot.Heads.Any(head => !head.IsRunning))
        {
            throw new InvalidOperationException("Smoke requires at least two running head nodes.");
        }

        if (snapshot.StorageNodes.Count < 3 || snapshot.StorageNodes.Any(node => !node.IsRunning))
        {
            throw new InvalidOperationException("Smoke requires at least three running storage nodes.");
        }

        var clientA = cluster.CreateClient("client-a");
        var clientB = cluster.CreateClient("client-b", preferLastHead: true);

        var publishedA = await clientA.PutTextAsync(
            "docs/client-a-object.txt",
            "hello from client a",
            cancellationToken).ConfigureAwait(false);
        if (publishedA.ReplicaCount != options.RequiredReplicaCount)
        {
            throw new InvalidOperationException("Client A object was not replicated to the required count.");
        }

        var retrievedByB = await clientB.GetTextAsync("docs/client-a-object.txt", cancellationToken).ConfigureAwait(false);
        if (retrievedByB != "hello from client a")
        {
            throw new InvalidOperationException("Client B could not retrieve Client A data.");
        }

        var payloadB = Encoding.UTF8.GetBytes("hello from client b with binary-safe bytes");
        var publishedB = await clientB.PutAsync("docs/client-b-object.bin", payloadB, cancellationToken).ConfigureAwait(false);
        if (publishedB.ReplicaCount != options.RequiredReplicaCount)
        {
            throw new InvalidOperationException("Client B object was not replicated to the required count.");
        }

        var retrievedByA = await clientA.GetAsync("docs/client-b-object.bin", cancellationToken).ConfigureAwait(false);
        if (!payloadB.SequenceEqual(retrievedByA.Plaintext))
        {
            throw new InvalidOperationException("Client A could not retrieve Client B data.");
        }

        var afterWrites = await cluster.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (afterWrites.StorageNodes.Any(node => node.Replicas.Count < 2))
        {
            throw new InvalidOperationException("Every storage node should hold replicas after two fully replicated writes.");
        }

        await clientA.DeleteAsync("docs/client-a-object.txt", cancellationToken).ConfigureAwait(false);
        var deleteVerified = false;
        try
        {
            await clientB.GetTextAsync("docs/client-a-object.txt", cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            deleteVerified = true;
        }

        if (!deleteVerified)
        {
            throw new InvalidOperationException("Deleted object remained retrievable.");
        }

        var objectRows = await cluster.ScalarLongAsync(
            "SELECT COUNT(*) FROM objects;",
            cancellationToken).ConfigureAwait(false);
        var healthyReplicaRows = await cluster.ScalarLongAsync(
            "SELECT COUNT(*) FROM replicas WHERE state = 'healthy';",
            cancellationToken).ConfigureAwait(false);
        var plaintextNameRows = await cluster.ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM objects
            WHERE object_id LIKE '%client-a%'
               OR object_id LIKE '%client-b%';
            """,
            cancellationToken).ConfigureAwait(false);

        if (plaintextNameRows != 0)
        {
            throw new InvalidOperationException("Metadata object ids leaked plaintext client object names.");
        }

        return new LocalRuntimeSmokeResult(
            options.RuntimeRoot,
            snapshot.Heads.Count,
            snapshot.StorageNodes.Count,
            PublishedObjects: 2,
            VerifiedRetrievals: 2,
            DeleteVerified: deleteVerified,
            MetadataObjectRows: objectRows,
            HealthyReplicaRows: healthyReplicaRows);
    }
}
