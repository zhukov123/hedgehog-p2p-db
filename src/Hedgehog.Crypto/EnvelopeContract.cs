namespace Hedgehog.Crypto;

public static class EnvelopeContract
{
    public const int Version = 1;
    public const string DomainSeparation = "hedgehog-v1-envelope";
}

public static class ObjectNameLookup
{
    public static string Normalize(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            throw new ArgumentException("Object name is required.", nameof(friendlyName));
        }

        return string.Join(
            '/',
            friendlyName.Trim().Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    public static byte[] ComputeLookupHash(byte[] datasetLookupKey, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(datasetLookupKey);
        if (datasetLookupKey.Length < 32)
        {
            throw new ArgumentException("Dataset lookup key must be at least 32 bytes.", nameof(datasetLookupKey));
        }

        using var hmac = new System.Security.Cryptography.HMACSHA256(datasetLookupKey);
        return hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Normalize(friendlyName)));
    }

    public static string ObjectIdFromLookupHash(byte[] lookupHash)
    {
        ArgumentNullException.ThrowIfNull(lookupHash);
        if (lookupHash.Length < 12)
        {
            throw new ArgumentException("Lookup hash must be at least 12 bytes.", nameof(lookupHash));
        }

        return $"obj_{Convert.ToHexString(lookupHash.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}

public static class ObjectPayloadCrypto
{
    private const byte PayloadVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public static byte[] Encrypt(byte[] datasetDataKey, byte[] plaintext, byte[] associatedData)
    {
        ValidateKey(datasetDataKey, nameof(datasetDataKey));
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(associatedData);

        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new System.Security.Cryptography.AesGcm(datasetDataKey, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        var payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        payload[0] = PayloadVersion;
        nonce.CopyTo(payload.AsSpan(1, NonceSize));
        tag.CopyTo(payload.AsSpan(1 + NonceSize, TagSize));
        ciphertext.CopyTo(payload.AsSpan(1 + NonceSize + TagSize));
        return payload;
    }

    public static byte[] Decrypt(byte[] datasetDataKey, byte[] encryptedPayload, byte[] associatedData)
    {
        ValidateKey(datasetDataKey, nameof(datasetDataKey));
        ArgumentNullException.ThrowIfNull(encryptedPayload);
        ArgumentNullException.ThrowIfNull(associatedData);

        if (encryptedPayload.Length < 1 + NonceSize + TagSize || encryptedPayload[0] != PayloadVersion)
        {
            throw new ArgumentException("Encrypted payload is not a Hedgehog v1 object payload.", nameof(encryptedPayload));
        }

        var nonce = encryptedPayload.AsSpan(1, NonceSize);
        var tag = encryptedPayload.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = encryptedPayload.AsSpan(1 + NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using (var aes = new System.Security.Cryptography.AesGcm(datasetDataKey, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }

        return plaintext;
    }

    private static void ValidateKey(byte[] key, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize)
        {
            throw new ArgumentException("Object payload key must be exactly 32 bytes.", parameterName);
        }
    }
}
