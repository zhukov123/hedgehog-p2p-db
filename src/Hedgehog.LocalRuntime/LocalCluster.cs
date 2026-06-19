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
    IReadOnlyList<HeadNodeSnapshot> Heads,
    IReadOnlyList<StorageAgentSnapshot> StorageNodes);

public sealed class LocalCluster : IAsyncDisposable
{
    private readonly LocalClusterOptions options;
    private readonly List<FileStorageAgent> storageNodes = [];
    private readonly List<LocalHeadNode> heads = [];
    private readonly List<SqliteConnection> headConnections = [];
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

    public IReadOnlyList<IHeadNode> Heads => heads;

    public IReadOnlyList<IStorageAgentNode> StorageNodes => storageNodes;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (started)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath)!);
        Directory.CreateDirectory(Path.Combine(RuntimeRoot, "storage"));

        await using (var migrationConnection = new SqliteConnection(ConnectionString))
        {
            await migrationConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteMetadataAuthority.CreateMigrationRunner()
                .ApplyMigrationsAsync(migrationConnection, cancellationToken)
                .ConfigureAwait(false);
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

        for (var i = 0; i < options.HeadCount; i++)
        {
            var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            headConnections.Add(connection);

            var head = new LocalHeadNode(
                new HeadNodeOptions(
                    HeadId: $"head-{i + 1}",
                    options.TenantId,
                    options.DatasetId,
                    ActorId: $"head-{i + 1}",
                    LookupKeyId: "lookup-key-local",
                    DataKeyId: "data-key-local",
                    options.RequiredReplicaCount),
                connection,
                SqliteMetadataAuthority.CreateWorkflowStore(),
                storageNodes);
            await head.StartAsync(cancellationToken).ConfigureAwait(false);
            heads.Add(head);
        }

        started = true;
    }

    public HedgehogClient CreateClient(string clientId, bool preferLastHead = false)
    {
        RequireStarted();
        var orderedHeads = preferLastHead ? heads.AsEnumerable().Reverse().Cast<IHeadNode>().ToArray() : heads.Cast<IHeadNode>().ToArray();
        return new HedgehogClient(
            new HedgehogClientOptions(
                clientId,
                options.TenantId,
                options.DatasetId,
                options.DatasetLookupKey,
                options.DatasetDataKey),
            orderedHeads);
    }

    public async Task<LocalClusterSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        RequireStarted();
        var headSnapshots = new List<HeadNodeSnapshot>();
        foreach (var head in heads)
        {
            headSnapshots.Add(await head.SnapshotAsync(cancellationToken).ConfigureAwait(false));
        }

        var storageSnapshots = new List<StorageAgentSnapshot>();
        foreach (var node in storageNodes)
        {
            storageSnapshots.Add(await node.SnapshotAsync(cancellationToken).ConfigureAwait(false));
        }

        return new LocalClusterSnapshot(RuntimeRoot, MetadataPath, headSnapshots, storageSnapshots);
    }

    public async Task<long> ScalarLongAsync(
        string sql,
        CancellationToken cancellationToken = default,
        params (string Name, object? Value)[] parameters)
    {
        RequireStarted();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var head in heads)
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
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = MetadataPath,
        Cache = SqliteCacheMode.Shared,
    }.ToString();

    private void RequireStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("Local cluster is not running.");
        }
    }
}
