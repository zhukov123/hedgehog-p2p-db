using System.Data;
using System.Data.Common;

namespace Hedgehog.Metadata.Sqlite;

public sealed class SqliteMetadataInspector : ISqliteMetadataInspector
{
    public async Task<IReadOnlyList<SqliteInvariantViolation>> CheckInvariantsAsync(
        IDbConnection connection,
        CancellationToken cancellationToken = default)
    {
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);

        var violations = new List<SqliteInvariantViolation>();
        violations.AddRange(await ReadInvariantViolationsAsync(
            db,
            """
            SELECT
                'object_current_version_missing' AS code,
                'object' AS scope,
                o.object_id AS entity_id,
                'critical' AS severity,
                'object current_version_id does not point at a version for the same object' AS details
            FROM objects o
            LEFT JOIN object_versions v
                ON v.version_id = o.current_version_id
                AND v.object_id = o.object_id
            WHERE o.current_version_id IS NOT NULL
                AND v.version_id IS NULL
            ORDER BY o.object_id;
            """,
            cancellationToken).ConfigureAwait(false));

        violations.AddRange(await ReadInvariantViolationsAsync(
            db,
            """
            SELECT
                'active_object_without_current_version' AS code,
                'object' AS scope,
                o.object_id AS entity_id,
                'high' AS severity,
                'active object has no current committed or delete-marker version' AS details
            FROM objects o
            WHERE o.state IN ('active', 'delete_marker')
                AND o.current_version_id IS NULL
                AND NOT EXISTS (
                    SELECT 1
                    FROM object_versions v
                    WHERE v.object_id = o.object_id
                        AND v.state = 'writing'
                )
            ORDER BY o.object_id;
            """,
            cancellationToken).ConfigureAwait(false));

        violations.AddRange(await ReadInvariantViolationsAsync(
            db,
            """
            SELECT
                'committed_version_below_replica_quorum' AS code,
                'version' AS scope,
                v.version_id AS entity_id,
                'critical' AS severity,
                'committed version has fewer healthy replicas than required'
                    || ': required=' || v.required_replica_count
                    || ', healthy=' || COUNT(r.replica_id) AS details
            FROM object_versions v
            LEFT JOIN replicas r
                ON r.version_id = v.version_id
                AND r.state = 'healthy'
            WHERE v.state = 'committed'
            GROUP BY v.version_id, v.required_replica_count
            HAVING COUNT(r.replica_id) < v.required_replica_count
            ORDER BY v.version_id;
            """,
            cancellationToken).ConfigureAwait(false));

        violations.AddRange(await ReadInvariantViolationsAsync(
            db,
            """
            SELECT
                'healthy_replica_without_hash_confirmation' AS code,
                'replica' AS scope,
                r.replica_id AS entity_id,
                'high' AS severity,
                'replica is healthy but hash_confirmed is not set' AS details
            FROM replicas r
            WHERE r.state = 'healthy'
                AND r.hash_confirmed <> 1
            ORDER BY r.replica_id;
            """,
            cancellationToken).ConfigureAwait(false));

        violations.AddRange(await ReadInvariantViolationsAsync(
            db,
            """
            SELECT
                'node_capacity_accounting_invalid' AS code,
                'node' AS scope,
                n.node_id AS entity_id,
                'high' AS severity,
                'node capacity accounting is impossible'
                    || ': capacity=' || n.capacity_bytes
                    || ', used=' || n.used_bytes
                    || ', reserved=' || n.reserved_bytes
                    || ', free=' || n.free_bytes AS details
            FROM nodes n
            WHERE n.used_bytes + n.reserved_bytes > n.capacity_bytes
                OR n.free_bytes > n.capacity_bytes - n.used_bytes - n.reserved_bytes
            ORDER BY n.node_id;
            """,
            cancellationToken).ConfigureAwait(false));

        return violations;
    }

    public async Task<IReadOnlyList<SqliteRepairReadinessCandidate>> ListRepairReadinessAsync(
        IDbConnection connection,
        CancellationToken cancellationToken = default)
    {
        var db = RequireDbConnection(connection);
        await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);

        var candidates = new List<SqliteRepairReadinessCandidate>();
        candidates.AddRange(await ReadRepairCandidatesAsync(
            db,
            """
            WITH version_health AS (
                SELECT
                    v.version_id,
                    COUNT(r.replica_id) AS healthy_count
                FROM object_versions v
                LEFT JOIN replicas r
                    ON r.version_id = v.version_id
                    AND r.state = 'healthy'
                GROUP BY v.version_id
            )
            SELECT
                o.tenant_id,
                o.dataset_id,
                o.object_id,
                v.version_id,
                NULL AS replica_id,
                'under_replicated' AS kind,
                100 AS priority,
                'version has fewer healthy replicas than required'
                    || ': required=' || v.required_replica_count
                    || ', healthy=' || vh.healthy_count AS reason,
                v.required_replica_count,
                vh.healthy_count,
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM repair_jobs j
                    WHERE j.version_id = v.version_id
                        AND j.kind = 'under_replicated'
                        AND j.state IN ('pending', 'leased', 'running', 'verifying', 'retry_wait')
                ) THEN 1 ELSE 0 END AS has_active_repair_job
            FROM object_versions v
            JOIN objects o ON o.object_id = v.object_id
            JOIN version_health vh ON vh.version_id = v.version_id
            WHERE v.state IN ('committed', 'under_replicated')
                AND vh.healthy_count < v.required_replica_count
            ORDER BY priority DESC, o.object_id, v.version_id;
            """,
            cancellationToken).ConfigureAwait(false));

        candidates.AddRange(await ReadRepairCandidatesAsync(
            db,
            """
            WITH version_health AS (
                SELECT
                    v.version_id,
                    COUNT(hr.replica_id) AS healthy_count
                FROM object_versions v
                LEFT JOIN replicas hr
                    ON hr.version_id = v.version_id
                    AND hr.state = 'healthy'
                GROUP BY v.version_id
            )
            SELECT
                o.tenant_id,
                o.dataset_id,
                o.object_id,
                v.version_id,
                r.replica_id,
                CASE
                    WHEN r.state = 'suspect' THEN 'suspect_verify'
                    WHEN r.state IN ('corrupt', 'stale') THEN 'missing_replace'
                    ELSE 'delete_cleanup'
                END AS kind,
                CASE
                    WHEN r.state = 'corrupt' THEN 90
                    WHEN r.state = 'stale' THEN 80
                    WHEN r.state = 'suspect' THEN 70
                    ELSE 40
                END AS priority,
                'replica state requires repair handling: ' || r.state AS reason,
                v.required_replica_count,
                vh.healthy_count,
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM repair_jobs j
                    WHERE j.version_id = v.version_id
                        AND j.kind = CASE
                            WHEN r.state = 'suspect' THEN 'suspect_verify'
                            WHEN r.state IN ('corrupt', 'stale') THEN 'missing_replace'
                            ELSE 'delete_cleanup'
                        END
                        AND j.state IN ('pending', 'leased', 'running', 'verifying', 'retry_wait')
                ) THEN 1 ELSE 0 END AS has_active_repair_job
            FROM replicas r
            JOIN object_versions v ON v.version_id = r.version_id
            JOIN objects o ON o.object_id = v.object_id
            JOIN version_health vh ON vh.version_id = v.version_id
            WHERE r.state IN ('suspect', 'corrupt', 'stale', 'delete_pending')
            ORDER BY priority DESC, o.object_id, v.version_id, r.replica_id;
            """,
            cancellationToken).ConfigureAwait(false));

        return candidates
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.ObjectId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.VersionId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ReplicaId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IReadOnlyList<SqliteInvariantViolation>> ReadInvariantViolationsAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var violations = new List<SqliteInvariantViolation>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            violations.Add(new SqliteInvariantViolation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return violations;
    }

    private static async Task<IReadOnlyList<SqliteRepairReadinessCandidate>> ReadRepairCandidatesAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var candidates = new List<SqliteRepairReadinessCandidate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new SqliteRepairReadinessCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                Convert.ToInt32(reader.GetValue(6)),
                reader.GetString(7),
                Convert.ToInt32(reader.GetValue(8)),
                Convert.ToInt32(reader.GetValue(9)),
                Convert.ToInt32(reader.GetValue(10)) == 1));
        }

        return candidates;
    }

    private static DbConnection RequireDbConnection(IDbConnection connection) =>
        connection as DbConnection
        ?? throw new ArgumentException("Metadata inspector requires a DbConnection.", nameof(connection));

    private static async Task EnsureOpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
