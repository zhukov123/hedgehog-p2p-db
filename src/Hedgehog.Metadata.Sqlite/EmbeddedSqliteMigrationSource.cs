using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Hedgehog.Metadata.Sqlite;

public sealed class EmbeddedSqliteMigrationSource : ISqliteMigrationSource
{
    private readonly Assembly assembly;
    private readonly string resourceRoot;

    public EmbeddedSqliteMigrationSource()
        : this(typeof(SqliteMetadataAuthority).Assembly, SqliteMetadataAuthority.MigrationResourceRoot)
    {
    }

    public EmbeddedSqliteMigrationSource(Assembly assembly, string resourceRoot)
    {
        this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        this.resourceRoot = string.IsNullOrWhiteSpace(resourceRoot)
            ? throw new ArgumentException("Resource root is required.", nameof(resourceRoot))
            : resourceRoot;
    }

    public async ValueTask<IReadOnlyList<SqliteMigration>> LoadMigrationsAsync(
        CancellationToken cancellationToken = default)
    {
        var resourceNames = assembly
            .GetManifestResourceNames()
            .Where(IsMigrationResource)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var migrations = new List<SqliteMigration>(resourceNames.Length);

        foreach (var resourceName in resourceNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration resource '{resourceName}' was not found.");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var id = GetMigrationId(resourceName);
            migrations.Add(new SqliteMigration(id, resourceName, sql, ComputeSha256(sql)));
        }

        return migrations;
    }

    private bool IsMigrationResource(string resourceName)
    {
        return resourceName.StartsWith(resourceRoot + ".", StringComparison.Ordinal)
            && resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);
    }

    private string GetMigrationId(string resourceName)
    {
        var id = resourceName[(resourceRoot.Length + 1)..];
        return id.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            ? id[..^4]
            : id;
    }

    private static string ComputeSha256(string sql)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
