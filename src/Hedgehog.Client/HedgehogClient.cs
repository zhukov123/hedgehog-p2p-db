using System.Text;
using Hedgehog.Crypto;
using Hedgehog.Head;

namespace Hedgehog.Client;

public sealed record HedgehogClientOptions(
    string ClientId,
    string TenantId,
    string DatasetId,
    byte[] DatasetLookupKey,
    byte[] DatasetDataKey);

public sealed record PutObjectResult(
    string ClientId,
    string HeadId,
    string ObjectId,
    string VersionId,
    int ReplicaCount);

public sealed record GetObjectResult(
    string ClientId,
    string HeadId,
    string ObjectId,
    string VersionId,
    byte[] Plaintext);

public sealed class HedgehogClient
{
    private readonly HedgehogClientOptions options;
    private readonly IReadOnlyList<IHeadNode> heads;
    private int nextHeadIndex;

    public HedgehogClient(HedgehogClientOptions options, IReadOnlyList<IHeadNode> heads)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.heads = heads is { Count: > 0 }
            ? heads
            : throw new ArgumentException("At least one head node is required.", nameof(heads));
    }

    public Task<PutObjectResult> PutTextAsync(
        string friendlyName,
        string text,
        CancellationToken cancellationToken = default) =>
        PutAsync(friendlyName, Encoding.UTF8.GetBytes(text), cancellationToken);

    public async Task<PutObjectResult> PutAsync(
        string friendlyName,
        byte[] plaintext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var lookupHash = ObjectNameLookup.ComputeLookupHash(options.DatasetLookupKey, friendlyName);
        var objectId = ObjectNameLookup.ObjectIdFromLookupHash(lookupHash);
        var associatedData = AssociatedData(options.TenantId, options.DatasetId, lookupHash);
        var ciphertext = ObjectPayloadCrypto.Encrypt(options.DatasetDataKey, plaintext, associatedData);
        var idempotencyKey = $"client:{options.ClientId}:put:{objectId}:{Guid.NewGuid():N}";

        var result = await SelectHead().PublishAsync(
            new PublishObjectRequest(
                options.ClientId,
                objectId,
                lookupHash,
                ciphertext,
                "aes-256-gcm/client-side",
                idempotencyKey),
            cancellationToken).ConfigureAwait(false);

        return new PutObjectResult(
            options.ClientId,
            result.HeadId,
            result.ObjectId,
            result.VersionId,
            result.Replicas.Count);
    }

    public async Task<GetObjectResult> GetAsync(
        string friendlyName,
        CancellationToken cancellationToken = default)
    {
        var lookupHash = ObjectNameLookup.ComputeLookupHash(options.DatasetLookupKey, friendlyName);
        var associatedData = AssociatedData(options.TenantId, options.DatasetId, lookupHash);
        var result = await SelectHead().RetrieveAsync(
            new RetrieveObjectRequest(options.ClientId, lookupHash),
            cancellationToken).ConfigureAwait(false);
        var plaintext = ObjectPayloadCrypto.Decrypt(options.DatasetDataKey, result.Ciphertext, associatedData);

        return new GetObjectResult(
            options.ClientId,
            result.HeadId,
            result.ObjectId,
            result.VersionId,
            plaintext);
    }

    public async Task<string> GetTextAsync(
        string friendlyName,
        CancellationToken cancellationToken = default)
    {
        var result = await GetAsync(friendlyName, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(result.Plaintext);
    }

    public async Task DeleteAsync(
        string friendlyName,
        CancellationToken cancellationToken = default)
    {
        var lookupHash = ObjectNameLookup.ComputeLookupHash(options.DatasetLookupKey, friendlyName);
        var objectId = ObjectNameLookup.ObjectIdFromLookupHash(lookupHash);
        await SelectHead().DeleteAsync(
            new DeleteObjectRequest(
                options.ClientId,
                objectId,
                lookupHash,
                $"client:{options.ClientId}:delete:{objectId}:{Guid.NewGuid():N}"),
            cancellationToken).ConfigureAwait(false);
    }

    private IHeadNode SelectHead()
    {
        var index = Interlocked.Increment(ref nextHeadIndex) - 1;
        return heads[index % heads.Count];
    }

    private static byte[] AssociatedData(string tenantId, string datasetId, byte[] lookupHash)
    {
        var prefix = Encoding.UTF8.GetBytes($"{tenantId}/{datasetId}/");
        var associatedData = new byte[prefix.Length + lookupHash.Length];
        prefix.CopyTo(associatedData, 0);
        lookupHash.CopyTo(associatedData, prefix.Length);
        return associatedData;
    }
}
