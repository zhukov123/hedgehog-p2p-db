using Microsoft.Data.Sqlite;

namespace Hedgehog.LocalRuntime;

public sealed record LocalRuntimeRestoreDrillResult(
    string RuntimeRoot,
    int HeadCountAfterRestore,
    int StorageNodeCountAfterRestore,
    int ObjectsRecovered,
    int ReadsVerifiedAfterRestore,
    bool DeleteMarkerRecovered,
    long MetadataObjectRows,
    long MetadataVersionRows,
    long HealthyReplicaRows,
    long HealthyReplicasVerified,
    long CommittedReservationRows,
    long PendingOutboxRows,
    long PendingRepairJobRows,
    long AuditRows,
    int BackupManifestEntries,
    bool MissingReplicaBlobRejected,
    bool CorruptReplicaBlobRejected);

public static class LocalRuntimeRestoreDrill
{
    public static async Task<LocalRuntimeRestoreDrillResult> RunAsync(
        LocalClusterOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var liveObjectName = "restore/live-object.txt";
        var deletedObjectName = "restore/deleted-object.txt";
        var liveObjectText = "restore drill live payload";
        var deletedObjectText = "restore drill deleted payload";
        var metadataPath = Path.Combine(options.RuntimeRoot, "metadata", "hedgehog.sqlite");

        await using (var cluster = new LocalCluster(options))
        {
            await cluster.StartAsync(cancellationToken).ConfigureAwait(false);
            var writer = cluster.CreateClient("restore-writer");
            var livePut = await writer.PutTextAsync(liveObjectName, liveObjectText, cancellationToken).ConfigureAwait(false);
            await writer.PutTextAsync(deletedObjectName, deletedObjectText, cancellationToken).ConfigureAwait(false);
            await writer.DeleteAsync(deletedObjectName, cancellationToken).ConfigureAwait(false);

            await SeedRecoveryRowsAsync(
                metadataPath,
                livePut.VersionId,
                "restore-drill-outbox-1",
                "restore-drill-repair-1",
                cancellationToken).ConfigureAwait(false);
        }

        var backupRoot = Path.Combine(options.RuntimeRoot, "backups", "restore-drill");
        var backupManifest = await LocalRuntimeBackup.CreateAsync(
            options.RuntimeRoot,
            backupRoot,
            cancellationToken).ConfigureAwait(false);
        await LocalRuntimeBackup.ValidateAsync(backupRoot, cancellationToken).ConfigureAwait(false);
        var missingReplicaBlobRejected = await ValidateMissingReplicaBlobIsRejectedAsync(
            backupRoot,
            cancellationToken).ConfigureAwait(false);
        var corruptReplicaBlobRejected = await ValidateCorruptReplicaBlobIsRejectedAsync(
            backupRoot,
            cancellationToken).ConfigureAwait(false);

        await using var restored = new LocalCluster(options);
        await restored.StartAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = await restored.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var reader = restored.CreateClient("restore-reader", preferLastHead: true);
        var restoredText = await reader.GetTextAsync(liveObjectName, cancellationToken).ConfigureAwait(false);
        if (restoredText != liveObjectText)
        {
            throw new InvalidOperationException("Restored cluster returned unexpected plaintext for the live object.");
        }

        var deleteMarkerRecovered = await ThrowsInvalidOperationAsync(
            () => reader.GetTextAsync(deletedObjectName, cancellationToken)).ConfigureAwait(false);
        if (!deleteMarkerRecovered)
        {
            throw new InvalidOperationException("Restored cluster did not preserve the delete marker.");
        }

        var objectRows = await restored.ScalarLongAsync(
            "SELECT COUNT(*) FROM objects;",
            cancellationToken).ConfigureAwait(false);
        var versionRows = await restored.ScalarLongAsync(
            "SELECT COUNT(*) FROM object_versions;",
            cancellationToken).ConfigureAwait(false);
        var healthyReplicas = await restored.ScalarLongAsync(
            "SELECT COUNT(*) FROM replicas WHERE state = 'healthy';",
            cancellationToken).ConfigureAwait(false);
        var committedReservations = await restored.ScalarLongAsync(
            "SELECT COUNT(*) FROM capacity_reservations WHERE state = 'committed';",
            cancellationToken).ConfigureAwait(false);
        var pendingOutbox = await restored.ScalarLongAsync(
            "SELECT COUNT(*) FROM outbox_events WHERE delivered_at_ms IS NULL;",
            cancellationToken).ConfigureAwait(false);
        var pendingRepairJobs = await restored.ScalarLongAsync(
            "SELECT COUNT(*) FROM repair_jobs WHERE state IN ('pending', 'leased', 'running', 'verifying', 'retry_wait');",
            cancellationToken).ConfigureAwait(false);
        var auditRows = await restored.ScalarLongAsync(
            "SELECT COUNT(*) FROM audit_events;",
            cancellationToken).ConfigureAwait(false);
        var healthyReplicasVerified = await VerifyHealthyReplicasAsync(
            metadataPath,
            snapshot,
            cancellationToken).ConfigureAwait(false);

        if (committedReservations < options.RequiredReplicaCount * 2)
        {
            throw new InvalidOperationException("Restore drill did not recover committed capacity reservations.");
        }

        if (pendingOutbox < 1)
        {
            throw new InvalidOperationException("Restore drill did not recover pending outbox work.");
        }

        if (pendingRepairJobs < 1)
        {
            throw new InvalidOperationException("Restore drill did not recover pending repair work.");
        }

        if (healthyReplicasVerified != healthyReplicas)
        {
            throw new InvalidOperationException("Restore drill did not verify every healthy metadata replica in storage manifests.");
        }

        return new LocalRuntimeRestoreDrillResult(
            options.RuntimeRoot,
            snapshot.Heads.Count,
            snapshot.StorageNodes.Count,
            ObjectsRecovered: (int)objectRows,
            ReadsVerifiedAfterRestore: 1,
            deleteMarkerRecovered,
            objectRows,
            versionRows,
            healthyReplicas,
            healthyReplicasVerified,
            committedReservations,
            pendingOutbox,
            pendingRepairJobs,
            auditRows,
            backupManifest.Entries.Count,
            missingReplicaBlobRejected,
            corruptReplicaBlobRejected);
    }

    private static async Task<bool> ValidateMissingReplicaBlobIsRejectedAsync(
        string backupRoot,
        CancellationToken cancellationToken)
    {
        var copyRoot = $"{backupRoot}-missing-blob";
        CopyDirectory(backupRoot, copyRoot);
        var replica = FirstReplicaBlob(copyRoot);
        File.Delete(replica);
        return await ThrowsAnyValidationFailureAsync(
            () => LocalRuntimeBackup.ValidateAsync(copyRoot, cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<bool> ValidateCorruptReplicaBlobIsRejectedAsync(
        string backupRoot,
        CancellationToken cancellationToken)
    {
        var copyRoot = $"{backupRoot}-corrupt-blob";
        CopyDirectory(backupRoot, copyRoot);
        var replica = FirstReplicaBlob(copyRoot);
        await File.WriteAllBytesAsync(replica, [0x48, 0x65, 0x64, 0x67, 0x65], cancellationToken)
            .ConfigureAwait(false);
        return await ThrowsAnyValidationFailureAsync(
            () => LocalRuntimeBackup.ValidateAsync(copyRoot, cancellationToken)).ConfigureAwait(false);
    }

    private static string FirstReplicaBlob(string backupRoot)
    {
        return Directory.EnumerateFiles(Path.Combine(backupRoot, "storage"), "*.bin", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Restore backup did not include any replica blobs.");
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, recursive: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, directory)));
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static async Task SeedRecoveryRowsAsync(
        string metadataPath,
        string versionId,
        string outboxId,
        string repairJobId,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = metadataPath,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO outbox_events (
                outbox_id,
                workflow,
                destination_node_id,
                topic,
                payload,
                idempotency_key,
                available_at_ms,
                created_at_ms
            )
            VALUES (
                @outbox_id,
                'claim_outbox',
                'node-1',
                'restore.drill.pending',
                @payload,
                @outbox_id,
                @now_ms,
                @now_ms
            )
            ON CONFLICT (outbox_id) DO NOTHING;
            """,
            cancellationToken,
            ("@outbox_id", outboxId),
            ("@payload", new byte[] { 1, 2, 3, 4 }),
            ("@now_ms", nowMs)).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO repair_jobs (
                job_id,
                version_id,
                replica_id,
                kind,
                priority,
                state,
                attempt_count,
                not_before_ms,
                last_error,
                idempotency_key,
                created_at_ms,
                updated_at_ms
            )
            VALUES (
                @job_id,
                @version_id,
                NULL,
                'under_replicated',
                100,
                'pending',
                0,
                @now_ms,
                'restore drill pending repair survives restart',
                @job_id,
                @now_ms,
                @now_ms
            )
            ON CONFLICT (job_id) DO NOTHING;
            """,
            cancellationToken,
            ("@job_id", repairJobId),
            ("@version_id", versionId),
            ("@now_ms", nowMs)).ConfigureAwait(false);
    }

    private static async Task<long> VerifyHealthyReplicasAsync(
        string metadataPath,
        LocalClusterSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var metadataReplicas = await LoadHealthyMetadataReplicasAsync(metadataPath, cancellationToken)
            .ConfigureAwait(false);
        var storageReplicas = snapshot.StorageNodes
            .SelectMany(node => node.Replicas.Select(replica => new
            {
                node.NodeId,
                replica.VersionId,
                replica.ReplicaId,
                replica.StoredBytes,
                replica.ContentHash,
            }))
            .ToDictionary(
                replica => $"{replica.NodeId}/{replica.VersionId}/{replica.ReplicaId}",
                StringComparer.Ordinal);

        foreach (var metadata in metadataReplicas)
        {
            var key = $"{metadata.NodeId}/{metadata.VersionId}/{metadata.ReplicaId}";
            if (!storageReplicas.TryGetValue(key, out var stored))
            {
                throw new InvalidOperationException($"Healthy metadata replica '{key}' was missing from restored storage manifests.");
            }

            if (stored.StoredBytes != metadata.StoredBytes)
            {
                throw new InvalidOperationException($"Healthy metadata replica '{key}' restored with unexpected byte count.");
            }

            if (!stored.ContentHash.SequenceEqual(metadata.ContentHash))
            {
                throw new InvalidOperationException($"Healthy metadata replica '{key}' restored with unexpected content hash.");
            }
        }

        return metadataReplicas.Count;
    }

    private static async Task<IReadOnlyList<HealthyReplicaRow>> LoadHealthyMetadataReplicasAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = metadataPath,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.node_id, r.version_id, r.replica_id, COALESCE(r.byte_count, 0), v.content_hash
            FROM replicas r
            JOIN object_versions v ON v.version_id = r.version_id
            WHERE r.state = 'healthy'
            ORDER BY r.node_id, r.version_id, r.replica_id;
            """;

        var rows = new List<HealthyReplicaRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new HealthyReplicaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                (byte[])reader.GetValue(4)));
        }

        return rows;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ThrowsInvalidOperationAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task<bool> ThrowsAnyValidationFailureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private sealed record HealthyReplicaRow(
        string NodeId,
        string VersionId,
        string ReplicaId,
        long StoredBytes,
        byte[] ContentHash);
}
