using Hedgehog.LocalRuntime;

namespace Hedgehog.LocalRuntime.Api;

public sealed class RecoveryReadinessOptions
{
    public TimeSpan EvaluationTimeout { get; init; } = TimeSpan.FromSeconds(2);
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

internal sealed class LocalRuntimeRecoveryReadinessProbe(LocalCluster runtime) : IRecoveryReadinessProbe
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

        var allHeadsRunning = snapshot.Heads.All(head => head.IsRunning);
        var allStorageRunning = snapshot.StorageNodes.All(node => node.IsRunning);
        var storageCapacityValid = snapshot.StorageNodes.All(node => node.FreeBytes >= 0);
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
            new("manifest_reconciliation", RecoveryReadinessEvaluator.Unknown, "not_implemented"),
            new("reservation_reconciliation", RecoveryReadinessEvaluator.Unknown, "not_implemented"),
            new("repair_deficit", RecoveryReadinessEvaluator.Unknown, "not_implemented"),
            new("fresh_capacity_reports", RecoveryReadinessEvaluator.Unknown, "not_implemented"),
        ];

        return new RecoveryReadinessProbeSnapshot(operationalSummary, gates);
    }
}
