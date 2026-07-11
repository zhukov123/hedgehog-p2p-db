namespace Hedgehog.LocalRuntime;

public sealed record LocalRuntimeRestoreDrillResult(
    string SourceRuntimeRoot,
    string RestoredRuntimeRoot,
    int HeadCount,
    int StorageNodeCount,
    int ObjectsWritten,
    int ReadsVerifiedAfterRestore,
    bool DeleteMarkerVerifiedAfterRestore,
    long MetadataObjectRows,
    long MetadataVersionRows,
    long HealthyReplicaRows,
    long DeleteMarkerRows,
    long RestoredReplicaFiles);

public static class LocalRuntimeRestoreDrill
{
    public static async Task<LocalRuntimeRestoreDrillResult> RunAsync(
        string drillRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(drillRoot))
        {
            throw new ArgumentException("Drill root is required.", nameof(drillRoot));
        }

        var sourceRoot = Path.Combine(drillRoot, "source");
        var backupRoot = Path.Combine(drillRoot, "backup");
        var restoredRoot = Path.Combine(drillRoot, "restored");

        if (Directory.Exists(drillRoot))
        {
            throw new InvalidOperationException($"Restore drill root already exists: {drillRoot}");
        }

        var options = LocalClusterOptions.CreateDefault(sourceRoot);
        await using (var sourceCluster = new LocalCluster(options))
        {
            await sourceCluster.StartAsync(cancellationToken).ConfigureAwait(false);
            var writer = sourceCluster.CreateClient("restore-writer");
            var reader = sourceCluster.CreateClient("restore-reader", preferLastHead: true);

            var keepResult = await writer.PutTextAsync(
                "restore/keep.txt",
                "restore drill durable payload",
                cancellationToken).ConfigureAwait(false);
            var deleteResult = await writer.PutTextAsync(
                "restore/deleted.txt",
                "restore drill deleted payload",
                cancellationToken).ConfigureAwait(false);

            if (keepResult.ReplicaCount != options.RequiredReplicaCount
                || deleteResult.ReplicaCount != options.RequiredReplicaCount)
            {
                throw new InvalidOperationException("Restore drill writes were not fully replicated before backup.");
            }

            var beforeRestoreRead = await reader.GetTextAsync("restore/keep.txt", cancellationToken)
                .ConfigureAwait(false);
            if (beforeRestoreRead != "restore drill durable payload")
            {
                throw new InvalidOperationException("Restore drill could not read the durable payload before backup.");
            }

            await writer.DeleteAsync("restore/deleted.txt", cancellationToken).ConfigureAwait(false);
        }

        CopyDirectory(sourceRoot, backupRoot);
        CopyDirectory(backupRoot, restoredRoot);

        var restoredOptions = options with { RuntimeRoot = restoredRoot };
        await using var restoredCluster = new LocalCluster(restoredOptions);
        await restoredCluster.StartAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = await restoredCluster.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var restoredReader = restoredCluster.CreateClient("restore-reader-after", preferLastHead: true);
        var restoredPayload = await restoredReader.GetTextAsync("restore/keep.txt", cancellationToken)
            .ConfigureAwait(false);
        if (restoredPayload != "restore drill durable payload")
        {
            throw new InvalidOperationException("Restored cluster returned the wrong durable payload.");
        }

        var deleteMarkerVerified = false;
        try
        {
            await restoredReader.GetTextAsync("restore/deleted.txt", cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            deleteMarkerVerified = true;
        }

        if (!deleteMarkerVerified)
        {
            throw new InvalidOperationException("Restored cluster allowed a deleted object to be read.");
        }

        var metadataObjectRows = await restoredCluster.ScalarLongAsync(
            "SELECT COUNT(*) FROM objects;",
            cancellationToken).ConfigureAwait(false);
        var metadataVersionRows = await restoredCluster.ScalarLongAsync(
            "SELECT COUNT(*) FROM object_versions;",
            cancellationToken).ConfigureAwait(false);
        var healthyReplicaRows = await restoredCluster.ScalarLongAsync(
            "SELECT COUNT(*) FROM replicas WHERE state = 'healthy';",
            cancellationToken).ConfigureAwait(false);
        var deleteMarkerRows = await restoredCluster.ScalarLongAsync(
            "SELECT COUNT(*) FROM object_versions WHERE state = 'delete_marker';",
            cancellationToken).ConfigureAwait(false);
        var restoredReplicaFiles = Directory.EnumerateFiles(
                Path.Combine(restoredRoot, "storage"),
                "*.bin",
                SearchOption.AllDirectories)
            .LongCount();

        if (metadataObjectRows != 2 || metadataVersionRows != 3 || healthyReplicaRows != 6 || deleteMarkerRows != 1)
        {
            throw new InvalidOperationException("Restored metadata counts did not match the expected restore drill shape.");
        }

        if (restoredReplicaFiles != healthyReplicaRows)
        {
            throw new InvalidOperationException("Restored replica files did not match healthy metadata replicas.");
        }

        return new LocalRuntimeRestoreDrillResult(
            sourceRoot,
            restoredRoot,
            snapshot.Heads.Count,
            snapshot.StorageNodes.Count,
            ObjectsWritten: 2,
            ReadsVerifiedAfterRestore: 1,
            DeleteMarkerVerifiedAfterRestore: deleteMarkerVerified,
            MetadataObjectRows: metadataObjectRows,
            MetadataVersionRows: metadataVersionRows,
            HealthyReplicaRows: healthyReplicaRows,
            DeleteMarkerRows: deleteMarkerRows,
            RestoredReplicaFiles: restoredReplicaFiles);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var targetPath = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath);
        }
    }
}
