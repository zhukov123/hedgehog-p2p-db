namespace Hedgehog.Types;

public static class Labels
{
    public static readonly IReadOnlyList<LabelSpec> ObjectStates =
    [
        new("object", "active", "Active"),
        new("object", "delete_marker", "Delete marker"),
        new("object", "deleted", "Deleted"),
    ];

    public static readonly IReadOnlyList<LabelSpec> ObjectVersionStates =
    [
        new("object_version", "writing", "Writing"),
        new("object_version", "committed", "Committed"),
        new("object_version", "under_replicated", "Under replicated"),
        new("object_version", "quarantined", "Quarantined"),
        new("object_version", "delete_marker", "Delete marker"),
        new("object_version", "gc_eligible", "GC eligible"),
        new("object_version", "garbage_collected", "Garbage collected"),
    ];

    public static readonly IReadOnlyList<LabelSpec> ReplicaStates =
    [
        new("replica", "planned", "Planned"),
        new("replica", "streaming", "Streaming"),
        new("replica", "verifying", "Verifying"),
        new("replica", "healthy", "Healthy"),
        new("replica", "suspect", "Suspect"),
        new("replica", "corrupt", "Corrupt"),
        new("replica", "stale", "Stale"),
        new("replica", "delete_pending", "Delete pending"),
        new("replica", "deleted", "Deleted"),
    ];

    public static readonly IReadOnlyList<LabelSpec> LeaseStates =
    [
        new("lease", "issued", "Issued"),
        new("lease", "completed", "Completed"),
        new("lease", "expired", "Expired"),
        new("lease", "cancelled", "Cancelled"),
        new("lease", "fenced", "Fenced"),
    ];

    public static readonly IReadOnlyList<LabelSpec> RepairJobStates =
    [
        new("repair_job", "pending", "Pending"),
        new("repair_job", "leased", "Leased"),
        new("repair_job", "running", "Running"),
        new("repair_job", "verifying", "Verifying"),
        new("repair_job", "completed", "Completed"),
        new("repair_job", "retry_wait", "Retry wait"),
        new("repair_job", "failed_final", "Failed final"),
        new("repair_job", "canceled_superseded", "Canceled superseded"),
    ];

    public static readonly IReadOnlyList<LabelSpec> ReservationStates =
    [
        new("reservation", "pending", "Pending"),
        new("reservation", "reserved", "Reserved"),
        new("reservation", "streaming", "Streaming"),
        new("reservation", "finalizing", "Finalizing"),
        new("reservation", "committed", "Committed"),
        new("reservation", "expired", "Expired"),
        new("reservation", "aborted", "Aborted"),
        new("reservation", "failed_cleanup_required", "Failed cleanup required"),
    ];

    public static readonly IReadOnlyList<LabelSpec> CapacityPressureStates =
    [
        new("capacity_pressure", "normal", "Normal"),
        new("capacity_pressure", "pressure", "Pressure"),
        new("capacity_pressure", "critical", "Critical"),
        new("capacity_pressure", "emergency", "Emergency"),
    ];

    public static readonly IReadOnlyList<LabelSpec> DegradedModes =
    [
        new("degraded_mode", "normal", "Normal"),
        new("degraded_mode", "degraded_read_only", "Degraded read only"),
        new("degraded_mode", "authority_stale", "Authority stale"),
        new("degraded_mode", "recovering", "Recovering"),
    ];

    public static readonly IReadOnlyList<LabelSpec> NodeStates =
    [
        new("node", "joining", "Joining"),
        new("node", "active", "Active"),
        new("node", "draining", "Draining"),
        new("node", "quarantined", "Quarantined"),
        new("node", "revoked", "Revoked"),
        new("node", "retired", "Retired"),
    ];

    public static readonly IReadOnlyList<LabelSpec> InvitationStates =
    [
        new("invitation", "active", "Active"),
        new("invitation", "accepted", "Accepted"),
        new("invitation", "expired", "Expired"),
        new("invitation", "revoked", "Revoked"),
    ];

    public static readonly IReadOnlyList<LabelSpec> AuditDecisions =
    [
        new("audit_decision", "allowed", "Allowed"),
        new("audit_decision", "denied", "Denied"),
        new("audit_decision", "failed", "Failed"),
        new("audit_decision", "replayed", "Replayed"),
    ];

    public static readonly IReadOnlyList<IReadOnlyList<LabelSpec>> AllGroups =
    [
        ObjectStates,
        ObjectVersionStates,
        ReplicaStates,
        LeaseStates,
        RepairJobStates,
        ReservationStates,
        CapacityPressureStates,
        DegradedModes,
        NodeStates,
        InvitationStates,
        AuditDecisions,
    ];
}
