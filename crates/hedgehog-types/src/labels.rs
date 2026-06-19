#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LabelSpec {
    pub domain: &'static str,
    pub wire: &'static str,
    pub display: &'static str,
}

pub const OBJECT_STATES: &[LabelSpec] = &[
    LabelSpec { domain: "object", wire: "active", display: "Active" },
    LabelSpec { domain: "object", wire: "delete_marker", display: "Delete marker" },
    LabelSpec { domain: "object", wire: "deleted", display: "Deleted" },
];

pub const OBJECT_VERSION_STATES: &[LabelSpec] = &[
    LabelSpec { domain: "object_version", wire: "writing", display: "Writing" },
    LabelSpec { domain: "object_version", wire: "committed", display: "Committed" },
    LabelSpec { domain: "object_version", wire: "under_replicated", display: "Under replicated" },
    LabelSpec { domain: "object_version", wire: "quarantined", display: "Quarantined" },
    LabelSpec { domain: "object_version", wire: "delete_marker", display: "Delete marker" },
    LabelSpec { domain: "object_version", wire: "gc_eligible", display: "GC eligible" },
    LabelSpec { domain: "object_version", wire: "garbage_collected", display: "Garbage collected" },
];

pub const REPLICA_STATES: &[LabelSpec] = &[
    LabelSpec { domain: "replica", wire: "planned", display: "Planned" },
    LabelSpec { domain: "replica", wire: "streaming", display: "Streaming" },
    LabelSpec { domain: "replica", wire: "verifying", display: "Verifying" },
    LabelSpec { domain: "replica", wire: "healthy", display: "Healthy" },
    LabelSpec { domain: "replica", wire: "suspect", display: "Suspect" },
    LabelSpec { domain: "replica", wire: "corrupt", display: "Corrupt" },
    LabelSpec { domain: "replica", wire: "stale", display: "Stale" },
    LabelSpec { domain: "replica", wire: "delete_pending", display: "Delete pending" },
    LabelSpec { domain: "replica", wire: "deleted", display: "Deleted" },
];

pub const LEASE_STATES: &[LabelSpec] = &[
    LabelSpec { domain: "lease", wire: "issued", display: "Issued" },
    LabelSpec { domain: "lease", wire: "completed", display: "Completed" },
    LabelSpec { domain: "lease", wire: "expired", display: "Expired" },
    LabelSpec { domain: "lease", wire: "cancelled", display: "Cancelled" },
    LabelSpec { domain: "lease", wire: "fenced", display: "Fenced" },
];

pub const REPAIR_JOB_STATES: &[LabelSpec] = &[
    LabelSpec { domain: "repair_job", wire: "pending", display: "Pending" },
    LabelSpec { domain: "repair_job", wire: "leased", display: "Leased" },
    LabelSpec { domain: "repair_job", wire: "running", display: "Running" },
    LabelSpec { domain: "repair_job", wire: "verifying", display: "Verifying" },
    LabelSpec { domain: "repair_job", wire: "completed", display: "Completed" },
    LabelSpec { domain: "repair_job", wire: "retry_wait", display: "Retry wait" },
    LabelSpec { domain: "repair_job", wire: "failed_final", display: "Failed final" },
    LabelSpec { domain: "repair_job", wire: "canceled_superseded", display: "Canceled superseded" },
];

pub const RESERVATION_STATES: &[LabelSpec] = &[
    LabelSpec { domain: "reservation", wire: "pending", display: "Pending" },
    LabelSpec { domain: "reservation", wire: "reserved", display: "Reserved" },
    LabelSpec { domain: "reservation", wire: "streaming", display: "Streaming" },
    LabelSpec { domain: "reservation", wire: "finalizing", display: "Finalizing" },
    LabelSpec { domain: "reservation", wire: "committed", display: "Committed" },
    LabelSpec { domain: "reservation", wire: "expired", display: "Expired" },
    LabelSpec { domain: "reservation", wire: "aborted", display: "Aborted" },
    LabelSpec { domain: "reservation", wire: "failed_cleanup_required", display: "Failed cleanup required" },
];

pub const CAPACITY_PRESSURE_STATES: &[LabelSpec] = &[
    LabelSpec { domain: "capacity_pressure", wire: "normal", display: "Normal" },
    LabelSpec { domain: "capacity_pressure", wire: "pressure", display: "Pressure" },
    LabelSpec { domain: "capacity_pressure", wire: "critical", display: "Critical" },
    LabelSpec { domain: "capacity_pressure", wire: "emergency", display: "Emergency" },
];

pub const DEGRADED_MODES: &[LabelSpec] = &[
    LabelSpec { domain: "degraded_mode", wire: "normal", display: "Normal" },
    LabelSpec { domain: "degraded_mode", wire: "degraded_read_only", display: "Degraded read only" },
    LabelSpec { domain: "degraded_mode", wire: "authority_stale", display: "Authority stale" },
    LabelSpec { domain: "degraded_mode", wire: "recovering", display: "Recovering" },
];

pub const NODE_STATES: &[LabelSpec] = &[
    LabelSpec { domain: "node", wire: "joining", display: "Joining" },
    LabelSpec { domain: "node", wire: "active", display: "Active" },
    LabelSpec { domain: "node", wire: "draining", display: "Draining" },
    LabelSpec { domain: "node", wire: "quarantined", display: "Quarantined" },
    LabelSpec { domain: "node", wire: "revoked", display: "Revoked" },
    LabelSpec { domain: "node", wire: "retired", display: "Retired" },
];

pub const INVITATION_STATES: &[LabelSpec] = &[
    LabelSpec { domain: "invitation", wire: "active", display: "Active" },
    LabelSpec { domain: "invitation", wire: "accepted", display: "Accepted" },
    LabelSpec { domain: "invitation", wire: "expired", display: "Expired" },
    LabelSpec { domain: "invitation", wire: "revoked", display: "Revoked" },
];

pub const AUDIT_DECISIONS: &[LabelSpec] = &[
    LabelSpec { domain: "audit_decision", wire: "allowed", display: "Allowed" },
    LabelSpec { domain: "audit_decision", wire: "denied", display: "Denied" },
    LabelSpec { domain: "audit_decision", wire: "failed", display: "Failed" },
    LabelSpec { domain: "audit_decision", wire: "replayed", display: "Replayed" },
];

pub const ALL_LABEL_GROUPS: &[&[LabelSpec]] = &[
    OBJECT_STATES,
    OBJECT_VERSION_STATES,
    REPLICA_STATES,
    LEASE_STATES,
    REPAIR_JOB_STATES,
    RESERVATION_STATES,
    CAPACITY_PRESSURE_STATES,
    DEGRADED_MODES,
    NODE_STATES,
    INVITATION_STATES,
    AUDIT_DECISIONS,
];

