using Hedgehog.Metadata.Sqlite;
using Hedgehog.Types;
using Microsoft.Data.Sqlite;

await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();

var runner = SqliteMetadataAuthority.CreateMigrationRunner();
await runner.ApplyMigrationsAsync(connection);

Equal(6, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM __hedgehog_schema_migrations;"));
Equal(Labels.AllGroups.SelectMany(group => group).Count(), await ScalarIntAsync(connection, "SELECT COUNT(*) FROM labels;"));
Equal(13, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM workflow_definitions;"));
Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM metadata_store WHERE store_id = 'default';"));

await AssertTableHasColumnsAsync(connection, "objects", "tenant_id", "dataset_id", "object_lookup_hash", "lookup_key_id");
await AssertTableHasColumnsAsync(connection, "object_versions", "placement_epoch", "delete_epoch", "required_replica_count");
await AssertTableHasColumnsAsync(connection, "replicas", "fencing_token", "placement_epoch", "delete_epoch");
await AssertTableHasColumnsAsync(connection, "capacity_reservations", "reservation_class", "fencing_token", "bytes_reserved");
await AssertForeignKeyCheckCleanAsync(connection);

await runner.ApplyMigrationsAsync(connection);
Equal(6, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM __hedgehog_schema_migrations;"));

Console.WriteLine("Hedgehog.Metadata.Sqlite.Tests passed.");

static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    var value = await command.ExecuteScalarAsync();
    return Convert.ToInt32(value);
}

static async Task AssertTableHasColumnsAsync(SqliteConnection connection, string tableName, params string[] expectedColumns)
{
    var columns = new HashSet<string>(StringComparer.Ordinal);
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({tableName});";
    await using var reader = await command.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        columns.Add(reader.GetString(1));
    }

    foreach (var expectedColumn in expectedColumns)
    {
        if (!columns.Contains(expectedColumn))
        {
            throw new InvalidOperationException($"Expected table '{tableName}' to have column '{expectedColumn}'.");
        }
    }
}

static async Task AssertForeignKeyCheckCleanAsync(SqliteConnection connection)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_key_check;";
    await using var reader = await command.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        throw new InvalidOperationException("PRAGMA foreign_key_check returned violations.");
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
    }
}
