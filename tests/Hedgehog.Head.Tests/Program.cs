using Hedgehog.Head;
using Hedgehog.Metadata.Sqlite;
using Microsoft.Data.Sqlite;

await DispatcherPublishesAndAcknowledgesOnlyClaimedRowsAsync();
await PublisherFailureLeavesRowsRetryableAsync();

Console.WriteLine("Hedgehog.Head.Tests passed.");

static async Task DispatcherPublishesAndAcknowledgesOnlyClaimedRowsAsync()
{
    await using var connection = await CreateConnectionAsync();
    var now = new DateTimeOffset(2026, 1, 2, 7, 0, 0, TimeSpan.Zero);
    var time = new ManualTimeProvider(now);
    await SeedOutboxAsync(connection, now);

    var publisher = new RecordingPublisher();
    var dispatcher = new HeadOutboxDispatcher(
        connection,
        SqliteMetadataAuthority.CreateWorkflowStore(),
        publisher,
        time);

    var result = await dispatcher.DispatchOnceAsync(
        new OutboxDispatchOptions(
            "head-a",
            TimeSpan.FromMinutes(1),
            MaxItems: 10,
            DestinationNodeId: "node-a",
            Topic: "repair.leased"));

    Equal(2, result.Claimed);
    Equal(2, result.Delivered);
    Equal(0, result.Failed);
    Equal(2, publisher.Messages.Count);
    Equal("outbox-broadcast", publisher.Messages[0].OutboxId);
    Equal("outbox-node-a", publisher.Messages[1].OutboxId);
    Equal(2, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM outbox_events WHERE delivered_at_ms IS NOT NULL;"));
    Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM outbox_events WHERE outbox_id = 'outbox-node-b' AND delivered_at_ms IS NOT NULL;"));
}

static async Task PublisherFailureLeavesRowsRetryableAsync()
{
    await using var connection = await CreateConnectionAsync();
    var now = new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero);
    var time = new ManualTimeProvider(now);
    await SeedOutboxAsync(connection, now, onlyFailureRow: true);

    var failingPublisher = new RecordingPublisher("outbox-node-a");
    var dispatcher = new HeadOutboxDispatcher(
        connection,
        SqliteMetadataAuthority.CreateWorkflowStore(),
        failingPublisher,
        time);

    var failed = await dispatcher.DispatchOnceAsync(
        new OutboxDispatchOptions(
            "head-a",
            TimeSpan.FromMinutes(1),
            MaxItems: 10,
            DestinationNodeId: "node-a",
            Topic: "repair.leased"));

    Equal(1, failed.Claimed);
    Equal(0, failed.Delivered);
    Equal(1, failed.Failed);
    Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM outbox_events WHERE delivered_at_ms IS NOT NULL;"));

    time.Advance(TimeSpan.FromMinutes(2));
    var retryPublisher = new RecordingPublisher();
    dispatcher = new HeadOutboxDispatcher(
        connection,
        SqliteMetadataAuthority.CreateWorkflowStore(),
        retryPublisher,
        time);

    var retried = await dispatcher.DispatchOnceAsync(
        new OutboxDispatchOptions(
            "head-b",
            TimeSpan.FromMinutes(1),
            MaxItems: 10,
            DestinationNodeId: "node-a",
            Topic: "repair.leased"));

    Equal(1, retried.Claimed);
    Equal(1, retried.Delivered);
    Equal(0, retried.Failed);
    Equal(2, await ScalarIntAsync(connection, "SELECT attempt_count FROM outbox_events WHERE outbox_id = 'outbox-node-a';"));
    Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM outbox_events WHERE outbox_id = 'outbox-node-a' AND delivered_at_ms IS NOT NULL;"));
}

static async Task<SqliteConnection> CreateConnectionAsync()
{
    var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();
    await SqliteMetadataAuthority.CreateMigrationRunner().ApplyMigrationsAsync(connection);
    await ExecuteAsync(
        connection,
        """
        INSERT INTO tenants (tenant_id, display_name, state, created_at_ms, updated_at_ms)
        VALUES ('tenant-a', 'Tenant A', 'active', 1, 1);

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
            ('node-a', 'tenant-a', 'Node A', 'active', 1000, 0, 0, 1000, 1, 1),
            ('node-b', 'tenant-a', 'Node B', 'active', 1000, 0, 0, 1000, 1, 1);
        """);
    return connection;
}

static async Task SeedOutboxAsync(
    SqliteConnection connection,
    DateTimeOffset now,
    bool onlyFailureRow = false)
{
    if (onlyFailureRow)
    {
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
            VALUES ('outbox-node-a', 'claim_outbox', 'node-a', 'repair.leased', X'0304', 'idem-outbox-node-a', @now_ms, @now_ms);
            """,
            ("@now_ms", now.ToUnixTimeMilliseconds()));
        return;
    }

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
        VALUES
            ('outbox-broadcast', 'claim_outbox', NULL, 'repair.leased', X'0102', 'idem-outbox-broadcast', @now_ms, @now_ms),
            ('outbox-node-a', 'claim_outbox', 'node-a', 'repair.leased', X'0304', 'idem-outbox-node-a', @now_ms, @now_ms),
            ('outbox-node-b', 'claim_outbox', 'node-b', 'repair.leased', X'0506', 'idem-outbox-node-b', @now_ms, @now_ms),
            ('outbox-other-topic', 'claim_outbox', 'node-a', 'repair.retry', X'0708', 'idem-outbox-other-topic', @now_ms, @now_ms);
        """,
        ("@now_ms", now.ToUnixTimeMilliseconds()));
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

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
    }
}

sealed class RecordingPublisher(string? failOutboxId = null) : IOutboxPublisher
{
    private readonly List<OutboxDispatchMessage> messages = [];

    public IReadOnlyList<OutboxDispatchMessage> Messages => messages;

    public Task PublishAsync(OutboxDispatchMessage message, CancellationToken cancellationToken = default)
    {
        if (message.OutboxId == failOutboxId)
        {
            throw new InvalidOperationException("publisher failed");
        }

        messages.Add(message);
        return Task.CompletedTask;
    }
}

sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset now = now;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan value)
    {
        now += value;
    }
}
