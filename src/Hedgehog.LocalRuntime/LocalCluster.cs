using System.Security.Cryptography;
using Hedgehog.Agent.Core;
using Hedgehog.Agent.Store;
using Hedgehog.Client;
using Hedgehog.Head;
using Hedgehog.Metadata.Sqlite;
using Microsoft.Data.Sqlite;

namespace Hedgehog.LocalRuntime;

public sealed record LocalClusterOptions(
    string RuntimeRoot,
    int HeadCount,
    int StorageNodeCount,
    int RequiredReplicaCount,
    long StorageNodeCapacityBytes,
    string TenantId,
    string DatasetId,
    byte[] DatasetLookupKey,
    byte[] DatasetDataKey)
{
    public static LocalClusterOptions CreateDefault(string runtimeRoot) =>
        new(
            runtimeRoot,
            HeadCount: 2,
            StorageNodeCount: 3,
            RequiredReplicaCount: 3,
            StorageNodeCapacityBytes: 64L * 1024L * 1024L,
            TenantId: "tenant-local",
            DatasetId: "dataset-local",
            DatasetLookupKey: RandomNumberGenerator.GetBytes(32),
            DatasetDataKey: RandomNumberGenerator.GetBytes(32));
}

public sealed record LocalClusterSnapshot(
    string RuntimeRoot,
    string MetadataPath,
    IReadOnlyList<LocalTenantSnapshot> Tenants,
    IReadOnlyList<HeadNodeSnapshot> Heads,
    IReadOnlyList<StorageAgentSnapshot> StorageNodes);

public sealed record LocalOutboxSnapshot(
    long PendingRows,
    long LeasedRows,
    long FailedRows,
    long DeliveredRows,
    long OldestPendingAgeSeconds);

public sealed record LocalTenantSnapshot(
    string TenantId,
    string DatasetId,
    int HeadCount,
    int RequiredReplicaCount);

public sealed record LocalTenantRegistration(
    string TenantId,
    string DatasetId,
    byte[] DatasetLookupKey,
    byte[] DatasetDataKey,
    int RequiredReplicaCount);

internal sealed record LocalTenantRuntime(
    LocalTenantRegistration Registration,
    IReadOnlyList<LocalHeadNode> Heads);

public sealed class LocalCluster : IAsyncDisposable
{
    private readonly LocalClusterOptions options;
    private readonly List<FileStorageAgent> storageNodes = [];
    private readonly Dictionary<string, LocalTenantRuntime> tenants = new(StringComparer.Ordinal);
    private readonly List<SqliteConnection> headConnections = [];
    private readonly RecordingOutboxPublisher outboxPublisher = new();
    private bool started;

    public LocalCluster(LocalClusterOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.RuntimeRoot))
        {
            throw new ArgumentException("Runtime root is required.", nameof(options));
        }

        if (options.HeadCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The local runtime smoke requires at least two head nodes.");
        }

        if (options.StorageNodeCount < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The local runtime smoke requires at least three storage nodes.");
        }

        if (options.RequiredReplicaCount <= 0 || options.RequiredReplicaCount > options.StorageNodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Required replica count must fit available storage nodes.");
        }
    }

    public string RuntimeRoot => options.RuntimeRoot;

    public string MetadataPath => Path.Combine(RuntimeRoot, "metadata", "hedgehog.sqlite");

    public IReadOnlyList<IHeadNode> Heads => tenants.Values.SelectMany(tenant => tenant.Heads).Cast<IHeadNode>().ToArray();

    public IReadOnlyList<IStorageAgentNode> StorageNodes => storageNodes;

    public IReadOnlyList<OutboxDispatchMessage> PublishedOutboxMessages => outboxPublisher.Messages;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (started)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath)!);
        Directory.CreateDirectory(Path.Combine(RuntimeRoot, "storage"));

        await using (var migrationConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await SqliteMetadataAuthority.CreateMigrationRunner()
                .ApplyMigrationsAsync(migrationConnection, cancellationToken)
                .ConfigureAwait(false);
            await ExecuteConnectionPragmaAsync(
                migrationConnection,
                "PRAGMA journal_mode = WAL;",
                cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < options.StorageNodeCount; i++)
        {
            var agent = new FileStorageAgent(
                $"node-{i + 1}",
                Path.Combine(RuntimeRoot, "storage"),
                options.StorageNodeCapacityBytes);
            await agent.StartAsync(cancellationToken).ConfigureAwait(false);
            storageNodes.Add(agent);
        }

        await AddTenantAsync(
            options.TenantId,
            options.DatasetId,
            options.DatasetLookupKey,
            options.DatasetDataKey,
            options.RequiredReplicaCount,
            cancellationToken).ConfigureAwait(false);

        started = true;
    }

    public HedgehogClient CreateClient(string clientId, bool preferLastHead = false)
    {
        return CreateClientForTenant(options.TenantId, options.DatasetId, clientId, preferLastHead);
    }

    public HedgehogClient CreateClientForTenant(
        string tenantId,
        string datasetId,
        string clientId,
        bool preferLastHead = false)
    {
        RequireStarted();
        var tenant = GetTenant(tenantId, datasetId);
        var orderedHeads = preferLastHead
            ? tenant.Heads.AsEnumerable().Reverse().Cast<IHeadNode>().ToArray()
            : tenant.Heads.Cast<IHeadNode>().ToArray();
        return new HedgehogClient(
            new HedgehogClientOptions(
                clientId,
                tenant.Registration.TenantId,
                tenant.Registration.DatasetId,
                tenant.Registration.DatasetLookupKey,
                tenant.Registration.DatasetDataKey),
            orderedHeads);
    }

    public async Task<LocalTenantRegistration> AddTenantAsync(
        string tenantId,
        string datasetId,
        CancellationToken cancellationToken = default) =>
        await AddTenantAsync(
            tenantId,
            datasetId,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            options.RequiredReplicaCount,
            cancellationToken).ConfigureAwait(false);

    public async Task<LocalTenantRegistration> AddTenantAsync(
        string tenantId,
        string datasetId,
        byte[] datasetLookupKey,
        byte[] datasetDataKey,
        int requiredReplicaCount,
        CancellationToken cancellationToken = default)
    {
        if (storageNodes.Count == 0)
        {
            throw new InvalidOperationException("Storage nodes must be running before tenants can be added.");
        }

        if (requiredReplicaCount <= 0 || requiredReplicaCount > storageNodes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredReplicaCount), "Required replica count must fit available storage nodes.");
        }

        if (datasetLookupKey.Length != 32)
        {
            throw new ArgumentException("Dataset lookup key must be exactly 32 bytes.", nameof(datasetLookupKey));
        }

        if (datasetDataKey.Length != 32)
        {
            throw new ArgumentException("Dataset data key must be exactly 32 bytes.", nameof(datasetDataKey));
        }

        var key = TenantKey(tenantId, datasetId);
        if (tenants.TryGetValue(key, out var existing))
        {
            return existing.Registration;
        }

        var tenantHeads = new List<LocalHeadNode>();
        for (var i = 0; i < options.HeadCount; i++)
        {
            var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            headConnections.Add(connection);

            var headId = $"{tenantId}-{datasetId}-head-{i + 1}";
            var head = new LocalHeadNode(
                new HeadNodeOptions(
                    HeadId: headId,
                    tenantId,
                    datasetId,
                    ActorId: headId,
                    LookupKeyId: $"lookup-key-{tenantId}-{datasetId}",
                    DataKeyId: $"data-key-{tenantId}-{datasetId}",
                    requiredReplicaCount),
                connection,
                SqliteMetadataAuthority.CreateWorkflowStore(),
                storageNodes);
            await head.StartAsync(cancellationToken).ConfigureAwait(false);
            tenantHeads.Add(head);
        }

        var registration = new LocalTenantRegistration(
            tenantId,
            datasetId,
            datasetLookupKey.ToArray(),
            datasetDataKey.ToArray(),
            requiredReplicaCount);
        tenants.Add(key, new LocalTenantRuntime(registration, tenantHeads));
        return registration;
    }

    public async Task<LocalClusterSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        RequireStarted();
        var headSnapshots = new List<HeadNodeSnapshot>();
        foreach (var head in tenants.Values.SelectMany(tenant => tenant.Heads))
        {
            headSnapshots.Add(await head.SnapshotAsync(cancellationToken).ConfigureAwait(false));
        }

        var storageSnapshots = new List<StorageAgentSnapshot>();
        foreach (var node in storageNodes)
        {
            storageSnapshots.Add(await node.SnapshotAsync(cancellationToken).ConfigureAwait(false));
        }

        var tenantSnapshots = tenants.Values
            .Select(tenant => new LocalTenantSnapshot(
                tenant.Registration.TenantId,
                tenant.Registration.DatasetId,
                tenant.Heads.Count,
                tenant.Registration.RequiredReplicaCount))
            .OrderBy(tenant => tenant.TenantId, StringComparer.Ordinal)
            .ThenBy(tenant => tenant.DatasetId, StringComparer.Ordinal)
            .ToArray();

        return new LocalClusterSnapshot(RuntimeRoot, MetadataPath, tenantSnapshots, headSnapshots, storageSnapshots);
    }

    public async Task<OutboxDispatchResult> DispatchOutboxAsync(
        int maxItems = 25,
        TimeSpan? leaseDuration = null,
        string? topic = null,
        CancellationToken cancellationToken = default)
    {
        RequireStarted();
        var claimed = 0;
        var delivered = 0;
        var failed = 0;
        foreach (var head in tenants.Values.SelectMany(tenant => tenant.Heads))
        {
            var result = await head.DispatchOutboxAsync(
                outboxPublisher,
                maxItems,
                leaseDuration ?? TimeSpan.FromSeconds(30),
                topic,
                cancellationToken).ConfigureAwait(false);
            claimed += result.Claimed;
            delivered += result.Delivered;
            failed += result.Failed;
            if (result.Claimed == 0)
            {
                continue;
            }

            break;
        }

        return new OutboxDispatchResult(claimed, delivered, failed);
    }

    public async Task<LocalOutboxSnapshot> OutboxSnapshotAsync(CancellationToken cancellationToken = default)
    {
        RequireStarted();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pending = await ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM outbox_events
            WHERE delivered_at_ms IS NULL
              AND available_at_ms <= @now_ms
              AND (claimed_until_ms IS NULL OR claimed_until_ms <= @now_ms)
              AND attempt_count = 0;
            """,
            cancellationToken,
            ("@now_ms", nowMs)).ConfigureAwait(false);
        var leased = await ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM outbox_events
            WHERE delivered_at_ms IS NULL
              AND claimed_until_ms > @now_ms;
            """,
            cancellationToken,
            ("@now_ms", nowMs)).ConfigureAwait(false);
        var failed = await ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM outbox_events
            WHERE delivered_at_ms IS NULL
              AND available_at_ms <= @now_ms
              AND (claimed_until_ms IS NULL OR claimed_until_ms <= @now_ms)
              AND attempt_count > 0;
            """,
            cancellationToken,
            ("@now_ms", nowMs)).ConfigureAwait(false);
        var delivered = await ScalarLongAsync(
            "SELECT COUNT(*) FROM outbox_events WHERE delivered_at_ms IS NOT NULL;",
            cancellationToken).ConfigureAwait(false);
        var oldestAvailable = await ScalarNullableLongAsync(
            """
            SELECT MIN(available_at_ms)
            FROM outbox_events
            WHERE delivered_at_ms IS NULL
              AND available_at_ms <= @now_ms
              AND (claimed_until_ms IS NULL OR claimed_until_ms <= @now_ms);
            """,
            cancellationToken,
            ("@now_ms", nowMs)).ConfigureAwait(false);
        var oldestAgeSeconds = oldestAvailable is null
            ? 0
            : Math.Max(0, (nowMs - oldestAvailable.Value) / 1000);

        return new LocalOutboxSnapshot(pending, leased, failed, delivered, oldestAgeSeconds);
    }

    public async Task<long> ScalarLongAsync(
        string sql,
        CancellationToken cancellationToken = default,
        params (string Name, object? Value)[] parameters)
    {
        RequireStarted();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    private async Task<long?> ScalarNullableLongAsync(
        string sql,
        CancellationToken cancellationToken = default,
        params (string Name, object? Value)[] parameters)
    {
        RequireStarted();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var head in tenants.Values.SelectMany(tenant => tenant.Heads))
        {
            await head.StopAsync().ConfigureAwait(false);
        }

        foreach (var node in storageNodes)
        {
            await node.StopAsync().ConfigureAwait(false);
        }

        foreach (var connection in headConnections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = MetadataPath,
        Cache = SqliteCacheMode.Shared,
    }.ToString();

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteConnectionPragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteConnectionPragmaAsync(connection, "PRAGMA busy_timeout = 10000;", cancellationToken)
            .ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteConnectionPragmaAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RequireStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("Local cluster is not running.");
        }
    }

    private LocalTenantRuntime GetTenant(string tenantId, string datasetId)
    {
        if (!tenants.TryGetValue(TenantKey(tenantId, datasetId), out var tenant))
        {
            throw new InvalidOperationException($"Tenant dataset '{tenantId}/{datasetId}' is not registered.");
        }

        return tenant;
    }

    private static string TenantKey(string tenantId, string datasetId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(datasetId))
        {
            throw new ArgumentException("Tenant and dataset ids are required.");
        }

        return $"{tenantId}/{datasetId}";
    }

    private sealed class RecordingOutboxPublisher : IOutboxPublisher
    {
        private readonly List<OutboxDispatchMessage> messages = [];

        public IReadOnlyList<OutboxDispatchMessage> Messages => messages.ToArray();

        public Task PublishAsync(OutboxDispatchMessage message, CancellationToken cancellationToken = default)
        {
            messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
