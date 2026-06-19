namespace Hedgehog.Metadata.Sqlite;

public sealed class SqliteMetadataAuthority
{
    public const string MigrationResourceRoot = "Hedgehog.Metadata.Sqlite.Migrations";

    public static ISqliteMigrationSource CreateMigrationSource()
    {
        return new EmbeddedSqliteMigrationSource();
    }

    public static ISqliteMigrationRunner CreateMigrationRunner()
    {
        return new SqliteMigrationRunner(CreateMigrationSource());
    }

    public static ISqliteMetadataWorkflowStore CreateWorkflowStore()
    {
        return new SqliteMetadataWorkflowStore();
    }
}
