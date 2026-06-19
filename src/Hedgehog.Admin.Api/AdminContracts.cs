namespace Hedgehog.Admin.Api;

public sealed record ActionRequestDto(
    string ActorId,
    string Reason,
    string? RequestId = null,
    string? IdempotencyKey = null);

public sealed record ActionResultDto(
    string TargetType,
    string TargetId,
    string Action,
    string Result,
    string Reason,
    string AuditEventId,
    DateTimeOffset OccurredAt);

public sealed record ClusterStatusDto(
    string ClusterId,
    DateTimeOffset GeneratedAt,
    string HeadHealth,
    string MetadataHealth,
    string WriteMode,
    string CapacityPressure,
    int UnavailableNodeCount,
    int RepairBacklogCount,
    long RepairBytesPending,
    long OutboxLagSeconds,
    IReadOnlyList<StatusSignalDto> Signals);

public sealed record StatusSignalDto(
    string Name,
    string State,
    string Value,
    string Detail);

public sealed record NodeDto(
    string NodeId,
    string Region,
    string State,
    DateTimeOffset LastSeenAt,
    long HeartbeatAgeSeconds,
    bool AcceptingWrites,
    string DrainState,
    long PhysicalBytes,
    long UsableBytes,
    long UsedBytes,
    long ReservedBytes,
    int HealthyReplicas,
    int SuspectReplicas,
    int PendingReplicas);

public sealed record CapacityScopeDto(
    string ScopeType,
    string ScopeId,
    string Pressure,
    long PhysicalBytes,
    long UsableBytes,
    long CommittedBytes,
    long ReservedBytes,
    long EffectiveFreeBytes,
    long EmergencyReserveBytes,
    bool WritesFrozen,
    DateTimeOffset UpdatedAt);

public sealed record ObjectVersionDto(
    string TenantId,
    string DatasetId,
    string ObjectId,
    string ObjectLookupHashPrefix,
    string VersionId,
    bool IsCurrent,
    string State,
    bool IsTombstone,
    int HealthyReplicas,
    int RequiredReplicas,
    int SuspectReplicas,
    long PlacementEpoch,
    long? DeleteEpoch,
    bool GcBlocked,
    DateTimeOffset UpdatedAt);

public sealed record RepairJobDto(
    string JobId,
    string RepairClass,
    string Priority,
    string State,
    string Reason,
    string TenantId,
    string DatasetId,
    string ObjectLookupHashPrefix,
    string VersionId,
    long BytesPending,
    DateTimeOffset EnqueuedAt,
    int AttemptCount,
    string? LastFailureReason);

public sealed record AuditEventDto(
    string EventId,
    DateTimeOffset OccurredAt,
    string ActorType,
    string ActorId,
    string AuthorityKeyId,
    string RequestId,
    string? IdempotencyKey,
    string Action,
    string TargetType,
    string TargetId,
    string Result,
    string Reason,
    string HeadNodeId,
    IReadOnlyDictionary<string, string> RedactedMetadata);

public sealed record RecoveryGateDto(
    string GateId,
    string Name,
    string State,
    string Severity,
    string Reason,
    DateTimeOffset OpenedAt,
    int RequiredApprovals,
    int Approvals,
    IReadOnlyList<string> Blocks,
    IReadOnlyList<string> AllowedActions);
