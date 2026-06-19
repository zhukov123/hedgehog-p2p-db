using System.Security.Cryptography;

namespace Hedgehog.Agent.Core;

public sealed record StorageReplicaWrite(
    string VersionId,
    string ReplicaId,
    byte[] Payload,
    byte[] ExpectedHash,
    long FencingToken);

public sealed record StorageReplicaRead(
    string VersionId,
    string ReplicaId,
    byte[] ExpectedHash);

public sealed record StorageReplicaDelete(
    string VersionId,
    string ReplicaId);

public sealed record StorageReplicaResult(
    string NodeId,
    string VersionId,
    string ReplicaId,
    long StoredBytes,
    byte[] ContentHash,
    DateTimeOffset CompletedAt);

public sealed record StoredReplicaInfo(
    string VersionId,
    string ReplicaId,
    long StoredBytes,
    byte[] ContentHash,
    DateTimeOffset UpdatedAt);

public sealed record StorageAgentSnapshot(
    string NodeId,
    bool IsRunning,
    long CapacityBytes,
    long UsedBytes,
    long FreeBytes,
    IReadOnlyList<StoredReplicaInfo> Replicas);

public interface IStorageAgentNode
{
    string NodeId { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<StorageReplicaResult> StoreReplicaAsync(
        StorageReplicaWrite request,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadReplicaAsync(
        StorageReplicaRead request,
        CancellationToken cancellationToken = default);

    Task DeleteReplicaAsync(
        StorageReplicaDelete request,
        CancellationToken cancellationToken = default);

    Task<StorageAgentSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
}

public static class StorageHash
{
    public static byte[] Sha256(byte[] payload) => SHA256.HashData(payload);

    public static bool EqualsHash(byte[] left, byte[] right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}
