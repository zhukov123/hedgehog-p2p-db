namespace Hedgehog.Metadata.Sqlite;

public interface ISqliteMigrationSource
{
    ValueTask<IReadOnlyList<SqliteMigration>> LoadMigrationsAsync(CancellationToken cancellationToken = default);
}
