using System.Data.Common;

namespace Hedgehog.Metadata.Sqlite;

public interface ISqliteMigrationRunner
{
    Task ApplyMigrationsAsync(DbConnection connection, CancellationToken cancellationToken = default);
}
