using Hedgehog.Agent.Core;
using Hedgehog.Metadata.Sqlite;
using Microsoft.Data.Sqlite;

namespace Hedgehog.LocalRuntime;

public sealed record LocalRuntimeReadinessOptions(
    TimeSpan OutboxMaxAvailableAge,
    TimeSpan GateTimeout,
    double EmergencyFreeBytesRatio)
{
    public static LocalRuntimeReadinessOptions Default { get; } = new(
        OutboxMaxAvailableAge: TimeSpan.FromMinutes(5),
        GateTimeout: TimeSpan.FromSeconds(2),
        EmergencyFreeBytesRatio: 0.05d);
}

public static class LocalRuntimeReadinessGateStatus
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Unknown = "unknown";

    public static bool IsValid(string status) =>
        status is Passed or Failed or Unknown;
}

public sealed record LocalRuntimeReadinessGate(
    string Label,
    string Status,
    IReadOnlyDictionary<string, string> Diagnostics)
{

    public static LocalRuntimeReadinessGate Passed(
        string label,
        IReadOnlyDictionary<string, string>? diagnostics = null) =>
        Create(label, LocalRuntimeReadinessGateStatus.Passed, diagnostics);

    public static LocalRuntimeReadinessGate Failed(
        string label,
        IReadOnlyDictionary<string, string>? diagnostics = null) =>
        Create(label, LocalRuntimeReadinessGateStatus.Failed, diagnostics);

    public static LocalRuntimeReadinessGate Unknown(
        string label,
        IReadOnlyDictionary<string, string>? diagnostics = null) =>
        Create(label, LocalRuntimeReadinessGateStatus.Unknown, diagnostics);

    private static LocalRuntimeReadinessGate Create(
        string label,
        string status,
        IReadOnlyDictionary<string, string>? diagnostics)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Gate label is required.", nameof(label));
        }

        if (!LocalRuntimeReadinessGateStatus.IsValid(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Readiness gate status must be passed, failed, or unknown.");
        }

        return new LocalRuntimeReadinessGate(label, status, diagnostics ?? EmptyDiagnostics);
    }

    private static IReadOnlyDictionary<string, string> EmptyDiagnostics { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record LocalRuntimeReadinessResult(
    string SchemaVersion,
    DateTimeOffset EvaluatedAtUtc,
    bool Ready,
    IReadOnlyList<LocalRuntimeReadinessGate> Gates);

public sealed class LocalRuntimeReadinessEvaluator
{
    public const string SchemaVersion = "hedgehog.local_runtime.readiness.v1";

    private readonly LocalRuntimeReadinessOptions options;

    public LocalRuntimeReadinessEvaluator(LocalRuntimeReadinessOptions? options = null)
    {
        this.options = options ?? LocalRuntimeReadinessOptions.Default;
    }

    public async Task<LocalRuntimeReadinessResult> EvaluateAsync(
        LocalCluster runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var evaluatedAt = DateTimeOffset.UtcNow;
        var gates = new[]
        {
            await EvaluateGateAsync(
                "schema_current",
                token => EvaluateSchemaCurrentAsync(runtime.MetadataPath, token),
                cancellationToken).ConfigureAwait(false),
            await EvaluateGateAsync(
                "metadata_integrity",
                token => EvaluateMetadataIntegrityAsync(runtime.MetadataPath, token),
                cancellationToken).ConfigureAwait(false),
            await EvaluateGateAsync(
                "outbox_reconciled",
                token => EvaluateOutboxReconciledAsync(runtime.MetadataPath, evaluatedAt, token),
                cancellationToken).ConfigureAwait(false),
            await EvaluateGateAsync(
                "audit_writable",
                token => EvaluateAuditWritableAsync(runtime.MetadataPath, evaluatedAt, token),
                cancellationToken).ConfigureAwait(false),
            await EvaluateGateAsync(
                "storage_consistent",
                token => EvaluateStorageConsistentAsync(runtime, token),
                cancellationToken).ConfigureAwait(false),
            await EvaluateGateAsync(
                "capacity_safe",
                token => EvaluateCapacitySafeAsync(runtime, token),
                cancellationToken).ConfigureAwait(false),
        };

        return new LocalRuntimeReadinessResult(
            SchemaVersion,
            evaluatedAt,
            gates.All(gate => gate.Status == LocalRuntimeReadinessGateStatus.Passed),
            gates);
    }

    private async Task<LocalRuntimeReadinessGate> EvaluateGateAsync(
        string label,
        Func<CancellationToken, Task<LocalRuntimeReadinessGate>> evaluate,
        CancellationToken cancellationToken)
    {
        if (options.GateTimeout <= TimeSpan.Zero)
        {
            return LocalRuntimeReadinessGate.Unknown(label, Diagnostics(
                ("reason", "timeout"),
                ("timeout_ms", "0")));
        }

        using var timeout = new CancellationTokenSource(options.GateTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            return await evaluate(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return LocalRuntimeReadinessGate.Unknown(label, Diagnostics(
                ("reason", "timeout"),
                ("timeout_ms", Milliseconds(options.GateTimeout))));
        }
        catch (OperationCanceledException)
        {
            return LocalRuntimeReadinessGate.Unknown(label, Diagnostics(("reason", "cancelled")));
        }
        catch (Exception)
        {
            return LocalRuntimeReadinessGate.Unknown(label, Diagnostics(("reason", "probe_error")));
        }
    }

    private static async Task<LocalRuntimeReadinessGate> EvaluateSchemaCurrentAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        var expected = await SqliteMetadataAuthority.CreateMigrationSource()
            .LoadMigrationsAsync(cancellationToken)
            .ConfigureAwait(false);
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await OpenConnectionAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, checksum_sha256 FROM __hedgehog_schema_migrations ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetString(0)] = reader.GetString(1);
        }

        var expectedById = expected.ToDictionary(migration => migration.Id, migration => migration.ChecksumSha256, StringComparer.Ordinal);
        var missing = expectedById.Keys.Count(id => !applied.ContainsKey(id));
        var extra = applied.Keys.Count(id => !expectedById.ContainsKey(id));
        var mismatched = expectedById.Count(item =>
            applied.TryGetValue(item.Key, out var checksum)
            && !StringComparer.Ordinal.Equals(checksum, item.Value));

        var diagnostics = Diagnostics(
            ("expected_count", expectedById.Count.ToString()),
            ("applied_count", applied.Count.ToString()),
            ("missing_count", missing.ToString()),
            ("extra_count", extra.ToString()),
            ("checksum_mismatch_count", mismatched.ToString()));

        return missing == 0 && extra == 0 && mismatched == 0
            ? LocalRuntimeReadinessGate.Passed("schema_current", diagnostics)
            : LocalRuntimeReadinessGate.Failed("schema_current", diagnostics);
    }

    private static async Task<LocalRuntimeReadinessGate> EvaluateMetadataIntegrityAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";

        var violationCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            violationCount++;
        }

        var diagnostics = Diagnostics(("violation_count", violationCount.ToString()));
        return violationCount == 0
            ? LocalRuntimeReadinessGate.Passed("metadata_integrity", diagnostics)
            : LocalRuntimeReadinessGate.Failed("metadata_integrity", diagnostics);
    }

    private async Task<LocalRuntimeReadinessGate> EvaluateOutboxReconciledAsync(
        string metadataPath,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        var nowMs = evaluatedAt.ToUnixTimeMilliseconds();
        var thresholdMs = (long)options.OutboxMaxAvailableAge.TotalMilliseconds;
        await using var connection = await OpenConnectionAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        var pendingCount = await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM outbox_events WHERE delivered_at_ms IS NULL;",
            cancellationToken).ConfigureAwait(false);
        var expiredClaimCount = await ScalarLongAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM outbox_events
            WHERE delivered_at_ms IS NULL
              AND claimed_until_ms IS NOT NULL
              AND claimed_until_ms <= @now_ms;
            """,
            cancellationToken,
            ("@now_ms", nowMs)).ConfigureAwait(false);
        var oldestAvailableAtMs = await NullableLongAsync(
            connection,
            """
            SELECT MIN(available_at_ms)
            FROM outbox_events
            WHERE delivered_at_ms IS NULL
              AND available_at_ms <= @now_ms
              AND (claimed_until_ms IS NULL OR claimed_until_ms <= @now_ms);
            """,
            cancellationToken,
            ("@now_ms", nowMs)).ConfigureAwait(false);
        var oldestAgeMs = oldestAvailableAtMs is null ? 0 : Math.Max(0, nowMs - oldestAvailableAtMs.Value);
        var diagnostics = Diagnostics(
            ("pending_count", pendingCount.ToString()),
            ("expired_claim_count", expiredClaimCount.ToString()),
            ("oldest_available_age_ms", oldestAgeMs.ToString()),
            ("max_available_age_ms", thresholdMs.ToString()));

        return expiredClaimCount == 0 && oldestAgeMs <= thresholdMs
            ? LocalRuntimeReadinessGate.Passed("outbox_reconciled", diagnostics)
            : LocalRuntimeReadinessGate.Failed("outbox_reconciled", diagnostics);
    }

    private static async Task<LocalRuntimeReadinessGate> EvaluateAuditWritableAsync(
        string metadataPath,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events (
                workflow,
                decision,
                correlation_id,
                idempotency_key,
                occurred_at_ms
            )
            VALUES (
                'evaluate_recovery_gate',
                'allowed',
                'readiness-probe',
                @idempotency_key,
                @occurred_at_ms
            );
            """;
        command.Parameters.AddWithValue("@idempotency_key", $"readiness-probe-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@occurred_at_ms", evaluatedAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        return LocalRuntimeReadinessGate.Passed("audit_writable", Diagnostics(("rollback_only", "true")));
    }

    private static async Task<LocalRuntimeReadinessGate> EvaluateStorageConsistentAsync(
        LocalCluster runtime,
        CancellationToken cancellationToken)
    {
        var snapshot = await runtime.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var metadataReplicas = await LoadHealthyMetadataReplicasAsync(runtime.MetadataPath, cancellationToken)
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

        var missingManifestCount = 0;
        var missingBlobCount = 0;
        var byteMismatchCount = 0;
        var hashMismatchCount = 0;
        foreach (var metadata in metadataReplicas)
        {
            var key = $"{metadata.NodeId}/{metadata.VersionId}/{metadata.ReplicaId}";
            if (!storageReplicas.TryGetValue(key, out var stored))
            {
                missingManifestCount++;
                continue;
            }

            if (stored.StoredBytes != metadata.StoredBytes)
            {
                byteMismatchCount++;
            }

            if (!stored.ContentHash.SequenceEqual(metadata.ContentHash))
            {
                hashMismatchCount++;
            }

            var replicaPath = Path.Combine(
                runtime.RuntimeRoot,
                "storage",
                metadata.NodeId,
                "replicas",
                metadata.VersionId,
                $"{metadata.ReplicaId}.bin");
            if (!File.Exists(replicaPath))
            {
                missingBlobCount++;
                continue;
            }

            var payload = await File.ReadAllBytesAsync(replicaPath, cancellationToken).ConfigureAwait(false);
            if (payload.LongLength != metadata.StoredBytes)
            {
                byteMismatchCount++;
            }

            if (!StorageHash.EqualsHash(StorageHash.Sha256(payload), metadata.ContentHash))
            {
                hashMismatchCount++;
            }
        }

        var diagnostics = Diagnostics(
            ("healthy_replica_count", metadataReplicas.Count.ToString()),
            ("missing_manifest_count", missingManifestCount.ToString()),
            ("missing_blob_count", missingBlobCount.ToString()),
            ("byte_mismatch_count", byteMismatchCount.ToString()),
            ("hash_mismatch_count", hashMismatchCount.ToString()));

        return missingManifestCount == 0
            && missingBlobCount == 0
            && byteMismatchCount == 0
            && hashMismatchCount == 0
            ? LocalRuntimeReadinessGate.Passed("storage_consistent", diagnostics)
            : LocalRuntimeReadinessGate.Failed("storage_consistent", diagnostics);
    }

    private async Task<LocalRuntimeReadinessGate> EvaluateCapacitySafeAsync(
        LocalCluster runtime,
        CancellationToken cancellationToken)
    {
        var snapshot = await runtime.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var negativeAccountingCount = snapshot.StorageNodes.Count(node =>
            node.UsedBytes < 0 || node.FreeBytes < 0 || node.UsedBytes > node.CapacityBytes);
        var emergencyPressureCount = snapshot.StorageNodes.Count(node =>
            node.CapacityBytes > 0
            && (double)node.FreeBytes / node.CapacityBytes <= options.EmergencyFreeBytesRatio);
        var diagnostics = Diagnostics(
            ("storage_node_count", snapshot.StorageNodes.Count.ToString()),
            ("negative_accounting_count", negativeAccountingCount.ToString()),
            ("emergency_pressure_count", emergencyPressureCount.ToString()),
            ("emergency_free_ratio", options.EmergencyFreeBytesRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));

        return negativeAccountingCount == 0 && emergencyPressureCount == 0
            ? LocalRuntimeReadinessGate.Passed("capacity_safe", diagnostics)
            : LocalRuntimeReadinessGate.Failed("capacity_safe", diagnostics);
    }

    private static async Task<IReadOnlyList<HealthyReplicaRow>> LoadHealthyMetadataReplicasAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(metadataPath, cancellationToken).ConfigureAwait(false);
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

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = metadataPath,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 1000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<long?> NullableLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static IReadOnlyDictionary<string, string> Diagnostics(
        params (string Key, string Value)[] items) =>
        items.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static string Milliseconds(TimeSpan value) =>
        Math.Max(0, (long)value.TotalMilliseconds).ToString();

    private sealed record HealthyReplicaRow(
        string NodeId,
        string VersionId,
        string ReplicaId,
        long StoredBytes,
        byte[] ContentHash);
}
