using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hedgehog.Metadata.Core;
using Hedgehog.Types;

namespace Hedgehog.Metadata.Sqlite;

public sealed class SqliteMetadataWorkflowStore : ISqliteMetadataWorkflowStore
{
    public async Task<SqliteWorkflowResult> CreateWriteIntentAsync(
        IDbConnection connection,
        SqliteCreateWriteIntentRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCreateWriteIntent(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replay = await TryBeginIdempotencyAsync(
                db,
                transaction,
                request.IdempotencyKey,
                request.TenantId,
                request.DatasetId,
                MetadataWorkflowNames.CreateWriteIntent,
                request.ActorId,
                request,
                request.RequestedAt,
                cancellationToken).ConfigureAwait(false);

            if (replay)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SqliteWorkflowResult(MetadataWorkflowNames.CreateWriteIntent, "replayed", Replayed: true, []);
            }

            await ExecuteAsync(
                db,
                transaction,
                """
                INSERT INTO objects (
                    object_id,
                    tenant_id,
                    dataset_id,
                    object_lookup_hash,
                    lookup_key_id,
                    current_version_id,
                    state,
                    placement_epoch,
                    delete_epoch,
                    created_at_ms,
                    updated_at_ms
                )
                VALUES (
                    @object_id,
                    @tenant_id,
                    @dataset_id,
                    @object_lookup_hash,
                    @lookup_key_id,
                    NULL,
                    'active',
                    @placement_epoch,
                    @delete_epoch,
                    @now_ms,
                    @now_ms
                )
                ON CONFLICT (object_id) DO UPDATE SET
                    lookup_key_id = excluded.lookup_key_id,
                    updated_at_ms = excluded.updated_at_ms;
                """,
                cancellationToken,
                ("@object_id", request.ObjectId),
                ("@tenant_id", request.TenantId),
                ("@dataset_id", request.DatasetId),
                ("@object_lookup_hash", request.ObjectLookupHash),
                ("@lookup_key_id", request.LookupKeyId),
                ("@placement_epoch", request.PlacementEpoch),
                ("@delete_epoch", request.DeleteEpoch),
                ("@now_ms", ToUnixMs(request.RequestedAt))).ConfigureAwait(false);

            await ExecuteAsync(
                db,
                transaction,
                """
                INSERT INTO object_versions (
                    version_id,
                    object_id,
                    version_no,
                    state,
                    content_hash,
                    size_bytes,
                    encryption_alg,
                    data_key_id,
                    placement_epoch,
                    delete_epoch,
                    required_replica_count,
                    created_at_ms,
                    updated_at_ms
                )
                VALUES (
                    @version_id,
                    @object_id,
                    @version_no,
                    'writing',
                    @content_hash,
                    @size_bytes,
                    @encryption_alg,
                    @data_key_id,
                    @placement_epoch,
                    @delete_epoch,
                    @required_replica_count,
                    @now_ms,
                    @now_ms
                );
                """,
                cancellationToken,
                ("@version_id", request.VersionId),
                ("@object_id", request.ObjectId),
                ("@version_no", request.VersionNo),
                ("@content_hash", request.ContentHash),
                ("@size_bytes", request.SizeBytes),
                ("@encryption_alg", request.EncryptionAlg),
                ("@data_key_id", request.DataKeyId),
                ("@placement_epoch", request.PlacementEpoch),
                ("@delete_epoch", request.DeleteEpoch),
                ("@required_replica_count", request.RequiredReplicaCount),
                ("@now_ms", ToUnixMs(request.RequestedAt))).ConfigureAwait(false);

            var expiresAtMs = ToUnixMs(request.RequestedAt.Add(request.ReservationTtl));
            foreach (var replica in request.Replicas)
            {
                await ExecuteAsync(
                    db,
                    transaction,
                    """
                    INSERT INTO replicas (
                        replica_id,
                        version_id,
                        node_id,
                        state,
                        placement_epoch,
                        delete_epoch,
                        fencing_token,
                        created_at_ms,
                        updated_at_ms
                    )
                    VALUES (
                        @replica_id,
                        @version_id,
                        @node_id,
                        'planned',
                        @placement_epoch,
                        @delete_epoch,
                        @fencing_token,
                        @now_ms,
                        @now_ms
                    );
                    """,
                    cancellationToken,
                    ("@replica_id", replica.ReplicaId),
                    ("@version_id", request.VersionId),
                    ("@node_id", replica.NodeId),
                    ("@placement_epoch", request.PlacementEpoch),
                    ("@delete_epoch", request.DeleteEpoch),
                    ("@fencing_token", replica.FencingToken),
                    ("@now_ms", ToUnixMs(request.RequestedAt))).ConfigureAwait(false);

                await ExecuteAsync(
                    db,
                    transaction,
                    """
                    INSERT INTO capacity_reservations (
                        reservation_id,
                        tenant_id,
                        dataset_id,
                        object_id,
                        version_id,
                        replica_id,
                        node_id,
                        reservation_class,
                        state,
                        bytes_reserved,
                        placement_epoch,
                        delete_epoch,
                        fencing_token,
                        created_at_ms,
                        expires_at_ms
                    )
                    VALUES (
                        @reservation_id,
                        @tenant_id,
                        @dataset_id,
                        @object_id,
                        @version_id,
                        @replica_id,
                        @node_id,
                        'write',
                        'reserved',
                        @bytes_reserved,
                        @placement_epoch,
                        @delete_epoch,
                        @fencing_token,
                        @now_ms,
                        @expires_at_ms
                    );
                    """,
                    cancellationToken,
                    ("@reservation_id", replica.ReservationId),
                    ("@tenant_id", request.TenantId),
                    ("@dataset_id", request.DatasetId),
                    ("@object_id", request.ObjectId),
                    ("@version_id", request.VersionId),
                    ("@replica_id", replica.ReplicaId),
                    ("@node_id", replica.NodeId),
                    ("@bytes_reserved", replica.BytesReserved),
                    ("@placement_epoch", request.PlacementEpoch),
                    ("@delete_epoch", request.DeleteEpoch),
                    ("@fencing_token", replica.FencingToken),
                    ("@now_ms", ToUnixMs(request.RequestedAt)),
                    ("@expires_at_ms", expiresAtMs)).ConfigureAwait(false);
            }

            await AppendAuditAsync(
                db,
                transaction,
                MetadataWorkflowNames.CreateWriteIntent,
                "allowed",
                request.ActorId,
                objectId: request.ObjectId,
                versionId: request.VersionId,
                request.IdempotencyKey,
                request.RequestedAt,
                cancellationToken).ConfigureAwait(false);

            await CompleteIdempotencyAsync(db, transaction, request.IdempotencyKey, request.RequestedAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWorkflowResult(MetadataWorkflowNames.CreateWriteIntent, "writing", Replayed: false, []);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteWorkflowResult> CompleteReplicaAsync(
        IDbConnection connection,
        SqliteCompleteReplicaRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCompleteReplica(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replay = await TryBeginIdempotencyAsync(
                db,
                transaction,
                request.IdempotencyKey,
                request.TenantId,
                request.DatasetId,
                MetadataWorkflowNames.CompleteReplica,
                actorId: null,
                request,
                request.CompletedAt,
                cancellationToken).ConfigureAwait(false);

            if (replay)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SqliteWorkflowResult(MetadataWorkflowNames.CompleteReplica, "replayed", Replayed: true, []);
            }

            var rows = await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE replicas
                SET state = 'healthy',
                    byte_count = @stored_bytes,
                    hash_confirmed = 1,
                    last_verified_at_ms = @completed_at_ms,
                    updated_at_ms = @completed_at_ms
                WHERE replica_id = @replica_id
                  AND version_id = @version_id
                  AND node_id = @node_id
                  AND fencing_token = @fencing_token
                  AND placement_epoch = @placement_epoch
                  AND delete_epoch = @delete_epoch
                  AND state IN ('planned', 'streaming', 'verifying')
                  AND EXISTS (
                      SELECT 1
                      FROM object_versions
                      WHERE object_versions.version_id = replicas.version_id
                        AND object_versions.content_hash = @content_hash
                        AND object_versions.size_bytes = @stored_bytes
                  );
                """,
                cancellationToken,
                ("@replica_id", request.ReplicaId),
                ("@version_id", request.VersionId),
                ("@node_id", request.NodeId),
                ("@fencing_token", request.FencingToken),
                ("@placement_epoch", request.PlacementEpoch),
                ("@delete_epoch", request.DeleteEpoch),
                ("@content_hash", request.ContentHash),
                ("@stored_bytes", request.StoredBytes),
                ("@completed_at_ms", ToUnixMs(request.CompletedAt))).ConfigureAwait(false);

            if (rows != 1)
            {
                throw new InvalidOperationException("Replica completion did not match an active planned replica with the supplied fencing token and epochs.");
            }

            await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE capacity_reservations
                SET state = 'finalizing',
                    committed_at_ms = @completed_at_ms
                WHERE replica_id = @replica_id
                  AND version_id = @version_id
                  AND node_id = @node_id
                  AND fencing_token = @fencing_token
                  AND placement_epoch = @placement_epoch
                  AND delete_epoch = @delete_epoch
                  AND state IN ('reserved', 'streaming', 'finalizing');
                """,
                cancellationToken,
                ("@replica_id", request.ReplicaId),
                ("@version_id", request.VersionId),
                ("@node_id", request.NodeId),
                ("@fencing_token", request.FencingToken),
                ("@placement_epoch", request.PlacementEpoch),
                ("@delete_epoch", request.DeleteEpoch),
                ("@completed_at_ms", ToUnixMs(request.CompletedAt))).ConfigureAwait(false);

            await AppendAuditAsync(
                db,
                transaction,
                MetadataWorkflowNames.CompleteReplica,
                "allowed",
                actorId: null,
                objectId: request.ObjectId,
                versionId: request.VersionId,
                request.IdempotencyKey,
                request.CompletedAt,
                cancellationToken).ConfigureAwait(false);

            await CompleteIdempotencyAsync(db, transaction, request.IdempotencyKey, request.CompletedAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWorkflowResult(MetadataWorkflowNames.CompleteReplica, "healthy", Replayed: false, []);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteWorkflowResult> CommitVersionAsync(
        IDbConnection connection,
        SqliteCommitVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCommitVersion(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replay = await TryBeginIdempotencyAsync(
                db,
                transaction,
                request.IdempotencyKey,
                request.TenantId,
                request.DatasetId,
                MetadataWorkflowNames.CommitVersion,
                request.ActorId,
                request,
                request.CommittedAt,
                cancellationToken).ConfigureAwait(false);

            if (replay)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SqliteWorkflowResult(MetadataWorkflowNames.CommitVersion, "replayed", Replayed: true, []);
            }

            var requiredReplicas = await ScalarLongAsync(
                db,
                transaction,
                "SELECT required_replica_count FROM object_versions WHERE version_id = @version_id AND object_id = @object_id AND state = 'writing';",
                cancellationToken,
                ("@version_id", request.VersionId),
                ("@object_id", request.ObjectId)).ConfigureAwait(false);
            if (requiredReplicas is null)
            {
                throw new InvalidOperationException("Writable object version was not found.");
            }

            var healthyReplicas = await ScalarLongAsync(
                db,
                transaction,
                "SELECT COUNT(*) FROM replicas WHERE version_id = @version_id AND state = 'healthy';",
                cancellationToken,
                ("@version_id", request.VersionId)).ConfigureAwait(false);
            if (healthyReplicas < requiredReplicas)
            {
                throw new InvalidOperationException($"Version requires {requiredReplicas} healthy replicas but has {healthyReplicas}.");
            }

            await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE object_versions
                SET state = 'committed',
                    committed_at_ms = @committed_at_ms,
                    updated_at_ms = @committed_at_ms
                WHERE version_id = @version_id
                  AND object_id = @object_id
                  AND state = 'writing';
                """,
                cancellationToken,
                ("@version_id", request.VersionId),
                ("@object_id", request.ObjectId),
                ("@committed_at_ms", ToUnixMs(request.CommittedAt))).ConfigureAwait(false);

            await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE objects
                SET current_version_id = @version_id,
                    state = 'active',
                    updated_at_ms = @committed_at_ms
                WHERE object_id = @object_id
                  AND tenant_id = @tenant_id
                  AND dataset_id = @dataset_id;
                """,
                cancellationToken,
                ("@version_id", request.VersionId),
                ("@object_id", request.ObjectId),
                ("@tenant_id", request.TenantId),
                ("@dataset_id", request.DatasetId),
                ("@committed_at_ms", ToUnixMs(request.CommittedAt))).ConfigureAwait(false);

            await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE capacity_reservations
                SET state = 'committed',
                    committed_at_ms = @committed_at_ms
                WHERE version_id = @version_id
                  AND state IN ('reserved', 'streaming', 'finalizing');
                """,
                cancellationToken,
                ("@version_id", request.VersionId),
                ("@committed_at_ms", ToUnixMs(request.CommittedAt))).ConfigureAwait(false);

            await AppendAuditAsync(
                db,
                transaction,
                MetadataWorkflowNames.CommitVersion,
                "allowed",
                request.ActorId,
                request.ObjectId,
                request.VersionId,
                request.IdempotencyKey,
                request.CommittedAt,
                cancellationToken).ConfigureAwait(false);

            await CompleteIdempotencyAsync(db, transaction, request.IdempotencyKey, request.CommittedAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWorkflowResult(MetadataWorkflowNames.CommitVersion, "committed", Replayed: false, ["object.version_committed"]);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteWorkflowResult> CreateDeleteMarkerAsync(
        IDbConnection connection,
        SqliteCreateDeleteMarkerRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateDeleteMarker(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replay = await TryBeginIdempotencyAsync(
                db,
                transaction,
                request.IdempotencyKey,
                request.TenantId,
                request.DatasetId,
                MetadataWorkflowNames.DeleteMarker,
                request.ActorId,
                request,
                request.CreatedAt,
                cancellationToken).ConfigureAwait(false);

            if (replay)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SqliteWorkflowResult(MetadataWorkflowNames.DeleteMarker, "replayed", Replayed: true, []);
            }

            await ExecuteAsync(
                db,
                transaction,
                """
                INSERT INTO objects (
                    object_id,
                    tenant_id,
                    dataset_id,
                    object_lookup_hash,
                    lookup_key_id,
                    current_version_id,
                    state,
                    placement_epoch,
                    delete_epoch,
                    created_at_ms,
                    updated_at_ms
                )
                VALUES (
                    @object_id,
                    @tenant_id,
                    @dataset_id,
                    @object_lookup_hash,
                    @lookup_key_id,
                    NULL,
                    'active',
                    @placement_epoch,
                    @delete_epoch,
                    @now_ms,
                    @now_ms
                )
                ON CONFLICT (object_id) DO UPDATE SET
                    updated_at_ms = excluded.updated_at_ms;
                """,
                cancellationToken,
                ("@object_id", request.ObjectId),
                ("@tenant_id", request.TenantId),
                ("@dataset_id", request.DatasetId),
                ("@object_lookup_hash", request.ObjectLookupHash),
                ("@lookup_key_id", request.LookupKeyId),
                ("@placement_epoch", request.PlacementEpoch),
                ("@delete_epoch", request.DeleteEpoch),
                ("@now_ms", ToUnixMs(request.CreatedAt))).ConfigureAwait(false);

            await ExecuteAsync(
                db,
                transaction,
                """
                INSERT INTO object_versions (
                    version_id,
                    object_id,
                    version_no,
                    state,
                    encryption_alg,
                    data_key_id,
                    placement_epoch,
                    delete_epoch,
                    required_replica_count,
                    created_at_ms,
                    updated_at_ms
                )
                VALUES (
                    @version_id,
                    @object_id,
                    @version_no,
                    'delete_marker',
                    'none',
                    'none',
                    @placement_epoch,
                    @delete_epoch,
                    1,
                    @now_ms,
                    @now_ms
                );
                """,
                cancellationToken,
                ("@version_id", request.DeleteMarkerVersionId),
                ("@object_id", request.ObjectId),
                ("@version_no", request.VersionNo),
                ("@placement_epoch", request.PlacementEpoch),
                ("@delete_epoch", request.DeleteEpoch),
                ("@now_ms", ToUnixMs(request.CreatedAt))).ConfigureAwait(false);

            await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE objects
                SET current_version_id = @version_id,
                    state = 'delete_marker',
                    delete_epoch = @delete_epoch,
                    updated_at_ms = @now_ms
                WHERE object_id = @object_id
                  AND tenant_id = @tenant_id
                  AND dataset_id = @dataset_id;
                """,
                cancellationToken,
                ("@version_id", request.DeleteMarkerVersionId),
                ("@delete_epoch", request.DeleteEpoch),
                ("@now_ms", ToUnixMs(request.CreatedAt)),
                ("@object_id", request.ObjectId),
                ("@tenant_id", request.TenantId),
                ("@dataset_id", request.DatasetId)).ConfigureAwait(false);

            await ExecuteAsync(
                db,
                transaction,
                """
                INSERT INTO tombstones (
                    tombstone_id,
                    object_id,
                    version_id,
                    delete_epoch,
                    reason,
                    retain_until_ms,
                    created_at_ms
                )
                VALUES (
                    @tombstone_id,
                    @object_id,
                    @version_id,
                    @delete_epoch,
                    'delete_marker',
                    @retain_until_ms,
                    @now_ms
                );
                """,
                cancellationToken,
                ("@tombstone_id", $"tombstone-{request.DeleteMarkerVersionId}"),
                ("@object_id", request.ObjectId),
                ("@version_id", request.DeleteMarkerVersionId),
                ("@delete_epoch", request.DeleteEpoch),
                ("@retain_until_ms", ToUnixMs(request.CreatedAt.AddDays(30))),
                ("@now_ms", ToUnixMs(request.CreatedAt))).ConfigureAwait(false);

            await AppendAuditAsync(
                db,
                transaction,
                MetadataWorkflowNames.DeleteMarker,
                "allowed",
                request.ActorId,
                request.ObjectId,
                request.DeleteMarkerVersionId,
                request.IdempotencyKey,
                request.CreatedAt,
                cancellationToken).ConfigureAwait(false);

            await CompleteIdempotencyAsync(db, transaction, request.IdempotencyKey, request.CreatedAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWorkflowResult(MetadataWorkflowNames.DeleteMarker, "delete_marker", Replayed: false, ["object.delete_marker"]);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteWorkflowResult> LeaseRepairAsync(
        IDbConnection connection,
        SqliteLeaseRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseRepair(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replay = await TryBeginIdempotencyAsync(
                db,
                transaction,
                request.IdempotencyKey,
                request.TenantId,
                request.DatasetId,
                MetadataWorkflowNames.LeaseRepair,
                actorId: null,
                request,
                request.LeasedAt,
                cancellationToken).ConfigureAwait(false);

            if (replay)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SqliteWorkflowResult(MetadataWorkflowNames.LeaseRepair, "replayed", Replayed: true, []);
            }

            await ExecuteAsync(
                db,
                transaction,
                """
                INSERT INTO leases (
                    lease_id,
                    resource_type,
                    resource_id,
                    holder_id,
                    state,
                    fencing_token,
                    expires_at_ms,
                    created_at_ms
                )
                VALUES (
                    @lease_id,
                    'repair_job',
                    @job_id,
                    @holder_id,
                    'issued',
                    0,
                    @expires_at_ms,
                    @created_at_ms
                );
                """,
                cancellationToken,
                ("@lease_id", request.LeaseId),
                ("@job_id", request.JobId),
                ("@holder_id", request.HolderNodeId),
                ("@expires_at_ms", ToUnixMs(request.LeasedAt.Add(request.LeaseDuration))),
                ("@created_at_ms", ToUnixMs(request.LeasedAt))).ConfigureAwait(false);

            await ExecuteAsync(
                db,
                transaction,
                """
                INSERT INTO repair_jobs (
                    job_id,
                    version_id,
                    replica_id,
                    kind,
                    priority,
                    state,
                    attempt_count,
                    lease_id,
                    not_before_ms,
                    last_error,
                    idempotency_key,
                    created_at_ms,
                    updated_at_ms
                )
                VALUES (
                    @job_id,
                    @version_id,
                    @replica_id,
                    @kind,
                    @priority,
                    'leased',
                    1,
                    @lease_id,
                    @not_before_ms,
                    NULL,
                    @idempotency_key,
                    @created_at_ms,
                    @created_at_ms
                );
                """,
                cancellationToken,
                ("@job_id", request.JobId),
                ("@version_id", request.VersionId),
                ("@replica_id", request.ReplicaId),
                ("@kind", request.Kind),
                ("@priority", request.Priority),
                ("@lease_id", request.LeaseId),
                ("@not_before_ms", ToUnixMs(request.LeasedAt)),
                ("@idempotency_key", request.IdempotencyKey),
                ("@created_at_ms", ToUnixMs(request.LeasedAt))).ConfigureAwait(false);

            await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE object_versions
                SET state = CASE
                        WHEN state = 'committed' THEN 'under_replicated'
                        ELSE state
                    END,
                    updated_at_ms = @updated_at_ms
                WHERE version_id = @version_id
                  AND object_id = @object_id
                  AND state IN ('committed', 'under_replicated', 'quarantined');
                """,
                cancellationToken,
                ("@version_id", request.VersionId),
                ("@object_id", request.ObjectId),
                ("@updated_at_ms", ToUnixMs(request.LeasedAt))).ConfigureAwait(false);

            await AppendAuditAsync(
                db,
                transaction,
                MetadataWorkflowNames.LeaseRepair,
                "allowed",
                actorId: null,
                request.ObjectId,
                request.VersionId,
                request.IdempotencyKey,
                request.LeasedAt,
                cancellationToken).ConfigureAwait(false);

            await CompleteIdempotencyAsync(db, transaction, request.IdempotencyKey, request.LeasedAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWorkflowResult(MetadataWorkflowNames.LeaseRepair, "leased", Replayed: false, ["repair.leased"]);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteWorkflowResult> ExpireReservationAsync(
        IDbConnection connection,
        SqliteExpireReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateExpireReservation(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replay = await TryBeginIdempotencyAsync(
                db,
                transaction,
                request.IdempotencyKey,
                request.TenantId,
                request.DatasetId,
                MetadataWorkflowNames.ExpireReservation,
                actorId: null,
                request,
                request.ExpiredAt,
                cancellationToken).ConfigureAwait(false);

            if (replay)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SqliteWorkflowResult(MetadataWorkflowNames.ExpireReservation, "replayed", Replayed: true, []);
            }

            var rows = await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE capacity_reservations
                SET state = 'expired'
                WHERE reservation_id = @reservation_id
                  AND tenant_id = @tenant_id
                  AND dataset_id = @dataset_id
                  AND object_id = @object_id
                  AND version_id = @version_id
                  AND expires_at_ms <= @expired_at_ms
                  AND state IN ('pending', 'reserved', 'streaming', 'finalizing');
                """,
                cancellationToken,
                ("@reservation_id", request.ReservationId),
                ("@tenant_id", request.TenantId),
                ("@dataset_id", request.DatasetId),
                ("@object_id", request.ObjectId),
                ("@version_id", request.VersionId),
                ("@expired_at_ms", ToUnixMs(request.ExpiredAt))).ConfigureAwait(false);

            if (rows != 1)
            {
                throw new InvalidOperationException("Reservation was not found in an expired, expirable state.");
            }

            await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE replicas
                SET state = 'stale',
                    updated_at_ms = @expired_at_ms
                WHERE replica_id = (
                    SELECT replica_id
                    FROM capacity_reservations
                    WHERE reservation_id = @reservation_id
                )
                  AND state IN ('planned', 'streaming', 'verifying');
                """,
                cancellationToken,
                ("@reservation_id", request.ReservationId),
                ("@expired_at_ms", ToUnixMs(request.ExpiredAt))).ConfigureAwait(false);

            await AppendAuditAsync(
                db,
                transaction,
                MetadataWorkflowNames.ExpireReservation,
                "allowed",
                actorId: null,
                request.ObjectId,
                request.VersionId,
                request.IdempotencyKey,
                request.ExpiredAt,
                cancellationToken).ConfigureAwait(false);

            await CompleteIdempotencyAsync(db, transaction, request.IdempotencyKey, request.ExpiredAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWorkflowResult(MetadataWorkflowNames.ExpireReservation, "expired", Replayed: false, ["reservation.expired"]);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteWorkflowResult> CleanupConversionAsync(
        IDbConnection connection,
        SqliteCleanupConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCleanupConversion(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replay = await TryBeginIdempotencyAsync(
                db,
                transaction,
                request.IdempotencyKey,
                request.TenantId,
                request.DatasetId,
                MetadataWorkflowNames.CleanupConversion,
                actorId: null,
                request,
                request.ConvertedAt,
                cancellationToken).ConfigureAwait(false);

            if (replay)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SqliteWorkflowResult(MetadataWorkflowNames.CleanupConversion, "replayed", Replayed: true, []);
            }

            var nextReservationState = request.RequiresCleanup ? "failed_cleanup_required" : "aborted";
            var nextReplicaState = request.RequiresCleanup ? "delete_pending" : "stale";

            var rows = await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE capacity_reservations
                SET state = @state,
                    cleanup_required_at_ms = @converted_at_ms
                WHERE reservation_id = @reservation_id
                  AND tenant_id = @tenant_id
                  AND dataset_id = @dataset_id
                  AND object_id = @object_id
                  AND version_id = @version_id
                  AND state IN ('expired', 'aborted', 'failed_cleanup_required');
                """,
                cancellationToken,
                ("@state", nextReservationState),
                ("@converted_at_ms", ToUnixMs(request.ConvertedAt)),
                ("@reservation_id", request.ReservationId),
                ("@tenant_id", request.TenantId),
                ("@dataset_id", request.DatasetId),
                ("@object_id", request.ObjectId),
                ("@version_id", request.VersionId)).ConfigureAwait(false);

            if (rows != 1)
            {
                throw new InvalidOperationException("Reservation was not found in a cleanup-convertible state.");
            }

            await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE replicas
                SET state = @state,
                    updated_at_ms = @converted_at_ms
                WHERE replica_id = @replica_id
                  AND version_id = @version_id
                  AND state IN ('planned', 'streaming', 'verifying', 'stale', 'delete_pending');
                """,
                cancellationToken,
                ("@state", nextReplicaState),
                ("@converted_at_ms", ToUnixMs(request.ConvertedAt)),
                ("@replica_id", request.ReplicaId),
                ("@version_id", request.VersionId)).ConfigureAwait(false);

            await AppendAuditAsync(
                db,
                transaction,
                MetadataWorkflowNames.CleanupConversion,
                "allowed",
                actorId: null,
                request.ObjectId,
                request.VersionId,
                request.IdempotencyKey,
                request.ConvertedAt,
                cancellationToken).ConfigureAwait(false);

            await CompleteIdempotencyAsync(db, transaction, request.IdempotencyKey, request.ConvertedAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWorkflowResult(MetadataWorkflowNames.CleanupConversion, nextReservationState, Replayed: false, ["reservation.cleanup_converted"]);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteWorkflowResult> RecordCapacityReportAsync(
        IDbConnection connection,
        SqliteCapacityReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCapacityReport(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replay = await TryBeginIdempotencyAsync(
                db,
                transaction,
                request.IdempotencyKey,
                tenantId: null,
                datasetId: null,
                MetadataWorkflowNames.CapacityReport,
                actorId: null,
                request,
                request.ObservedAt,
                cancellationToken).ConfigureAwait(false);

            if (replay)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SqliteWorkflowResult(MetadataWorkflowNames.CapacityReport, "replayed", Replayed: true, []);
            }

            var nodeExists = await ScalarLongAsync(
                db,
                transaction,
                "SELECT COUNT(*) FROM nodes WHERE node_id = @node_id;",
                cancellationToken,
                ("@node_id", request.NodeId)).ConfigureAwait(false);
            if (nodeExists != 1)
            {
                throw new InvalidOperationException("Capacity report node was not found.");
            }

            await ExecuteAsync(
                db,
                transaction,
                """
                INSERT INTO capacity_reports (
                    node_id,
                    capacity_pressure,
                    capacity_bytes,
                    used_bytes,
                    reserved_bytes,
                    free_bytes,
                    observed_at_ms,
                    raw_report
                )
                VALUES (
                    @node_id,
                    @capacity_pressure,
                    @capacity_bytes,
                    @used_bytes,
                    @reserved_bytes,
                    @free_bytes,
                    @observed_at_ms,
                    @raw_report
                );
                """,
                cancellationToken,
                ("@node_id", request.NodeId),
                ("@capacity_pressure", request.CapacityPressure),
                ("@capacity_bytes", request.CapacityBytes),
                ("@used_bytes", request.UsedBytes),
                ("@reserved_bytes", request.ReservedBytes),
                ("@free_bytes", request.FreeBytes),
                ("@observed_at_ms", ToUnixMs(request.ObservedAt)),
                ("@raw_report", request.RawReport)).ConfigureAwait(false);

            var nodeRows = await ExecuteAsync(
                db,
                transaction,
                """
                UPDATE nodes
                SET capacity_pressure = @capacity_pressure,
                    capacity_bytes = @capacity_bytes,
                    used_bytes = @used_bytes,
                    reserved_bytes = @reserved_bytes,
                    free_bytes = @free_bytes,
                    last_seen_at_ms = @observed_at_ms
                WHERE node_id = @node_id;
                """,
                cancellationToken,
                ("@node_id", request.NodeId),
                ("@capacity_pressure", request.CapacityPressure),
                ("@capacity_bytes", request.CapacityBytes),
                ("@used_bytes", request.UsedBytes),
                ("@reserved_bytes", request.ReservedBytes),
                ("@free_bytes", request.FreeBytes),
                ("@observed_at_ms", ToUnixMs(request.ObservedAt))).ConfigureAwait(false);

            if (nodeRows != 1)
            {
                throw new InvalidOperationException("Capacity report node was not found.");
            }

            await AppendNodeAuditAsync(
                db,
                transaction,
                MetadataWorkflowNames.CapacityReport,
                "allowed",
                request.NodeId,
                request.IdempotencyKey,
                request.ObservedAt,
                cancellationToken).ConfigureAwait(false);

            await CompleteIdempotencyAsync(db, transaction, request.IdempotencyKey, request.ObservedAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWorkflowResult(MetadataWorkflowNames.CapacityReport, request.CapacityPressure, Replayed: false, ["capacity.reported"]);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteClaimOutboxResult> ClaimOutboxAsync(
        IDbConnection connection,
        SqliteClaimOutboxRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateClaimOutbox(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var claimedAtMs = ToUnixMs(request.ClaimedAt);
            var claimedUntil = request.ClaimedAt.Add(request.ClaimDuration);
            var claimedUntilMs = ToUnixMs(claimedUntil);
            var events = await ClaimOutboxRowsAsync(
                db,
                transaction,
                request,
                claimedAtMs,
                claimedUntil,
                claimedUntilMs,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteClaimOutboxResult(
                new SqliteWorkflowResult(
                    MetadataWorkflowNames.ClaimOutbox,
                    events.Count == 0 ? "empty" : "claimed",
                    Replayed: false,
                    events.Select(static item => item.Topic).Distinct(StringComparer.Ordinal).ToArray()),
                events);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteWorkflowResult> EvaluateRecoveryGateAsync(
        IDbConnection connection,
        SqliteEvaluateRecoveryGateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateEvaluateRecoveryGate(request);
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replay = await TryBeginIdempotencyAsync(
                db,
                transaction,
                request.IdempotencyKey,
                tenantId: null,
                datasetId: null,
                MetadataWorkflowNames.EvaluateRecoveryGate,
                request.ActorId,
                request,
                request.EvaluatedAt,
                cancellationToken).ConfigureAwait(false);

            if (replay)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SqliteWorkflowResult(MetadataWorkflowNames.EvaluateRecoveryGate, "replayed", Replayed: true, []);
            }

            var evaluatedAtMs = ToUnixMs(request.EvaluatedAt);
            var state = request.IsOpen ? "open" : "closed";
            var openedAtMs = request.IsOpen ? evaluatedAtMs : (long?)null;
            var closedAtMs = request.IsOpen ? (long?)null : evaluatedAtMs;
            var outboxTopic = request.IsOpen ? "recovery_gate.opened" : "recovery_gate.closed";

            await ExecuteAsync(
                db,
                transaction,
                """
                INSERT INTO recovery_gates (
                    gate_id,
                    name,
                    state,
                    severity,
                    reason,
                    required_approvals,
                    approvals,
                    blocks_json,
                    allowed_actions_json,
                    opened_at_ms,
                    closed_at_ms,
                    evaluated_at_ms,
                    updated_at_ms,
                    idempotency_key
                )
                VALUES (
                    @gate_id,
                    @name,
                    @state,
                    @severity,
                    @reason,
                    @required_approvals,
                    CASE WHEN @state = 'closed' THEN @required_approvals ELSE 0 END,
                    @blocks_json,
                    @allowed_actions_json,
                    @opened_at_ms,
                    @closed_at_ms,
                    @evaluated_at_ms,
                    @evaluated_at_ms,
                    @idempotency_key
                )
                ON CONFLICT (gate_id) DO UPDATE SET
                    name = excluded.name,
                    state = excluded.state,
                    severity = excluded.severity,
                    reason = excluded.reason,
                    required_approvals = excluded.required_approvals,
                    approvals = CASE
                        WHEN excluded.state = 'closed' THEN excluded.required_approvals
                        ELSE MIN(recovery_gates.approvals, excluded.required_approvals)
                    END,
                    blocks_json = excluded.blocks_json,
                    allowed_actions_json = excluded.allowed_actions_json,
                    opened_at_ms = CASE
                        WHEN excluded.state = 'open' AND recovery_gates.state = 'closed' THEN excluded.opened_at_ms
                        ELSE COALESCE(recovery_gates.opened_at_ms, excluded.opened_at_ms)
                    END,
                    closed_at_ms = excluded.closed_at_ms,
                    evaluated_at_ms = excluded.evaluated_at_ms,
                    updated_at_ms = excluded.updated_at_ms,
                    idempotency_key = excluded.idempotency_key;
                """,
                cancellationToken,
                ("@gate_id", request.GateId),
                ("@name", request.Name),
                ("@state", state),
                ("@severity", request.Severity),
                ("@reason", request.Reason),
                ("@required_approvals", request.RequiredApprovals),
                ("@blocks_json", SerializeStringList(request.Blocks)),
                ("@allowed_actions_json", SerializeStringList(request.AllowedActions)),
                ("@opened_at_ms", openedAtMs),
                ("@closed_at_ms", closedAtMs),
                ("@evaluated_at_ms", evaluatedAtMs),
                ("@idempotency_key", request.IdempotencyKey)).ConfigureAwait(false);

            await ExecuteAsync(
                db,
                transaction,
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
                    @workflow,
                    NULL,
                    @topic,
                    @payload,
                    @idempotency_key,
                    @available_at_ms,
                    @created_at_ms
                );
                """,
                cancellationToken,
                ("@outbox_id", $"recovery-gate-{request.IdempotencyKey}"),
                ("@workflow", MetadataWorkflowNames.EvaluateRecoveryGate),
                ("@topic", outboxTopic),
                ("@payload", BuildRecoveryGatePayload(request, state)),
                ("@idempotency_key", request.IdempotencyKey),
                ("@available_at_ms", evaluatedAtMs),
                ("@created_at_ms", evaluatedAtMs)).ConfigureAwait(false);

            await AppendAuditAsync(
                db,
                transaction,
                MetadataWorkflowNames.EvaluateRecoveryGate,
                "allowed",
                request.ActorId,
                objectId: null,
                versionId: null,
                request.IdempotencyKey,
                request.EvaluatedAt,
                cancellationToken).ConfigureAwait(false);

            await CompleteIdempotencyAsync(db, transaction, request.IdempotencyKey, request.EvaluatedAt, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWorkflowResult(MetadataWorkflowNames.EvaluateRecoveryGate, state, Replayed: false, [outboxTopic]);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IReadOnlyList<SqliteClaimedOutboxEvent>> ClaimOutboxRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqliteClaimOutboxRequest request,
        long claimedAtMs,
        DateTimeOffset claimedUntil,
        long claimedUntilMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE outbox_events
            SET claimed_by = @claimed_by,
                claimed_until_ms = @claimed_until_ms,
                attempt_count = attempt_count + 1
            WHERE outbox_id IN (
                SELECT outbox_id
                FROM outbox_events
                WHERE delivered_at_ms IS NULL
                  AND available_at_ms <= @claimed_at_ms
                  AND (claimed_until_ms IS NULL OR claimed_until_ms <= @claimed_at_ms)
                  AND (
                      @destination_node_id IS NULL
                      OR destination_node_id IS NULL
                      OR destination_node_id = @destination_node_id
                  )
                  AND (@topic IS NULL OR topic = @topic)
                ORDER BY available_at_ms, created_at_ms, outbox_id
                LIMIT @max_items
            )
            RETURNING
                outbox_id,
                workflow,
                destination_node_id,
                topic,
                payload,
                idempotency_key,
                attempt_count,
                available_at_ms,
                created_at_ms;
            """;
        AddParameters(
            command,
            ("@claimed_by", request.ClaimedBy),
            ("@claimed_until_ms", claimedUntilMs),
            ("@claimed_at_ms", claimedAtMs),
            ("@destination_node_id", request.DestinationNodeId),
            ("@topic", request.Topic),
            ("@max_items", request.MaxItems));

        var events = new List<SqliteClaimedOutboxEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new SqliteClaimedOutboxEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                (byte[])reader.GetValue(4),
                reader.GetString(5),
                reader.GetInt32(6),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                claimedUntil,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
        }

        return events;
    }

    private static async Task<bool> TryBeginIdempotencyAsync(
        DbConnection connection,
        DbTransaction transaction,
        string idempotencyKey,
        string? tenantId,
        string? datasetId,
        string workflow,
        string? actorId,
        object request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await ScalarStringAsync(
            connection,
            transaction,
            "SELECT result_state FROM idempotency_records WHERE idempotency_key = @idempotency_key;",
            cancellationToken,
            ("@idempotency_key", idempotencyKey)).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing == "completed";
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO idempotency_records (
                idempotency_key,
                tenant_id,
                dataset_id,
                workflow,
                actor_id,
                request_hash,
                result_state,
                created_at_ms
            )
            VALUES (
                @idempotency_key,
                @tenant_id,
                @dataset_id,
                @workflow,
                @actor_id,
                @request_hash,
                'started',
                @created_at_ms
            );
            """,
            cancellationToken,
            ("@idempotency_key", idempotencyKey),
            ("@tenant_id", tenantId),
            ("@dataset_id", datasetId),
            ("@workflow", workflow),
            ("@actor_id", actorId),
            ("@request_hash", HashRequest(request)),
            ("@created_at_ms", ToUnixMs(now))).ConfigureAwait(false);

        return false;
    }

    private static Task CompleteIdempotencyAsync(
        DbConnection connection,
        DbTransaction transaction,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE idempotency_records
            SET result_state = 'completed',
                completed_at_ms = @completed_at_ms
            WHERE idempotency_key = @idempotency_key;
            """,
            cancellationToken,
            ("@idempotency_key", idempotencyKey),
            ("@completed_at_ms", ToUnixMs(now)));

    private static Task AppendAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        string workflow,
        string decision,
        string? actorId,
        string? objectId,
        string? versionId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO audit_events (
                workflow,
                decision,
                actor_id,
                object_id,
                version_id,
                idempotency_key,
                occurred_at_ms
            )
            VALUES (
                @workflow,
                @decision,
                @actor_id,
                @object_id,
                @version_id,
                @idempotency_key,
                @occurred_at_ms
            );
            """,
            cancellationToken,
            ("@workflow", workflow),
            ("@decision", decision),
            ("@actor_id", actorId),
            ("@object_id", objectId),
            ("@version_id", versionId),
            ("@idempotency_key", idempotencyKey),
            ("@occurred_at_ms", ToUnixMs(occurredAt)));

    private static Task AppendNodeAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        string workflow,
        string decision,
        string nodeId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO audit_events (
                workflow,
                decision,
                node_id,
                idempotency_key,
                occurred_at_ms
            )
            VALUES (
                @workflow,
                @decision,
                @node_id,
                @idempotency_key,
                @occurred_at_ms
            );
            """,
            cancellationToken,
            ("@workflow", workflow),
            ("@decision", decision),
            ("@node_id", nodeId),
            ("@idempotency_key", idempotencyKey),
            ("@occurred_at_ms", ToUnixMs(occurredAt)));

    private static async Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long?> ScalarLongAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var value = await ScalarAsync(connection, transaction, sql, cancellationToken, parameters).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task<string?> ScalarStringAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var value = await ScalarAsync(connection, transaction, sql, cancellationToken, parameters).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(DbCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    private static DbConnection RequireDbConnection(IDbConnection connection) =>
        connection as DbConnection
        ?? throw new ArgumentException("Workflow store requires a DbConnection.", nameof(connection));

    private static async Task EnsureOpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static byte[] HashRequest(object request) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(request.ToString() ?? string.Empty));

    private static string SerializeStringList(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values);

    private static byte[] BuildRecoveryGatePayload(SqliteEvaluateRecoveryGateRequest request, string state) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            gate_id = request.GateId,
            state,
            severity = request.Severity,
            evaluated_at_ms = ToUnixMs(request.EvaluatedAt),
        }));

    private static void ValidateCreateWriteIntent(SqliteCreateWriteIntentRequest request)
    {
        RequireText(request.TenantId, nameof(request.TenantId));
        RequireText(request.DatasetId, nameof(request.DatasetId));
        RequireText(request.ObjectId, nameof(request.ObjectId));
        RequireBytes(request.ObjectLookupHash, nameof(request.ObjectLookupHash));
        RequireText(request.LookupKeyId, nameof(request.LookupKeyId));
        RequireText(request.VersionId, nameof(request.VersionId));
        RequireText(request.ActorId, nameof(request.ActorId));
        RequireBytes(request.ContentHash, nameof(request.ContentHash));
        RequirePositive(request.SizeBytes, nameof(request.SizeBytes));
        RequireText(request.EncryptionAlg, nameof(request.EncryptionAlg));
        RequireText(request.DataKeyId, nameof(request.DataKeyId));
        RequirePositive(request.RequiredReplicaCount, nameof(request.RequiredReplicaCount));
        RequirePositive(request.PlacementEpoch, nameof(request.PlacementEpoch));
        RequireNonNegative(request.DeleteEpoch, nameof(request.DeleteEpoch));
        RequirePositive(request.ReservationTtl, nameof(request.ReservationTtl));
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.Replicas.Count < request.RequiredReplicaCount)
        {
            throw new ArgumentException("At least required replica count reservations must be supplied.", nameof(request));
        }
    }

    private static void ValidateCompleteReplica(SqliteCompleteReplicaRequest request)
    {
        RequireText(request.TenantId, nameof(request.TenantId));
        RequireText(request.DatasetId, nameof(request.DatasetId));
        RequireText(request.ObjectId, nameof(request.ObjectId));
        RequireText(request.VersionId, nameof(request.VersionId));
        RequireText(request.ReplicaId, nameof(request.ReplicaId));
        RequireText(request.NodeId, nameof(request.NodeId));
        RequireBytes(request.ContentHash, nameof(request.ContentHash));
        RequirePositive(request.StoredBytes, nameof(request.StoredBytes));
        RequireNonNegative(request.FencingToken, nameof(request.FencingToken));
        RequirePositive(request.PlacementEpoch, nameof(request.PlacementEpoch));
        RequireNonNegative(request.DeleteEpoch, nameof(request.DeleteEpoch));
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey));
    }

    private static void ValidateCommitVersion(SqliteCommitVersionRequest request)
    {
        RequireText(request.TenantId, nameof(request.TenantId));
        RequireText(request.DatasetId, nameof(request.DatasetId));
        RequireText(request.ObjectId, nameof(request.ObjectId));
        RequireText(request.VersionId, nameof(request.VersionId));
        RequireText(request.ActorId, nameof(request.ActorId));
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey));
    }

    private static void ValidateDeleteMarker(SqliteCreateDeleteMarkerRequest request)
    {
        RequireText(request.TenantId, nameof(request.TenantId));
        RequireText(request.DatasetId, nameof(request.DatasetId));
        RequireText(request.ObjectId, nameof(request.ObjectId));
        RequireBytes(request.ObjectLookupHash, nameof(request.ObjectLookupHash));
        RequireText(request.LookupKeyId, nameof(request.LookupKeyId));
        RequireText(request.DeleteMarkerVersionId, nameof(request.DeleteMarkerVersionId));
        RequireText(request.ActorId, nameof(request.ActorId));
        RequirePositive(request.PlacementEpoch, nameof(request.PlacementEpoch));
        RequireNonNegative(request.DeleteEpoch, nameof(request.DeleteEpoch));
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey));
    }

    private static void ValidateLeaseRepair(SqliteLeaseRepairRequest request)
    {
        RequireText(request.TenantId, nameof(request.TenantId));
        RequireText(request.DatasetId, nameof(request.DatasetId));
        RequireText(request.ObjectId, nameof(request.ObjectId));
        RequireText(request.VersionId, nameof(request.VersionId));
        RequireText(request.JobId, nameof(request.JobId));
        RequireText(request.LeaseId, nameof(request.LeaseId));
        RequireText(request.HolderNodeId, nameof(request.HolderNodeId));
        RequireText(request.Kind, nameof(request.Kind));
        RequireText(request.Reason, nameof(request.Reason));
        RequirePositive(request.LeaseDuration, nameof(request.LeaseDuration));
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey));
    }

    private static void ValidateExpireReservation(SqliteExpireReservationRequest request)
    {
        RequireText(request.TenantId, nameof(request.TenantId));
        RequireText(request.DatasetId, nameof(request.DatasetId));
        RequireText(request.ObjectId, nameof(request.ObjectId));
        RequireText(request.VersionId, nameof(request.VersionId));
        RequireText(request.ReservationId, nameof(request.ReservationId));
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey));
    }

    private static void ValidateCleanupConversion(SqliteCleanupConversionRequest request)
    {
        RequireText(request.TenantId, nameof(request.TenantId));
        RequireText(request.DatasetId, nameof(request.DatasetId));
        RequireText(request.ObjectId, nameof(request.ObjectId));
        RequireText(request.VersionId, nameof(request.VersionId));
        RequireText(request.ReservationId, nameof(request.ReservationId));
        RequireText(request.ReplicaId, nameof(request.ReplicaId));
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey));
    }

    private static void ValidateCapacityReport(SqliteCapacityReportRequest request)
    {
        RequireText(request.NodeId, nameof(request.NodeId));
        RequireText(request.CapacityPressure, nameof(request.CapacityPressure));
        RequireNonNegative(request.CapacityBytes, nameof(request.CapacityBytes));
        RequireNonNegative(request.UsedBytes, nameof(request.UsedBytes));
        RequireNonNegative(request.ReservedBytes, nameof(request.ReservedBytes));
        RequireNonNegative(request.FreeBytes, nameof(request.FreeBytes));
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey));
        RequireKnownLabel(Labels.CapacityPressureStates, request.CapacityPressure, nameof(request.CapacityPressure));
        RequireCapacityAccounting(request);
    }

    private static void ValidateClaimOutbox(SqliteClaimOutboxRequest request)
    {
        RequireText(request.ClaimedBy, nameof(request.ClaimedBy));
        RequirePositive(request.ClaimDuration, nameof(request.ClaimDuration));
        RequirePositive(request.MaxItems, nameof(request.MaxItems));
        if (request.DestinationNodeId is not null)
        {
            RequireText(request.DestinationNodeId, nameof(request.DestinationNodeId));
        }

        if (request.Topic is not null)
        {
            RequireText(request.Topic, nameof(request.Topic));
        }
    }

    private static void ValidateEvaluateRecoveryGate(SqliteEvaluateRecoveryGateRequest request)
    {
        RequireText(request.GateId, nameof(request.GateId));
        RequireText(request.Name, nameof(request.Name));
        RequireText(request.Severity, nameof(request.Severity));
        RequireText(request.Reason, nameof(request.Reason));
        RequirePositive(request.RequiredApprovals, nameof(request.RequiredApprovals));
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey));
        RequireOneOf(request.Severity, nameof(request.Severity), "info", "warning", "critical");
        RequireStringItems(request.Blocks, nameof(request.Blocks));
        RequireStringItems(request.AllowedActions, nameof(request.AllowedActions));
        if (request.ActorId is not null)
        {
            RequireText(request.ActorId, nameof(request.ActorId));
        }
    }

    private static void RequireOneOf(string value, string name, params string[] allowed)
    {
        if (!allowed.Any(item => string.Equals(item, value, StringComparison.Ordinal)))
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be one of: {string.Join(", ", allowed)}.");
        }
    }

    private static void RequireStringItems(IReadOnlyList<string> values, string name)
    {
        foreach (var value in values)
        {
            RequireText(value, name);
        }
    }

    private static void RequireKnownLabel(IReadOnlyList<LabelSpec> labels, string value, string name)
    {
        if (!labels.Any(label => string.Equals(label.Wire, value, StringComparison.Ordinal)))
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be a known label value.");
        }
    }

    private static void RequireCapacityAccounting(SqliteCapacityReportRequest request)
    {
        var allocatedBytes = checked(request.UsedBytes + request.ReservedBytes);
        if (allocatedBytes > request.CapacityBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.UsedBytes),
                allocatedBytes,
                "used plus reserved bytes must not exceed capacity bytes.");
        }

        if (request.FreeBytes > request.CapacityBytes - allocatedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.FreeBytes),
                request.FreeBytes,
                "free bytes must fit within capacity after used and reserved bytes.");
        }
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }
    }

    private static void RequireBytes(byte[] value, string name)
    {
        if (value.Length == 0)
        {
            throw new ArgumentException($"{name} is required.", name);
        }
    }

    private static void RequirePositive(long value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive.");
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive.");
        }
    }

    private static void RequireNonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be non-negative.");
        }
    }
}
