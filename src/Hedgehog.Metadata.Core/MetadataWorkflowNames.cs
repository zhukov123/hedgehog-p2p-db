namespace Hedgehog.Metadata.Core;

public static class MetadataWorkflowNames
{
    public const string CreateWriteIntent = "create_write_intent";
    public const string CompleteReplica = "complete_replica";
    public const string CommitVersion = "commit_version";
    public const string DeleteMarker = "delete_marker";
    public const string LeaseRepair = "lease_repair";
    public const string ReconcileReplicaFailure = "reconcile_replica_failure";
    public const string ExpireReservation = "expire_reservation";
    public const string CleanupConversion = "cleanup_conversion";
    public const string CapacityReport = "capacity_report";
    public const string AcceptInvite = "accept_invite";
    public const string RevokeActorOrNode = "revoke_actor_or_node";
    public const string ClaimOutbox = "claim_outbox";
    public const string AppendAuditCheckpoint = "append_audit_checkpoint";
    public const string EvaluateRecoveryGate = "evaluate_recovery_gate";
}
