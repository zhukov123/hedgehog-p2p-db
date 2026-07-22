namespace Hedgehog.Metadata.Core;

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
