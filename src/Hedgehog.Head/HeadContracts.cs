namespace Hedgehog.Head;

public sealed record HeadNodeOptions(
    string HeadId,
    string TenantId,
    string DatasetId,
    string ActorId,
    string LookupKeyId,
    string DataKeyId,
    int RequiredReplicaCount);

public sealed record PublishObjectRequest(
    string ClientId,
    string ObjectId,
    byte[] ObjectLookupHash,
    byte[] Ciphertext,
    string EncryptionAlg,
    string IdempotencyKey);

public sealed record PublishObjectResult(
    string HeadId,
    string ObjectId,
    string VersionId,
    long VersionNo,
    byte[] CiphertextHash,
    IReadOnlyList<ReplicaCommit> Replicas);

public sealed record RetrieveObjectRequest(
    string ClientId,
    byte[] ObjectLookupHash);

public sealed record RetrieveObjectResult(
    string HeadId,
    string ObjectId,
    string VersionId,
    byte[] Ciphertext,
    byte[] CiphertextHash,
    IReadOnlyList<ReplicaCommit> ReplicaCandidates);

public sealed record DeleteObjectRequest(
    string ClientId,
    string ObjectId,
    byte[] ObjectLookupHash,
    string IdempotencyKey);

public sealed record DeleteObjectResult(
    string HeadId,
    string ObjectId,
    string DeleteMarkerVersionId);

public sealed record ReplicaCommit(
    string NodeId,
    string ReplicaId,
    long StoredBytes,
    byte[] ContentHash);

public sealed record HeadNodeSnapshot(
    string HeadId,
    bool IsRunning,
    int StorageNodeCount,
    long PublishedObjectCount);

public sealed record ReplicaRepairReconciliationResult(
    int ReplicasChecked,
    int ReplicaFailuresDetected,
    int RepairJobsEnqueued);

public interface IHeadNode
{
    string HeadId { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<PublishObjectResult> PublishAsync(
        PublishObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<RetrieveObjectResult> RetrieveAsync(
        RetrieveObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<DeleteObjectResult> DeleteAsync(
        DeleteObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<HeadNodeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
}
