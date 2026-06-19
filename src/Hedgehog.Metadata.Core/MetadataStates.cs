namespace Hedgehog.Metadata.Core;

public enum ObjectLifecycleState
{
    Missing = 0,
    Active,
    DeleteMarker,
    Deleted,
}

public enum ObjectVersionLifecycleState
{
    Writing = 0,
    Committed,
    UnderReplicated,
    Quarantined,
    DeleteMarker,
    GcEligible,
    GarbageCollected,
}

public enum ReplicaLifecycleState
{
    Planned = 0,
    Streaming,
    Verifying,
    Healthy,
    Suspect,
    Corrupt,
    Stale,
    DeletePending,
    Deleted,
}

public enum RepairLeaseLifecycleState
{
    Issued = 0,
    Completed,
    Expired,
    Cancelled,
    Fenced,
}

public sealed record MetadataObjectState(
    ObjectId ObjectId,
    ObjectLifecycleState State,
    VersionId? CurrentVersionId,
    IReadOnlyList<ObjectVersionState> Versions,
    IReadOnlyList<RepairLeaseState> RepairLeases)
{
    public static MetadataObjectState Empty(ObjectId objectId) =>
        new(objectId, ObjectLifecycleState.Missing, null, [], []);
}

public sealed record ObjectVersionState(
    VersionId VersionId,
    ObjectVersionLifecycleState State,
    ActorId CreatedBy,
    DateTimeOffset CreatedAt,
    long? ContentLength,
    string? ContentHash,
    int RequiredReplicaCount,
    DateTimeOffset? WriteIntentExpiresAt,
    IReadOnlyList<ReplicaPlacementState> Replicas);

public sealed record ReplicaPlacementState(
    ReplicaId ReplicaId,
    NodeId NodeId,
    ReplicaLifecycleState State,
    long StoredBytes,
    string ContentHash,
    DateTimeOffset CompletedAt);

public sealed record RepairLeaseState(
    RepairLeaseId LeaseId,
    VersionId VersionId,
    ReplicaId? ReplicaId,
    NodeId HolderNodeId,
    RepairLeaseLifecycleState State,
    DateTimeOffset LeasedAt,
    DateTimeOffset ExpiresAt);
