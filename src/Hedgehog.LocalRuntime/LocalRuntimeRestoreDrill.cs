namespace Hedgehog.LocalRuntime;

public sealed record LocalRuntimeRestoreDrillResult(
    string SourceRuntimeRoot,
    string RestoredRuntimeRoot,
    int ObjectsWrittenBeforeBackup,
    int ReadsVerifiedAfterRestore,
    bool DeleteVerifiedAfterRestore,
    int ObjectsWrittenAfterRestore,
    long MetadataObjectRows,
    long MetadataVersionRows,
    long HealthyReplicaRows,
    long DeleteMarkerRows);

public static class LocalRuntimeRestoreDrill
{
    public static async Task<LocalRuntimeRestoreDrillResult> RunAsync(
        LocalClusterOptions sourceOptions,
        string restoredRuntimeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceOptions);
        if (string.IsNullOrWhiteSpace(restoredRuntimeRoot))
        {
            throw new ArgumentException("Restored runtime root is required.", nameof(restoredRuntimeRoot));
        }

        if (Directory.Exists(sourceOptions.RuntimeRoot))
        {
            throw new InvalidOperationException($"Source runtime root already exists: {sourceOptions.RuntimeRoot}");
        }

        if (Directory.Exists(restoredRuntimeRoot))
        {
            throw new InvalidOperationException($"Restored runtime root already exists: {restoredRuntimeRoot}");
        }

        await using (var sourceCluster = new LocalCluster(sourceOptions))
        {
            await sourceCluster.StartAsync(cancellationToken).ConfigureAwait(false);

            var writer = sourceCluster.CreateClient("restore-writer");
            await writer.PutTextAsync("restore/keep-alpha.txt", "alpha survives restore", cancellationToken).ConfigureAwait(false);
            await writer.PutTextAsync("restore/delete-beta.txt", "beta is deleted before backup", cancellationToken).ConfigureAwait(false);
            await writer.DeleteAsync("restore/delete-beta.txt", cancellationToken).ConfigureAwait(false);
        }

        CopyDirectory(sourceOptions.RuntimeRoot, restoredRuntimeRoot);

        var restoredOptions = sourceOptions with { RuntimeRoot = restoredRuntimeRoot };
        await using var restoredCluster = new LocalCluster(restoredOptions);
        await restoredCluster.StartAsync(cancellationToken).ConfigureAwait(false);

        var reader = restoredCluster.CreateClient("restore-reader", preferLastHead: true);
        var restoredText = await reader.GetTextAsync("restore/keep-alpha.txt", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(restoredText, "alpha survives restore", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Restored runtime returned the wrong object payload.");
        }

        var deleteVerified = false;
        try
        {
            await reader.GetTextAsync("restore/delete-beta.txt", cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            deleteVerified = true;
        }

        if (!deleteVerified)
        {
            throw new InvalidOperationException("Restored runtime made a pre-backup delete marker readable.");
        }

        var postRestoreWriter = restoredCluster.CreateClient("restore-post-writer");
        await postRestoreWriter.PutTextAsync("restore/post-gamma.txt", "gamma written after restore", cancellationToken).ConfigureAwait(false);
        var postRestoreReader = restoredCluster.CreateClient("restore-post-reader", preferLastHead: true);
        var postRestoreText = await postRestoreReader.GetTextAsync("restore/post-gamma.txt", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(postRestoreText, "gamma written after restore", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Restored runtime could not accept a new write.");
        }

        var objectRows = await restoredCluster.ScalarLongAsync("SELECT COUNT(*) FROM objects;", cancellationToken).ConfigureAwait(false);
        var versionRows = await restoredCluster.ScalarLongAsync("SELECT COUNT(*) FROM object_versions;", cancellationToken).ConfigureAwait(false);
        var healthyReplicaRows = await restoredCluster.ScalarLongAsync(
            "SELECT COUNT(*) FROM replicas WHERE state = 'healthy';",
            cancellationToken).ConfigureAwait(false);
        var deleteMarkerRows = await restoredCluster.ScalarLongAsync(
            "SELECT COUNT(*) FROM object_versions WHERE state = 'delete_marker';",
            cancellationToken).ConfigureAwait(false);

        return new LocalRuntimeRestoreDrillResult(
            sourceOptions.RuntimeRoot,
            restoredRuntimeRoot,
            ObjectsWrittenBeforeBackup: 2,
            ReadsVerifiedAfterRestore: 2,
            DeleteVerifiedAfterRestore: deleteVerified,
            ObjectsWrittenAfterRestore: 1,
            MetadataObjectRows: objectRows,
            MetadataVersionRows: versionRows,
            HealthyReplicaRows: healthyReplicaRows,
            DeleteMarkerRows: deleteMarkerRows);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            File.Copy(file, Path.Combine(destinationDirectory, relativePath));
        }
    }
}
