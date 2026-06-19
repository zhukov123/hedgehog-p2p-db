using Hedgehog.Metadata.Sqlite;
using Hedgehog.Types;
using Microsoft.Data.Sqlite;

await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();

var runner = SqliteMetadataAuthority.CreateMigrationRunner();
await runner.ApplyMigrationsAsync(connection);

Equal(6, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM __hedgehog_schema_migrations;"));
Equal(Labels.AllGroups.SelectMany(group => group).Count(), await ScalarIntAsync(connection, "SELECT COUNT(*) FROM labels;"));
Equal(13, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM workflow_definitions;"));
Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM metadata_store WHERE store_id = 'default';"));

await AssertTableHasColumnsAsync(connection, "objects", "tenant_id", "dataset_id", "object_lookup_hash", "lookup_key_id");
await AssertTableHasColumnsAsync(connection, "object_versions", "placement_epoch", "delete_epoch", "required_replica_count");
await AssertTableHasColumnsAsync(connection, "replicas", "fencing_token", "placement_epoch", "delete_epoch");
await AssertTableHasColumnsAsync(connection, "capacity_reservations", "reservation_class", "fencing_token", "bytes_reserved");
await AssertForeignKeyCheckCleanAsync(connection);

await runner.ApplyMigrationsAsync(connection);
Equal(6, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM __hedgehog_schema_migrations;"));

await SeedAuthorityAsync(connection);
await WriteLifecyclePersistsCoherentMetadataAsync(connection);
await DeleteMarkerPersistsTombstoneAsync(connection);
await RemainingWorkflowSetPersistsCoherentMetadataAsync(connection);
await AssertForeignKeyCheckCleanAsync(connection);

Console.WriteLine("Hedgehog.Metadata.Sqlite.Tests passed.");

static async Task SeedAuthorityAsync(SqliteConnection connection)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    await ExecuteAsync(
        connection,
        """
        INSERT INTO tenants (tenant_id, display_name, state, created_at_ms, updated_at_ms)
        VALUES ('tenant-a', 'Tenant A', 'active', @now_ms, @now_ms);

        INSERT INTO datasets (
            dataset_id,
            tenant_id,
            display_name,
            lookup_key_id,
            data_key_id,
            required_replica_count,
            state,
            created_at_ms,
            updated_at_ms
        )
        VALUES ('dataset-a', 'tenant-a', 'Dataset A', 'lookup-key-a', 'data-key-a', 2, 'active', @now_ms, @now_ms);

        INSERT INTO actors (
            actor_id,
            tenant_id,
            display_name,
            actor_kind,
            public_key_fingerprint,
            state,
            created_at_ms
        )
        VALUES ('actor-a', 'tenant-a', 'Actor A', 'admin', 'fingerprint-a', 'active', @now_ms);

        INSERT INTO nodes (
            node_id,
            tenant_id,
            display_name,
            state,
            capacity_bytes,
            used_bytes,
            reserved_bytes,
            free_bytes,
            joined_at_ms,
            last_seen_at_ms
        )
        VALUES
            ('node-a', 'tenant-a', 'Node A', 'active', 1000000, 0, 0, 1000000, @now_ms, @now_ms),
            ('node-b', 'tenant-a', 'Node B', 'active', 1000000, 0, 0, 1000000, @now_ms, @now_ms);
        """,
        ("@now_ms", now));
}

static async Task WriteLifecyclePersistsCoherentMetadataAsync(SqliteConnection connection)
{
    var workflowStore = SqliteMetadataAuthority.CreateWorkflowStore();
    var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    var lookupHash = new byte[] { 1, 2, 3, 4 };
    var contentHash = new byte[] { 9, 8, 7, 6 };

    var create = await workflowStore.CreateWriteIntentAsync(
        connection,
        new SqliteCreateWriteIntentRequest(
            "tenant-a",
            "dataset-a",
            "object-a",
            lookupHash,
            "lookup-key-a",
            "version-a",
            1,
            "actor-a",
            contentHash,
            12,
            "xchacha20-poly1305",
            "data-key-a",
            2,
            1,
            0,
            now,
            TimeSpan.FromMinutes(15),
            "idem-create-object-a-v1",
            [
                new("reservation-a", "replica-a", "node-a", 12, 101),
                new("reservation-b", "replica-b", "node-b", 12, 102),
            ]));

    Equal("writing", create.State);
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM objects WHERE object_id = 'object-a' AND tenant_id = 'tenant-a' AND dataset_id = 'dataset-a';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM object_versions WHERE version_id = 'version-a' AND state = 'writing';"));
    Equal(2, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM replicas WHERE version_id = 'version-a' AND state = 'planned';"));
    Equal(2, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM capacity_reservations WHERE version_id = 'version-a' AND state = 'reserved';"));

    var replay = await workflowStore.CreateWriteIntentAsync(
        connection,
        new SqliteCreateWriteIntentRequest(
            "tenant-a",
            "dataset-a",
            "object-a",
            lookupHash,
            "lookup-key-a",
            "version-a",
            1,
            "actor-a",
            contentHash,
            12,
            "xchacha20-poly1305",
            "data-key-a",
            2,
            1,
            0,
            now,
            TimeSpan.FromMinutes(15),
            "idem-create-object-a-v1",
            [
                new("reservation-a", "replica-a", "node-a", 12, 101),
                new("reservation-b", "replica-b", "node-b", 12, 102),
            ]));
    Equal(true, replay.Replayed);

    await workflowStore.CompleteReplicaAsync(
        connection,
        new SqliteCompleteReplicaRequest(
            "tenant-a",
            "dataset-a",
            "object-a",
            "version-a",
            "replica-a",
            "node-a",
            contentHash,
            12,
            101,
            1,
            0,
            now.AddSeconds(1),
            "idem-complete-replica-a"));

    await workflowStore.CompleteReplicaAsync(
        connection,
        new SqliteCompleteReplicaRequest(
            "tenant-a",
            "dataset-a",
            "object-a",
            "version-a",
            "replica-b",
            "node-b",
            contentHash,
            12,
            102,
            1,
            0,
            now.AddSeconds(2),
            "idem-complete-replica-b"));

    Equal(2, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM replicas WHERE version_id = 'version-a' AND state = 'healthy';"));

    var commit = await workflowStore.CommitVersionAsync(
        connection,
        new SqliteCommitVersionRequest(
            "tenant-a",
            "dataset-a",
            "object-a",
            "version-a",
            "actor-a",
            now.AddSeconds(3),
            "idem-commit-version-a"));

    Equal("committed", commit.State);
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM object_versions WHERE version_id = 'version-a' AND state = 'committed';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM objects WHERE object_id = 'object-a' AND current_version_id = 'version-a' AND state = 'active';"));
    Equal(2, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM capacity_reservations WHERE version_id = 'version-a' AND state = 'committed';"));
    Equal(4, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM audit_events WHERE object_id = 'object-a';"));
}

static async Task DeleteMarkerPersistsTombstoneAsync(SqliteConnection connection)
{
    var workflowStore = SqliteMetadataAuthority.CreateWorkflowStore();
    var now = new DateTimeOffset(2026, 1, 2, 4, 5, 6, TimeSpan.Zero);

    var result = await workflowStore.CreateDeleteMarkerAsync(
        connection,
        new SqliteCreateDeleteMarkerRequest(
            "tenant-a",
            "dataset-a",
            "object-a",
            [1, 2, 3, 4],
            "lookup-key-a",
            "delete-version-a",
            2,
            "actor-a",
            1,
            1,
            now,
            "idem-delete-object-a"));

    Equal("delete_marker", result.State);
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM object_versions WHERE version_id = 'delete-version-a' AND state = 'delete_marker';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM objects WHERE object_id = 'object-a' AND current_version_id = 'delete-version-a' AND state = 'delete_marker';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM tombstones WHERE object_id = 'object-a' AND version_id = 'delete-version-a' AND delete_epoch = 1;"));
}

static async Task RemainingWorkflowSetPersistsCoherentMetadataAsync(SqliteConnection connection)
{
    var workflowStore = SqliteMetadataAuthority.CreateWorkflowStore();
    var now = new DateTimeOffset(2026, 1, 2, 5, 6, 7, TimeSpan.Zero);

    var capacity = await workflowStore.RecordCapacityReportAsync(
        connection,
        new SqliteCapacityReportRequest(
            "node-a",
            "pressure",
            1_000_000,
            700_000,
            120_000,
            180_000,
            now,
            "idem-capacity-node-a",
            [1, 1, 2, 3]));

    Equal("pressure", capacity.State);
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM capacity_reports WHERE node_id = 'node-a' AND capacity_pressure = 'pressure';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM nodes WHERE node_id = 'node-a' AND capacity_pressure = 'pressure' AND reserved_bytes = 120000;"));

    var lease = await workflowStore.LeaseRepairAsync(
        connection,
        new SqliteLeaseRepairRequest(
            "tenant-a",
            "dataset-a",
            "object-a",
            "version-a",
            "repair-job-a",
            "replica-a",
            "repair-lease-a",
            "node-b",
            "under_replicated",
            100,
            "below required replica count",
            now.AddSeconds(1),
            TimeSpan.FromMinutes(5),
            "idem-lease-repair-a"));

    Equal("leased", lease.State);
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM repair_jobs WHERE job_id = 'repair-job-a' AND state = 'leased';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM leases WHERE lease_id = 'repair-lease-a' AND state = 'issued';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM object_versions WHERE version_id = 'version-a' AND state = 'under_replicated';"));

    await workflowStore.CreateWriteIntentAsync(
        connection,
        new SqliteCreateWriteIntentRequest(
            "tenant-a",
            "dataset-a",
            "object-b",
            [4, 3, 2, 1],
            "lookup-key-a",
            "version-b",
            1,
            "actor-a",
            [6, 7, 8, 9],
            20,
            "xchacha20-poly1305",
            "data-key-a",
            1,
            1,
            0,
            now.AddSeconds(2),
            TimeSpan.FromMinutes(1),
            "idem-create-object-b-v1",
            [
                new("reservation-c", "replica-c", "node-a", 20, 201),
            ]));

    var expired = await workflowStore.ExpireReservationAsync(
        connection,
        new SqliteExpireReservationRequest(
            "tenant-a",
            "dataset-a",
            "object-b",
            "version-b",
            "reservation-c",
            now.AddMinutes(2),
            "idem-expire-reservation-c"));

    Equal("expired", expired.State);
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM capacity_reservations WHERE reservation_id = 'reservation-c' AND state = 'expired';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM replicas WHERE replica_id = 'replica-c' AND state = 'stale';"));

    var cleanup = await workflowStore.CleanupConversionAsync(
        connection,
        new SqliteCleanupConversionRequest(
            "tenant-a",
            "dataset-a",
            "object-b",
            "version-b",
            "reservation-c",
            "replica-c",
            now.AddMinutes(3),
            RequiresCleanup: true,
            "idem-cleanup-reservation-c"));

    Equal("failed_cleanup_required", cleanup.State);
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM capacity_reservations WHERE reservation_id = 'reservation-c' AND state = 'failed_cleanup_required';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM replicas WHERE replica_id = 'replica-c' AND state = 'delete_pending';"));
}

static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    var value = await command.ExecuteScalarAsync();
    return Convert.ToInt32(value);
}

static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    foreach (var (name, value) in parameters)
    {
        command.Parameters.AddWithValue(name, value);
    }

    await command.ExecuteNonQueryAsync();
}

static async Task AssertTableHasColumnsAsync(SqliteConnection connection, string tableName, params string[] expectedColumns)
{
    var columns = new HashSet<string>(StringComparer.Ordinal);
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({tableName});";
    await using var reader = await command.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        columns.Add(reader.GetString(1));
    }

    foreach (var expectedColumn in expectedColumns)
    {
        if (!columns.Contains(expectedColumn))
        {
            throw new InvalidOperationException($"Expected table '{tableName}' to have column '{expectedColumn}'.");
        }
    }
}

static async Task AssertForeignKeyCheckCleanAsync(SqliteConnection connection)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_key_check;";
    await using var reader = await command.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        throw new InvalidOperationException("PRAGMA foreign_key_check returned violations.");
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
    }
}
