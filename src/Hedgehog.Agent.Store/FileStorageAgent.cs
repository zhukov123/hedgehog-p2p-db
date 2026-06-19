using System.Text.Json;
using Hedgehog.Agent.Core;

namespace Hedgehog.Agent.Store;

public sealed class FileStorageAgent : IStorageAgentNode
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string rootDirectory;
    private readonly Dictionary<string, StoredReplicaInfo> manifest = new(StringComparer.Ordinal);
    private bool isRunning;

    public FileStorageAgent(string nodeId, string rootDirectory, long capacityBytes)
    {
        NodeId = RequireSafeId(nodeId, nameof(nodeId));
        this.rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? throw new ArgumentException("Storage root directory is required.", nameof(rootDirectory))
            : rootDirectory;
        CapacityBytes = capacityBytes > 0
            ? capacityBytes
            : throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Capacity must be positive.");
    }

    public string NodeId { get; }

    public long CapacityBytes { get; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(NodeRoot);
            manifest.Clear();

            var manifestPath = ManifestPath;
            if (File.Exists(manifestPath))
            {
                await using var stream = File.OpenRead(manifestPath);
                var entries = await JsonSerializer.DeserializeAsync<List<StoredReplicaInfo>>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false) ?? [];

                foreach (var entry in entries)
                {
                    if (File.Exists(ReplicaPath(entry.VersionId, entry.ReplicaId)))
                    {
                        manifest[ManifestKey(entry.VersionId, entry.ReplicaId)] = entry;
                    }
                }
            }

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
            await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
            isRunning = false;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StorageReplicaResult> StoreReplicaAsync(
        StorageReplicaWrite request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var versionId = RequireSafeId(request.VersionId, nameof(request.VersionId));
        var replicaId = RequireSafeId(request.ReplicaId, nameof(request.ReplicaId));
        if (request.FencingToken < 0)
        {
            throw new InvalidOperationException("Fencing token must be non-negative.");
        }

        var contentHash = StorageHash.Sha256(request.Payload);
        if (!StorageHash.EqualsHash(contentHash, request.ExpectedHash))
        {
            throw new InvalidOperationException("Replica payload hash does not match the expected hash.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireRunning();
            var existingUsedBytes = UsedBytesUnsafe();
            var key = ManifestKey(versionId, replicaId);
            if (manifest.TryGetValue(key, out var existing))
            {
                existingUsedBytes -= existing.StoredBytes;
            }

            if (existingUsedBytes + request.Payload.LongLength > CapacityBytes)
            {
                throw new InvalidOperationException($"Node '{NodeId}' does not have enough free capacity.");
            }

            var replicaPath = ReplicaPath(versionId, replicaId);
            Directory.CreateDirectory(Path.GetDirectoryName(replicaPath)!);
            var tempPath = $"{replicaPath}.tmp-{Guid.NewGuid():N}";

            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(request.Payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, replicaPath, overwrite: true);
            var now = DateTimeOffset.UtcNow;
            var stored = new StoredReplicaInfo(versionId, replicaId, request.Payload.LongLength, contentHash, now);
            manifest[key] = stored;
            await SaveManifestAsync(cancellationToken).ConfigureAwait(false);

            return new StorageReplicaResult(NodeId, versionId, replicaId, stored.StoredBytes, contentHash, now);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<byte[]> ReadReplicaAsync(
        StorageReplicaRead request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var versionId = RequireSafeId(request.VersionId, nameof(request.VersionId));
        var replicaId = RequireSafeId(request.ReplicaId, nameof(request.ReplicaId));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireRunning();
            if (!manifest.ContainsKey(ManifestKey(versionId, replicaId)))
            {
                throw new FileNotFoundException($"Replica '{replicaId}' for version '{versionId}' was not found.");
            }

            var payload = await File.ReadAllBytesAsync(ReplicaPath(versionId, replicaId), cancellationToken)
                .ConfigureAwait(false);
            var actualHash = StorageHash.Sha256(payload);
            if (!StorageHash.EqualsHash(actualHash, request.ExpectedHash))
            {
                throw new InvalidOperationException("Stored replica hash no longer matches metadata.");
            }

            return payload;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteReplicaAsync(
        StorageReplicaDelete request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var versionId = RequireSafeId(request.VersionId, nameof(request.VersionId));
        var replicaId = RequireSafeId(request.ReplicaId, nameof(request.ReplicaId));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireRunning();
            manifest.Remove(ManifestKey(versionId, replicaId));
            var replicaPath = ReplicaPath(versionId, replicaId);
            if (File.Exists(replicaPath))
            {
                File.Delete(replicaPath);
            }

            await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StorageAgentSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var replicas = manifest.Values
                .OrderBy(replica => replica.VersionId, StringComparer.Ordinal)
                .ThenBy(replica => replica.ReplicaId, StringComparer.Ordinal)
                .ToArray();
            var usedBytes = replicas.Sum(replica => replica.StoredBytes);

            return new StorageAgentSnapshot(
                NodeId,
                isRunning,
                CapacityBytes,
                usedBytes,
                CapacityBytes - usedBytes,
                replicas);
        }
        finally
        {
            gate.Release();
        }
    }

    private string NodeRoot => Path.Combine(rootDirectory, NodeId);

    private string ManifestPath => Path.Combine(NodeRoot, "agent-manifest.json");

    private string ReplicaPath(string versionId, string replicaId) =>
        Path.Combine(NodeRoot, "replicas", versionId, $"{replicaId}.bin");

    private long UsedBytesUnsafe() => manifest.Values.Sum(replica => replica.StoredBytes);

    private async Task SaveManifestAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(NodeRoot);
        await using var stream = new FileStream(
            ManifestPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            manifest.Values.OrderBy(replica => replica.VersionId, StringComparer.Ordinal).ThenBy(replica => replica.ReplicaId, StringComparer.Ordinal).ToArray(),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private void RequireRunning()
    {
        if (!isRunning)
        {
            throw new InvalidOperationException($"Storage agent '{NodeId}' is not running.");
        }
    }

    private static string ManifestKey(string versionId, string replicaId) => $"{versionId}/{replicaId}";

    private static string RequireSafeId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new ArgumentException("Identifier must contain only ASCII letters, digits, dash, underscore, or dot.", parameterName);
        }

        return value;
    }
}
