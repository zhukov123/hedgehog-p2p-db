using Hedgehog.LocalRuntime;

namespace Hedgehog.LocalRuntime.Api;

public sealed class RecoveryReadinessOptions
{
    public TimeSpan EvaluationTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan CapacityReportFreshness { get; init; } = TimeSpan.FromMinutes(5);
}

public interface IRecoveryReadinessProbe
{
    Task<RecoveryReadinessProbeSnapshot> EvaluateAsync(CancellationToken cancellationToken);
}

public sealed record RecoveryReadinessProbeSnapshot(
    RecoveryOperationalSummaryDto OperationalSummary,
    IReadOnlyList<RecoveryGateProbeResult> Gates);

public sealed record RecoveryGateProbeResult(
    string Name,
    string Status,
    string Reason);

public sealed record RecoveryOperationalSummaryDto(
    bool MetadataAvailable,
    int TenantCount,
    int RunningHeads,
    int TotalHeads,
    int RunningStorageNodes,
    int TotalStorageNodes);

public sealed record RecoveryReadinessDto(
    string SchemaVersion,
    DateTimeOffset EvaluatedAt,
    bool Ready,
    RecoveryOperationalSummaryDto OperationalSummary,
    IReadOnlyList<RecoveryGateOutcomeDto> Gates);

public sealed record RecoveryGateOutcomeDto(
    string Name,
    string Status,
    string Reason);

public sealed class RecoveryReadinessEvaluator(
    IRecoveryReadinessProbe probe,
    RecoveryReadinessOptions options)
{
    public const string SchemaVersion = "recovery-readiness.v1";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> CanonicalGateNames =
    [
        "schema_migrations",
        "metadata_invariants",
        "outbox_reconciliation",
        "audit_continuity",
        "cache_rebuild",
        "manifest_reconciliation",
        "reservation_reconciliation",
        "repair_deficit",
        "fresh_capacity_reports",
    ];

    public async Task<RecoveryReadinessDto> EvaluateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(options.EvaluationTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var probed = await probe.EvaluateAsync(linked.Token).ConfigureAwait(false);
            return Build(probed, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildUnknown("probe_timeout", DateTimeOffset.UtcNow);
        }
        catch
        {
            return BuildUnknown("probe_unavailable", DateTimeOffset.UtcNow);
        }
    }

    private static RecoveryReadinessDto Build(RecoveryReadinessProbeSnapshot probed, DateTimeOffset evaluatedAt)
    {
        var byName = probed.Gates
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);

        var gates = CanonicalGateNames
            .Select(name => byName.TryGetValue(name, out var result)
                ? new RecoveryGateOutcomeDto(name, NormalizeStatus(result.Status), SanitizeReason(result.Reason))
                : new RecoveryGateOutcomeDto(name, Unknown, "not_implemented"))
            .ToArray();
        var ready = gates.All(gate => gate.Status == Passed);

        return new RecoveryReadinessDto(SchemaVersion, evaluatedAt, ready, probed.OperationalSummary, gates);
    }

    private static RecoveryReadinessDto BuildUnknown(string reason, DateTimeOffset evaluatedAt)
    {
        var gates = CanonicalGateNames
            .Select(name => new RecoveryGateOutcomeDto(name, Unknown, reason))
            .ToArray();
        return new RecoveryReadinessDto(SchemaVersion, evaluatedAt, Ready: false, UnknownOperationalSummary, gates);
    }

    private static RecoveryOperationalSummaryDto UnknownOperationalSummary { get; } =
        new(
            MetadataAvailable: false,
            TenantCount: 0,
            RunningHeads: 0,
            TotalHeads: 0,
            RunningStorageNodes: 0,
            TotalStorageNodes: 0);

    private static string NormalizeStatus(string status) =>
        status switch
        {
            Passed => Passed,
            Failed => Failed,
            Unknown => Unknown,
            _ => Unknown,
        };

    private static string SanitizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "unspecified";
        }

        if (reason.Contains('\\', StringComparison.Ordinal) || reason.Contains('/', StringComparison.Ordinal))
        {
            return "redacted";
        }

        return reason.Length <= 96 ? reason : reason[..96];
    }
}

internal sealed class LocalRuntimeRecoveryReadinessProbe(
    LocalCluster runtime,
    RecoveryReadinessOptions options) : IRecoveryReadinessProbe
{
    public async Task<RecoveryReadinessProbeSnapshot> EvaluateAsync(CancellationToken cancellationToken)
    {
        var snapshot = await runtime.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var tenantCount = await runtime.ScalarLongAsync(
            "SELECT COUNT(*) FROM tenants;",
            cancellationToken).ConfigureAwait(false);
        var auditEvents = await runtime.ScalarLongAsync(
            "SELECT COUNT(*) FROM audit_events;",
            cancellationToken).ConfigureAwait(false);
        var pendingOutbox = await runtime.ScalarLongAsync(
            "SELECT COUNT(*) FROM outbox_events WHERE delivered_at_ms IS NULL;",
            cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var staleActiveReservations = await runtime.ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM capacity_reservations
            WHERE state IN ('pending', 'reserved', 'streaming', 'finalizing')
              AND expires_at_ms <= @now_ms;
            """,
            cancellationToken,
            ("@now_ms", nowMs)).ConfigureAwait(false);
        var committedReservationGaps = await runtime.ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM capacity_reservations cr
            LEFT JOIN replicas r ON r.replica_id = cr.replica_id
            WHERE cr.state = 'committed'
              AND (r.replica_id IS NULL OR r.state <> 'healthy');
            """,
            cancellationToken).ConfigureAwait(false);
        var repairDeficits = await runtime.ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM object_versions v
            WHERE v.state = 'committed'
              AND (
                  SELECT COUNT(*)
                  FROM replicas r
                  WHERE r.version_id = v.version_id
                    AND r.state = 'healthy'
              ) < v.required_replica_count;
            """,
            cancellationToken).ConfigureAwait(false);
        var activeRepairJobs = await runtime.ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM repair_jobs
            WHERE state IN ('pending', 'leased', 'running', 'verifying', 'retry_wait');
            """,
            cancellationToken).ConfigureAwait(false);
        var freshCapacityCutoffMs = now.Subtract(options.CapacityReportFreshness).ToUnixTimeMilliseconds();
        var missingFreshCapacityReports = await runtime.ScalarLongAsync(
            """
            SELECT COUNT(*)
            FROM nodes n
            WHERE n.state = 'active'
              AND NOT EXISTS (
                  SELECT 1
                  FROM capacity_reports c
                  WHERE c.node_id = n.node_id
                    AND c.observed_at_ms >= @fresh_cutoff_ms
              );
            """,
            cancellationToken,
            ("@fresh_cutoff_ms", freshCapacityCutoffMs)).ConfigureAwait(false);

        var allHeadsRunning = snapshot.Heads.All(head => head.IsRunning);
        var allStorageRunning = snapshot.StorageNodes.All(node => node.IsRunning);
        var storageCapacityValid = snapshot.StorageNodes.All(node => node.FreeBytes >= 0);
        var manifestReconciled = await ManifestReconciledAsync(runtime, snapshot, cancellationToken)
            .ConfigureAwait(false);
        var operationalSummary = new RecoveryOperationalSummaryDto(
            MetadataAvailable: true,
            TenantCount: checked((int)tenantCount),
            RunningHeads: snapshot.Heads.Count(head => head.IsRunning),
            TotalHeads: snapshot.Heads.Count,
            RunningStorageNodes: snapshot.StorageNodes.Count(node => node.IsRunning),
            TotalStorageNodes: snapshot.StorageNodes.Count);

        RecoveryGateProbeResult[] gates =
        [
            new("schema_migrations", RecoveryReadinessEvaluator.Passed, "metadata_reachable"),
            tenantCount > 0 && allHeadsRunning && allStorageRunning && storageCapacityValid
                ? new("metadata_invariants", RecoveryReadinessEvaluator.Passed, "local_runtime_consistent")
                : new("metadata_invariants", RecoveryReadinessEvaluator.Failed, "local_runtime_inconsistent"),
            pendingOutbox == 0
                ? new("outbox_reconciliation", RecoveryReadinessEvaluator.Passed, "no_pending_outbox")
                : new("outbox_reconciliation", RecoveryReadinessEvaluator.Failed, "pending_outbox"),
            auditEvents > 0
                ? new("audit_continuity", RecoveryReadinessEvaluator.Passed, "audit_present")
                : new("audit_continuity", RecoveryReadinessEvaluator.Failed, "audit_missing"),
            new("cache_rebuild", RecoveryReadinessEvaluator.Unknown, "not_implemented"),
            manifestReconciled
                ? new("manifest_reconciliation", RecoveryReadinessEvaluator.Passed, "storage_manifests_match_metadata")
                : new("manifest_reconciliation", RecoveryReadinessEvaluator.Failed, "storage_manifest_drift"),
            staleActiveReservations == 0 && committedReservationGaps == 0
                ? new("reservation_reconciliation", RecoveryReadinessEvaluator.Passed, "reservations_reconciled")
                : new("reservation_reconciliation", RecoveryReadinessEvaluator.Failed, "reservation_drift"),
            repairDeficits == 0 && activeRepairJobs == 0
                ? new("repair_deficit", RecoveryReadinessEvaluator.Passed, "no_active_repair_deficit")
                : new("repair_deficit", RecoveryReadinessEvaluator.Failed, "repair_work_pending"),
            missingFreshCapacityReports == 0
                ? new("fresh_capacity_reports", RecoveryReadinessEvaluator.Passed, "capacity_reports_fresh")
                : new("fresh_capacity_reports", RecoveryReadinessEvaluator.Failed, "capacity_reports_stale"),
        ];

        return new RecoveryReadinessProbeSnapshot(operationalSummary, gates);
    }

    private static async Task<bool> ManifestReconciledAsync(
        LocalCluster runtime,
        LocalClusterSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var node in snapshot.StorageNodes)
        {
            var healthyMetadataReplicas = await runtime.ScalarLongAsync(
                """
                SELECT COUNT(*)
                FROM replicas
                WHERE node_id = @node_id
                  AND state = 'healthy';
                """,
                cancellationToken,
                ("@node_id", node.NodeId)).ConfigureAwait(false);
            if (healthyMetadataReplicas != node.Replicas.Count)
            {
                return false;
            }

            foreach (var replica in node.Replicas)
            {
                var metadataMatch = await runtime.ScalarLongAsync(
                    """
                    SELECT COUNT(*)
                    FROM replicas
                    WHERE node_id = @node_id
                      AND version_id = @version_id
                      AND replica_id = @replica_id
                      AND state = 'healthy'
                      AND byte_count = @stored_bytes;
                    """,
                    cancellationToken,
                    ("@node_id", node.NodeId),
                    ("@version_id", replica.VersionId),
                    ("@replica_id", replica.ReplicaId),
                    ("@stored_bytes", replica.StoredBytes)).ConfigureAwait(false);
                if (metadataMatch != 1)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
