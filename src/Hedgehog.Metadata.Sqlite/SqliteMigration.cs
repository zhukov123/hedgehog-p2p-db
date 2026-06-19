namespace Hedgehog.Metadata.Sqlite;

public sealed record SqliteMigration(
    string Id,
    string ResourceName,
    string Sql,
    string ChecksumSha256);
