using Hedgehog.LocalRuntime;
using Hedgehog.Metadata.Core;

namespace Hedgehog.LocalRuntime.Api;

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
