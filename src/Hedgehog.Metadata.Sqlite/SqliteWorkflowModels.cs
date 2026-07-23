using System.Data;

namespace Hedgehog.Metadata.Sqlite;

public sealed record SqliteReplicaReservation(
    string ReservationId,
    string ReplicaId,
    string NodeId,
    long BytesReserved,
    long FencingToken);

public sealed record SqliteCreateWriteIntentRequest(
    string TenantId,
    string DatasetId,
    string ObjectId,
    byte[] ObjectLookupHash,
    string LookupKeyId,
    string VersionId,
    long VersionNo,
    string ActorId,
    byte[] ContentHash,
    long SizeBytes,
    string EncryptionAlg,
    string DataKeyId,
    int RequiredReplicaCount,
    long PlacementEpoch,
    long DeleteEpoch,
    DateTimeOffset RequestedAt,
    TimeSpan ReservationTtl,
    string IdempotencyKey,
    IReadOnlyList<SqliteReplicaReservation> Replicas);

public sealed record SqliteCompleteReplicaRequest(
    string TenantId,
    string DatasetId,
    string ObjectId,
    string VersionId,
    string ReplicaId,
    string NodeId,
    byte[] ContentHash,
    long StoredBytes,
    long FencingToken,
    long PlacementEpoch,
    long DeleteEpoch,
    DateTimeOffset CompletedAt,
    string IdempotencyKey);

public sealed record SqliteCommitVersionRequest(
    string TenantId,
    string DatasetId,
    string ObjectId,
    string VersionId,
    string ActorId,
    DateTimeOffset CommittedAt,
    string IdempotencyKey);

public sealed record SqliteCreateDeleteMarkerRequest(
    string TenantId,
    string DatasetId,
    string ObjectId,
    byte[] ObjectLookupHash,
    string LookupKeyId,
    string DeleteMarkerVersionId,
    long VersionNo,
    string ActorId,
    long PlacementEpoch,
    long DeleteEpoch,
    DateTimeOffset CreatedAt,
    string IdempotencyKey);

public sealed record SqliteLeaseRepairRequest(
    string TenantId,
    string DatasetId,
    string ObjectId,
    string VersionId,
    string JobId,
    string? ReplicaId,
    string LeaseId,
    string HolderNodeId,
    string Kind,
    int Priority,
    string Reason,
    DateTimeOffset LeasedAt,
    TimeSpan LeaseDuration,
    string IdempotencyKey);

public sealed record SqliteExpireReservationRequest(
    string TenantId,
    string DatasetId,
    string ObjectId,
    string VersionId,
    string ReservationId,
    DateTimeOffset ExpiredAt,
    string IdempotencyKey);

public sealed record SqliteCleanupConversionRequest(
    string TenantId,
    string DatasetId,
    string ObjectId,
    string VersionId,
    string ReservationId,
    string ReplicaId,
    DateTimeOffset ConvertedAt,
    bool RequiresCleanup,
    string IdempotencyKey);

public sealed record SqliteCapacityReportRequest(
    string NodeId,
    string CapacityPressure,
    long CapacityBytes,
    long UsedBytes,
    long ReservedBytes,
    long FreeBytes,
    DateTimeOffset ObservedAt,
    string IdempotencyKey,
    byte[]? RawReport = null);

public sealed record SqliteAcceptInviteRequest(
    string TenantId,
    string InvitationId,
    string AcceptedEntityKind,
    string EntityId,
    string DisplayName,
    string PublicKeyFingerprint,
    DateTimeOffset AcceptedAt,
    string IdempotencyKey,
    string? ActorKind = null,
    string? NodeKeyId = null,
    string? AdvertiseEndpoint = null,
    string? TrustDomain = null);

public sealed record SqliteClaimOutboxRequest(
    string ClaimedBy,
    DateTimeOffset ClaimedAt,
    TimeSpan ClaimDuration,
    int MaxItems,
    string? DestinationNodeId = null,
    string? Topic = null);

public sealed record SqliteClaimedOutboxEvent(
    string OutboxId,
    string Workflow,
    string? DestinationNodeId,
    string Topic,
    byte[] Payload,
    string IdempotencyKey,
    int AttemptCount,
    DateTimeOffset AvailableAt,
    DateTimeOffset ClaimedUntil,
    DateTimeOffset CreatedAt);

public sealed record SqliteWorkflowResult(
    string Workflow,
    string State,
    bool Replayed,
    IReadOnlyList<string> OutboxTopics);

public sealed record SqliteClaimOutboxResult(
    SqliteWorkflowResult WorkflowResult,
    IReadOnlyList<SqliteClaimedOutboxEvent> Events);

public interface ISqliteMetadataWorkflowStore
{
    Task<SqliteWorkflowResult> CreateWriteIntentAsync(
        IDbConnection connection,
        SqliteCreateWriteIntentRequest request,
        CancellationToken cancellationToken = default);

    Task<SqliteWorkflowResult> CompleteReplicaAsync(
        IDbConnection connection,
        SqliteCompleteReplicaRequest request,
        CancellationToken cancellationToken = default);

    Task<SqliteWorkflowResult> CommitVersionAsync(
        IDbConnection connection,
        SqliteCommitVersionRequest request,
        CancellationToken cancellationToken = default);

    Task<SqliteWorkflowResult> CreateDeleteMarkerAsync(
        IDbConnection connection,
        SqliteCreateDeleteMarkerRequest request,
        CancellationToken cancellationToken = default);

    Task<SqliteWorkflowResult> LeaseRepairAsync(
        IDbConnection connection,
        SqliteLeaseRepairRequest request,
        CancellationToken cancellationToken = default);

    Task<SqliteWorkflowResult> ExpireReservationAsync(
        IDbConnection connection,
        SqliteExpireReservationRequest request,
        CancellationToken cancellationToken = default);

    Task<SqliteWorkflowResult> CleanupConversionAsync(
        IDbConnection connection,
        SqliteCleanupConversionRequest request,
        CancellationToken cancellationToken = default);

    Task<SqliteWorkflowResult> RecordCapacityReportAsync(
        IDbConnection connection,
        SqliteCapacityReportRequest request,
        CancellationToken cancellationToken = default);

    Task<SqliteWorkflowResult> AcceptInviteAsync(
        IDbConnection connection,
        SqliteAcceptInviteRequest request,
        CancellationToken cancellationToken = default);

    Task<SqliteClaimOutboxResult> ClaimOutboxAsync(
        IDbConnection connection,
        SqliteClaimOutboxRequest request,
        CancellationToken cancellationToken = default);
}
