using System.Data;
using System.Security.Cryptography;
using Hedgehog.Agent.Core;
using Hedgehog.Metadata.Sqlite;
using Microsoft.Data.Sqlite;

namespace Hedgehog.Head;

public sealed class LocalHeadNode : IHeadNode
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SqliteConnection connection;
    private readonly ISqliteMetadataWorkflowStore workflowStore;
    private readonly IReadOnlyList<IStorageAgentNode> storageNodes;
    private readonly HeadNodeOptions options;
    private bool isRunning;

    public LocalHeadNode(
        HeadNodeOptions options,
        SqliteConnection connection,
        ISqliteMetadataWorkflowStore workflowStore,
        IReadOnlyList<IStorageAgentNode> storageNodes)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.workflowStore = workflowStore ?? throw new ArgumentNullException(nameof(workflowStore));
        this.storageNodes = storageNodes is { Count: > 0 }
            ? storageNodes
            : throw new ArgumentException("At least one storage node is required.", nameof(storageNodes));

        if (options.RequiredReplicaCount <= 0 || options.RequiredReplicaCount > storageNodes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Required replica count must fit available storage nodes.");
        }
    }

    public string HeadId => options.HeadId;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureAuthoritySeedAsync(cancellationToken).ConfigureAwait(false);
            isRunning = true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.CompletedTask.ConfigureAwait(false);
            isRunning = false;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PublishObjectResult> PublishAsync(
        PublishObjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireRunning();
            var now = DateTimeOffset.UtcNow;
            var versionNo = await NextVersionNoAsync(request.ObjectId, cancellationToken).ConfigureAwait(false);
            var versionId = $"ver_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..37];
            var ciphertextHash = SHA256.HashData(request.Ciphertext);
            var placements = await SelectStorageNodesAsync(request.Ciphertext.LongLength, cancellationToken).ConfigureAwait(false);
            var reservations = placements
                .Select((node, index) => new SqliteReplicaReservation(
                    ReservationId: $"res_{versionId}_{index + 1}",
                    ReplicaId: $"rep_{versionId}_{index + 1}",
                    NodeId: node.NodeId,
                    BytesReserved: request.Ciphertext.LongLength,
                    FencingToken: versionNo * 100 + index + 1))
                .ToArray();

            await workflowStore.CreateWriteIntentAsync(
                connection,
                new SqliteCreateWriteIntentRequest(
                    options.TenantId,
                    options.DatasetId,
                    request.ObjectId,
                    request.ObjectLookupHash,
                    options.LookupKeyId,
                    versionId,
                    versionNo,
                    options.ActorId,
                    ciphertextHash,
                    request.Ciphertext.LongLength,
                    request.EncryptionAlg,
                    options.DataKeyId,
                    options.RequiredReplicaCount,
                    PlacementEpoch: versionNo,
                    DeleteEpoch: 0,
                    now,
                    TimeSpan.FromMinutes(15),
                    $"{request.IdempotencyKey}:intent",
                    reservations),
                cancellationToken).ConfigureAwait(false);

            var committedReplicas = new List<ReplicaCommit>();
            for (var i = 0; i < placements.Count; i++)
            {
                var node = placements[i];
                var reservation = reservations[i];
                var stored = await node.StoreReplicaAsync(
                    new StorageReplicaWrite(
                        versionId,
                        reservation.ReplicaId,
                        request.Ciphertext,
                        ciphertextHash,
                        reservation.FencingToken),
                    cancellationToken).ConfigureAwait(false);

                await workflowStore.CompleteReplicaAsync(
                    connection,
                    new SqliteCompleteReplicaRequest(
                        options.TenantId,
                        options.DatasetId,
                        request.ObjectId,
                        versionId,
                        reservation.ReplicaId,
                        node.NodeId,
                        stored.ContentHash,
                        stored.StoredBytes,
                        reservation.FencingToken,
                        PlacementEpoch: versionNo,
                        DeleteEpoch: 0,
                        stored.CompletedAt,
                        $"{request.IdempotencyKey}:complete:{node.NodeId}"),
                    cancellationToken).ConfigureAwait(false);

                committedReplicas.Add(new ReplicaCommit(node.NodeId, reservation.ReplicaId, stored.StoredBytes, stored.ContentHash));
            }

            await workflowStore.CommitVersionAsync(
                connection,
                new SqliteCommitVersionRequest(
                    options.TenantId,
                    options.DatasetId,
                    request.ObjectId,
                    versionId,
                    options.ActorId,
                    DateTimeOffset.UtcNow,
                    $"{request.IdempotencyKey}:commit"),
                cancellationToken).ConfigureAwait(false);

            return new PublishObjectResult(HeadId, request.ObjectId, versionId, versionNo, ciphertextHash, committedReplicas);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RetrieveObjectResult> RetrieveAsync(
        RetrieveObjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireRunning();
            var metadata = await LoadCurrentVersionAsync(request.ObjectLookupHash, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Object was not found.");
            var replicas = await LoadHealthyReplicasAsync(metadata.VersionId, cancellationToken).ConfigureAwait(false);

            foreach (var replica in replicas)
            {
                var node = storageNodes.FirstOrDefault(candidate => candidate.NodeId == replica.NodeId);
                if (node is null)
                {
                    continue;
                }

                try
                {
                    var ciphertext = await node.ReadReplicaAsync(
                        new StorageReplicaRead(metadata.VersionId, replica.ReplicaId, metadata.ContentHash),
                        cancellationToken).ConfigureAwait(false);
                    return new RetrieveObjectResult(
                        HeadId,
                        metadata.ObjectId,
                        metadata.VersionId,
                        ciphertext,
                        metadata.ContentHash,
                        replicas.Select(item => new ReplicaCommit(item.NodeId, item.ReplicaId, item.StoredBytes, metadata.ContentHash)).ToArray());
                }
                catch (IOException)
                {
                    continue;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
            }

            throw new InvalidOperationException($"No readable healthy replica was available for version '{metadata.VersionId}'.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeleteObjectResult> DeleteAsync(
        DeleteObjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireRunning();
            var now = DateTimeOffset.UtcNow;
            var versionNo = await NextVersionNoAsync(request.ObjectId, cancellationToken).ConfigureAwait(false);
            var deleteMarkerVersionId = $"del_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..37];

            await workflowStore.CreateDeleteMarkerAsync(
                connection,
                new SqliteCreateDeleteMarkerRequest(
                    options.TenantId,
                    options.DatasetId,
                    request.ObjectId,
                    request.ObjectLookupHash,
                    options.LookupKeyId,
                    deleteMarkerVersionId,
                    versionNo,
                    options.ActorId,
                    PlacementEpoch: versionNo,
                    DeleteEpoch: versionNo,
                    now,
                    $"{request.IdempotencyKey}:delete"),
                cancellationToken).ConfigureAwait(false);

            return new DeleteObjectResult(HeadId, request.ObjectId, deleteMarkerVersionId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HeadNodeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            var objectCount = await ScalarLongAsync(
                "SELECT COUNT(*) FROM objects WHERE tenant_id = @tenant_id AND dataset_id = @dataset_id AND state = 'active';",
                cancellationToken,
                ("@tenant_id", options.TenantId),
                ("@dataset_id", options.DatasetId)).ConfigureAwait(false);
            return new HeadNodeSnapshot(HeadId, isRunning, storageNodes.Count, objectCount);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<IStorageAgentNode>> SelectStorageNodesAsync(
        long requiredBytes,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<(IStorageAgentNode Node, StorageAgentSnapshot Snapshot)>();
        foreach (var node in storageNodes)
        {
            var snapshot = await node.SnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.IsRunning && snapshot.FreeBytes >= requiredBytes)
            {
                snapshots.Add((node, snapshot));
            }
        }

        var selected = snapshots
            .OrderByDescending(item => item.Snapshot.FreeBytes)
            .Take(options.RequiredReplicaCount)
            .Select(item => item.Node)
            .ToArray();

        if (selected.Length < options.RequiredReplicaCount)
        {
            throw new InvalidOperationException("Not enough running storage nodes have free capacity for the requested write.");
        }

        return selected;
    }

    private async Task<CurrentVersionMetadata?> LoadCurrentVersionAsync(
        byte[] objectLookupHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.object_id, v.version_id, v.content_hash
            FROM objects o
            JOIN object_versions v ON v.version_id = o.current_version_id
            WHERE o.tenant_id = @tenant_id
              AND o.dataset_id = @dataset_id
              AND o.object_lookup_hash = @object_lookup_hash
              AND o.state = 'active'
              AND v.state = 'committed'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@tenant_id", options.TenantId);
        command.Parameters.AddWithValue("@dataset_id", options.DatasetId);
        command.Parameters.AddWithValue("@object_lookup_hash", objectLookupHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CurrentVersionMetadata(
            reader.GetString(0),
            reader.GetString(1),
            (byte[])reader.GetValue(2));
    }

    private async Task<IReadOnlyList<ReplicaMetadata>> LoadHealthyReplicasAsync(
        string versionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, replica_id, COALESCE(byte_count, 0)
            FROM replicas
            WHERE version_id = @version_id
              AND state = 'healthy'
            ORDER BY updated_at_ms DESC, node_id;
            """;
        command.Parameters.AddWithValue("@version_id", versionId);

        var replicas = new List<ReplicaMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            replicas.Add(new ReplicaMetadata(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return replicas;
    }

    private async Task<long> NextVersionNoAsync(string objectId, CancellationToken cancellationToken)
    {
        var current = await ScalarLongAsync(
            """
            SELECT COALESCE(MAX(version_no), 0)
            FROM object_versions
            WHERE object_id = @object_id;
            """,
            cancellationToken,
            ("@object_id", objectId)).ConfigureAwait(false);
        return current + 1;
    }

    private async Task EnsureAuthoritySeedAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await ExecuteAsync(
            """
            INSERT INTO tenants (tenant_id, display_name, state, created_at_ms, updated_at_ms)
            VALUES (@tenant_id, @tenant_id, 'active', @now_ms, @now_ms)
            ON CONFLICT (tenant_id) DO UPDATE SET
                updated_at_ms = excluded.updated_at_ms;

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
            VALUES (
                @dataset_id,
                @tenant_id,
                @dataset_id,
                @lookup_key_id,
                @data_key_id,
                @required_replica_count,
                'active',
                @now_ms,
                @now_ms
            )
            ON CONFLICT (dataset_id) DO UPDATE SET
                required_replica_count = excluded.required_replica_count,
                updated_at_ms = excluded.updated_at_ms;

            INSERT INTO actors (
                actor_id,
                tenant_id,
                display_name,
                actor_kind,
                public_key_fingerprint,
                state,
                created_at_ms
            )
            VALUES (@actor_id, @tenant_id, @actor_id, 'head', @actor_id, 'active', @now_ms)
            ON CONFLICT (actor_id) DO NOTHING;
            """,
            cancellationToken,
            ("@tenant_id", options.TenantId),
            ("@dataset_id", options.DatasetId),
            ("@lookup_key_id", options.LookupKeyId),
            ("@data_key_id", options.DataKeyId),
            ("@required_replica_count", options.RequiredReplicaCount),
            ("@actor_id", options.ActorId),
            ("@now_ms", now)).ConfigureAwait(false);

        foreach (var node in storageNodes)
        {
            await ExecuteAsync(
                """
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
                VALUES (
                    @node_id,
                    @tenant_id,
                    @node_id,
                    'active',
                    @capacity_bytes,
                    0,
                    0,
                    @capacity_bytes,
                    @now_ms,
                    @now_ms
                )
                ON CONFLICT (node_id) DO UPDATE SET
                    state = 'active',
                    last_seen_at_ms = excluded.last_seen_at_ms;
                """,
                cancellationToken,
                ("@node_id", node.NodeId),
                ("@tenant_id", options.TenantId),
                ("@capacity_bytes", (await node.SnapshotAsync(cancellationToken).ConfigureAwait(false)).CapacityBytes),
                ("@now_ms", now)).ConfigureAwait(false);
        }
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(
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

    private async Task<long> ScalarLongAsync(
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

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(scalar);
    }

    private void RequireRunning()
    {
        if (!isRunning)
        {
            throw new InvalidOperationException($"Head node '{HeadId}' is not running.");
        }
    }

    private sealed record CurrentVersionMetadata(string ObjectId, string VersionId, byte[] ContentHash);

    private sealed record ReplicaMetadata(string NodeId, string ReplicaId, long StoredBytes);
}
