using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Hedgehog.LocalRuntime;

public sealed record LocalRuntimeBackupManifest(
    int Version,
    string RuntimeRoot,
    string CreatedAtUtc,
    IReadOnlyList<LocalRuntimeBackupManifestEntry> Entries);

public sealed record LocalRuntimeBackupManifestEntry(
    string Kind,
    string RelativePath,
    long Bytes,
    string Sha256Hex);

public static class LocalRuntimeBackup
{
    private const int ManifestVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ManifestFileName => "backup-manifest.json";

    public static async Task<LocalRuntimeBackupManifest> CreateAsync(
        string runtimeRoot,
        string backupRoot,
        CancellationToken cancellationToken = default)
    {
        RequireRoot(runtimeRoot, nameof(runtimeRoot));
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            throw new ArgumentException("Backup root is required.", nameof(backupRoot));
        }

        if (Directory.Exists(backupRoot))
        {
            throw new InvalidOperationException($"Backup root already exists: {backupRoot}");
        }

        Directory.CreateDirectory(backupRoot);
        await CheckpointMetadataAsync(runtimeRoot, cancellationToken).ConfigureAwait(false);

        var entries = new List<LocalRuntimeBackupManifestEntry>();
        await CopyFileWithManifestAsync(
            runtimeRoot,
            backupRoot,
            Path.Combine("metadata", "hedgehog.sqlite"),
            "metadata",
            entries,
            cancellationToken).ConfigureAwait(false);

        var storageRoot = Path.Combine(runtimeRoot, "storage");
        if (Directory.Exists(storageRoot))
        {
            foreach (var file in Directory.EnumerateFiles(storageRoot, "agent-manifest.json", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(runtimeRoot, file);
                await CopyFileWithManifestAsync(
                    runtimeRoot,
                    backupRoot,
                    relativePath,
                    "storage_manifest",
                    entries,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in Directory.EnumerateFiles(storageRoot, "*.bin", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(runtimeRoot, file);
                await CopyFileWithManifestAsync(
                    runtimeRoot,
                    backupRoot,
                    relativePath,
                    "replica_blob",
                    entries,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var manifest = new LocalRuntimeBackupManifest(
            ManifestVersion,
            Path.GetFullPath(runtimeRoot),
            DateTimeOffset.UtcNow.ToString("O"),
            entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray());

        await using var stream = new FileStream(
            Path.Combine(backupRoot, ManifestFileName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return manifest;
    }

    public static async Task<LocalRuntimeBackupManifest> ValidateAsync(
        string backupRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            throw new ArgumentException("Backup root is required.", nameof(backupRoot));
        }

        var manifestPath = Path.Combine(backupRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Backup manifest was not found.", manifestPath);
        }

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<LocalRuntimeBackupManifest>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Backup manifest could not be parsed.");

        if (manifest.Version != ManifestVersion)
        {
            throw new InvalidOperationException($"Unsupported backup manifest version: {manifest.Version}");
        }

        if (manifest.Entries.Count == 0)
        {
            throw new InvalidOperationException("Backup manifest did not contain any files.");
        }

        foreach (var entry in manifest.Entries)
        {
            if (Path.IsPathRooted(entry.RelativePath)
                || entry.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment == ".."))
            {
                throw new InvalidOperationException($"Backup manifest contains unsafe path: {entry.RelativePath}");
            }

            var path = Path.Combine(backupRoot, entry.RelativePath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Backup file is missing: {entry.RelativePath}", path);
            }

            var info = new FileInfo(path);
            if (info.Length != entry.Bytes)
            {
                throw new InvalidOperationException($"Backup file length mismatch: {entry.RelativePath}");
            }

            var sha256Hex = await Sha256HexAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sha256Hex, entry.Sha256Hex, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Backup file hash mismatch: {entry.RelativePath}");
            }
        }

        return manifest;
    }

    public static async Task<LocalRuntimeBackupManifest> RestoreAsync(
        string backupRoot,
        string restoreRuntimeRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(restoreRuntimeRoot))
        {
            throw new ArgumentException("Restore runtime root is required.", nameof(restoreRuntimeRoot));
        }

        if (Directory.Exists(restoreRuntimeRoot))
        {
            throw new InvalidOperationException($"Restore runtime root already exists: {restoreRuntimeRoot}");
        }

        var manifest = await ValidateAsync(backupRoot, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(restoreRuntimeRoot);

        try
        {
            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(backupRoot, entry.RelativePath);
                var destination = Path.Combine(restoreRuntimeRoot, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }
        catch
        {
            Directory.Delete(restoreRuntimeRoot, recursive: true);
            throw;
        }

        return manifest;
    }

    private static async Task CheckpointMetadataAsync(
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(runtimeRoot, "metadata", "hedgehog.sqlite");
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException("Metadata database was not found.", metadataPath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = metadataPath,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyFileWithManifestAsync(
        string runtimeRoot,
        string backupRoot,
        string relativePath,
        string kind,
        List<LocalRuntimeBackupManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        var source = Path.Combine(runtimeRoot, relativePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Runtime file was not found: {relativePath}", source);
        }

        var destination = Path.Combine(backupRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);

        var info = new FileInfo(destination);
        entries.Add(new LocalRuntimeBackupManifestEntry(
            kind,
            relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            info.Length,
            await Sha256HexAsync(destination, cancellationToken).ConfigureAwait(false)));
    }

    private static async Task<string> Sha256HexAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void RequireRoot(string root, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new ArgumentException("Runtime root must exist.", parameterName);
        }
    }
}
