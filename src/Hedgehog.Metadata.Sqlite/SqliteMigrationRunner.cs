using System.Data;
using System.Data.Common;

namespace Hedgehog.Metadata.Sqlite;

public sealed class SqliteMigrationRunner : ISqliteMigrationRunner
{
    private const string CreateHistorySql = """
        CREATE TABLE IF NOT EXISTS __hedgehog_schema_migrations (
            id TEXT NOT NULL PRIMARY KEY,
            checksum_sha256 TEXT NOT NULL,
            applied_at_unix_ms INTEGER NOT NULL,
            resource_name TEXT NOT NULL
        );
        """;

    private readonly ISqliteMigrationSource migrationSource;

    public SqliteMigrationRunner()
        : this(new EmbeddedSqliteMigrationSource())
    {
    }

    public SqliteMigrationRunner(ISqliteMigrationSource migrationSource)
    {
        this.migrationSource = migrationSource ?? throw new ArgumentNullException(nameof(migrationSource));
    }

    public async Task ApplyMigrationsAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(connection, transaction: null, "PRAGMA foreign_keys = ON;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, transaction: null, "PRAGMA busy_timeout = 5000;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, transaction: null, CreateHistorySql, cancellationToken)
            .ConfigureAwait(false);

        var appliedMigrations = await LoadAppliedMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
        var migrations = await migrationSource.LoadMigrationsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var migration in migrations)
        {
            if (appliedMigrations.TryGetValue(migration.Id, out var appliedChecksum))
            {
                if (!StringComparer.Ordinal.Equals(appliedChecksum, migration.ChecksumSha256))
                {
                    throw new InvalidOperationException(
                        $"Applied migration '{migration.Id}' has checksum '{appliedChecksum}', but embedded resource '{migration.ResourceName}' has checksum '{migration.ChecksumSha256}'.");
                }

                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await ExecuteNonQueryAsync(connection, transaction, migration.Sql, cancellationToken).ConfigureAwait(false);
                await RecordMigrationAsync(connection, transaction, migration, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadAppliedMigrationsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, checksum_sha256 FROM __hedgehog_schema_migrations ORDER BY id;";

        var appliedMigrations = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            appliedMigrations.Add(reader.GetString(0), reader.GetString(1));
        }

        return appliedMigrations;
    }

    private static async Task RecordMigrationAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqliteMigration migration,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO __hedgehog_schema_migrations (
                id,
                checksum_sha256,
                applied_at_unix_ms,
                resource_name
            )
            VALUES (
                @id,
                @checksum_sha256,
                CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER),
                @resource_name
            );
            """;

        AddParameter(command, "@id", migration.Id);
        AddParameter(command, "@checksum_sha256", migration.ChecksumSha256);
        AddParameter(command, "@resource_name", migration.ResourceName);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
