namespace Hedgehog.Metadata.Core;

public interface IMetadataEvent
{
    string WorkflowName { get; }

    ObjectId ObjectId { get; }

    DateTimeOffset OccurredAt { get; }
}

public sealed record WriteIntentCreated(
    ObjectId ObjectId,
    VersionId VersionId,
    ActorId WriterActorId,
    DateTimeOffset OccurredAt,
    long ContentLength,
    string ContentHash,
    int RequiredReplicaCount,
    DateTimeOffset ExpiresAt) : IMetadataEvent
{
    public string WorkflowName => MetadataWorkflowNames.CreateWriteIntent;
}

public sealed record ReplicaCompleted(
    ObjectId ObjectId,
    VersionId VersionId,
    ReplicaId ReplicaId,
    NodeId NodeId,
    DateTimeOffset OccurredAt,
    long StoredBytes,
    string ContentHash) : IMetadataEvent
{
    public string WorkflowName => MetadataWorkflowNames.CompleteReplica;
}

public sealed record VersionCommitted(
    ObjectId ObjectId,
    VersionId VersionId,
    ActorId CommitterActorId,
    DateTimeOffset OccurredAt) : IMetadataEvent
{
    public string WorkflowName => MetadataWorkflowNames.CommitVersion;
}

public sealed record DeleteMarkerCreated(
    ObjectId ObjectId,
    VersionId VersionId,
    ActorId ActorId,
    DateTimeOffset OccurredAt) : IMetadataEvent
{
    public string WorkflowName => MetadataWorkflowNames.DeleteMarker;
}

public sealed record RepairLeaseAcquired(
    ObjectId ObjectId,
    VersionId VersionId,
    RepairLeaseId LeaseId,
    NodeId HolderNodeId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ExpiresAt,
    ReplicaId? ReplicaId) : IMetadataEvent
{
    public string WorkflowName => MetadataWorkflowNames.LeaseRepair;
}

public sealed record ReservationExpired(
    ObjectId ObjectId,
    VersionId VersionId,
    DateTimeOffset OccurredAt,
    DateTimeOffset WriteIntentExpiresAt) : IMetadataEvent
{
    public string WorkflowName => MetadataWorkflowNames.ExpireReservation;
}

public sealed record MetadataDecision(MetadataObjectState State, IReadOnlyList<IMetadataEvent> Events);
