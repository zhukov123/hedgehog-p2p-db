namespace Hedgehog.Metadata.Core;

public sealed record CreateWriteIntentCommand(
    ObjectId ObjectId,
    VersionId VersionId,
    ActorId WriterActorId,
    DateTimeOffset RequestedAt,
    long ContentLength,
    string ContentHash,
    int RequiredReplicaCount,
    TimeSpan IntentTtl);

public sealed record CompleteReplicaCommand(
    ObjectId ObjectId,
    VersionId VersionId,
    ReplicaId ReplicaId,
    NodeId NodeId,
    DateTimeOffset CompletedAt,
    long StoredBytes,
    string ContentHash);

public sealed record CommitVersionCommand(
    ObjectId ObjectId,
    VersionId VersionId,
    ActorId CommitterActorId,
    DateTimeOffset CommittedAt);

public sealed record CreateDeleteMarkerCommand(
    ObjectId ObjectId,
    VersionId DeleteMarkerVersionId,
    ActorId ActorId,
    DateTimeOffset CreatedAt);

public sealed record AcquireRepairLeaseCommand(
    ObjectId ObjectId,
    VersionId VersionId,
    RepairLeaseId LeaseId,
    NodeId HolderNodeId,
    DateTimeOffset LeasedAt,
    TimeSpan LeaseDuration,
    ReplicaId? ReplicaId = null);

public sealed record ExpireReservationCommand(
    ObjectId ObjectId,
    VersionId VersionId,
    DateTimeOffset ExpiredAt);
