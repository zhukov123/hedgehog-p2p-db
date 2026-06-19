PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS capacity_reservations (
    reservation_id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    dataset_id TEXT NOT NULL REFERENCES datasets(dataset_id) ON DELETE CASCADE,
    object_id TEXT NULL REFERENCES objects(object_id) ON DELETE CASCADE,
    version_id TEXT NULL REFERENCES object_versions(version_id) ON DELETE CASCADE,
    replica_id TEXT NULL REFERENCES replicas(replica_id) ON DELETE SET NULL,
    node_id TEXT NOT NULL REFERENCES nodes(node_id) ON DELETE CASCADE,
    lease_id TEXT NULL REFERENCES leases(lease_id) ON DELETE SET NULL,
    reservation_class TEXT NOT NULL DEFAULT 'write'
        CHECK (reservation_class IN ('write', 'repair', 'cleanup', 'snapshot')),
    state TEXT NOT NULL DEFAULT 'pending'
        CHECK (state IN ('pending', 'reserved', 'streaming', 'finalizing', 'committed', 'expired', 'aborted', 'failed_cleanup_required')),
    bytes_reserved INTEGER NOT NULL DEFAULT 0 CHECK (bytes_reserved >= 0),
    placement_epoch INTEGER NOT NULL CHECK (placement_epoch > 0),
    delete_epoch INTEGER NOT NULL DEFAULT 0 CHECK (delete_epoch >= 0),
    fencing_token INTEGER NOT NULL CHECK (fencing_token >= 0),
    created_at_ms INTEGER NOT NULL,
    expires_at_ms INTEGER NOT NULL,
    committed_at_ms INTEGER NULL,
    cleanup_required_at_ms INTEGER NULL,
    metadata TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_capacity_reservations_state_expires
    ON capacity_reservations (state, expires_at_ms);

CREATE INDEX IF NOT EXISTS ix_capacity_reservations_node_state
    ON capacity_reservations (node_id, state);

CREATE INDEX IF NOT EXISTS ix_capacity_reservations_version_state
    ON capacity_reservations (version_id, state);

INSERT INTO labels (domain, wire, display) VALUES
    ('object', 'active', 'Active'),
    ('object', 'delete_marker', 'Delete marker'),
    ('object', 'deleted', 'Deleted'),
    ('object_version', 'writing', 'Writing'),
    ('object_version', 'committed', 'Committed'),
    ('object_version', 'under_replicated', 'Under replicated'),
    ('object_version', 'quarantined', 'Quarantined'),
    ('object_version', 'delete_marker', 'Delete marker'),
    ('object_version', 'gc_eligible', 'GC eligible'),
    ('object_version', 'garbage_collected', 'Garbage collected'),
    ('replica', 'planned', 'Planned'),
    ('replica', 'streaming', 'Streaming'),
    ('replica', 'verifying', 'Verifying'),
    ('replica', 'healthy', 'Healthy'),
    ('replica', 'suspect', 'Suspect'),
    ('replica', 'corrupt', 'Corrupt'),
    ('replica', 'stale', 'Stale'),
    ('replica', 'delete_pending', 'Delete pending'),
    ('replica', 'deleted', 'Deleted'),
    ('lease', 'issued', 'Issued'),
    ('lease', 'completed', 'Completed'),
    ('lease', 'expired', 'Expired'),
    ('lease', 'cancelled', 'Cancelled'),
    ('lease', 'fenced', 'Fenced'),
    ('repair_job', 'pending', 'Pending'),
    ('repair_job', 'leased', 'Leased'),
    ('repair_job', 'running', 'Running'),
    ('repair_job', 'verifying', 'Verifying'),
    ('repair_job', 'completed', 'Completed'),
    ('repair_job', 'retry_wait', 'Retry wait'),
    ('repair_job', 'failed_final', 'Failed final'),
    ('repair_job', 'canceled_superseded', 'Canceled superseded'),
    ('reservation', 'pending', 'Pending'),
    ('reservation', 'reserved', 'Reserved'),
    ('reservation', 'streaming', 'Streaming'),
    ('reservation', 'finalizing', 'Finalizing'),
    ('reservation', 'committed', 'Committed'),
    ('reservation', 'expired', 'Expired'),
    ('reservation', 'aborted', 'Aborted'),
    ('reservation', 'failed_cleanup_required', 'Failed cleanup required'),
    ('capacity_pressure', 'normal', 'Normal'),
    ('capacity_pressure', 'pressure', 'Pressure'),
    ('capacity_pressure', 'critical', 'Critical'),
    ('capacity_pressure', 'emergency', 'Emergency'),
    ('degraded_mode', 'normal', 'Normal'),
    ('degraded_mode', 'degraded_read_only', 'Degraded read only'),
    ('degraded_mode', 'authority_stale', 'Authority stale'),
    ('degraded_mode', 'recovering', 'Recovering'),
    ('node', 'joining', 'Joining'),
    ('node', 'active', 'Active'),
    ('node', 'draining', 'Draining'),
    ('node', 'quarantined', 'Quarantined'),
    ('node', 'revoked', 'Revoked'),
    ('node', 'retired', 'Retired'),
    ('invitation', 'active', 'Active'),
    ('invitation', 'accepted', 'Accepted'),
    ('invitation', 'expired', 'Expired'),
    ('invitation', 'revoked', 'Revoked'),
    ('audit_decision', 'allowed', 'Allowed'),
    ('audit_decision', 'denied', 'Denied'),
    ('audit_decision', 'failed', 'Failed'),
    ('audit_decision', 'replayed', 'Replayed')
ON CONFLICT (domain, wire) DO UPDATE SET
    display = excluded.display;

INSERT INTO workflow_definitions (name, display_order, description) VALUES
    ('create_write_intent', 10, 'Reserve metadata for a new encrypted object version.'),
    ('complete_replica', 20, 'Record a completed replica write and verification transition.'),
    ('commit_version', 30, 'Commit an object version after replica policy is satisfied.'),
    ('delete_marker', 40, 'Append or commit a delete marker version.'),
    ('lease_repair', 50, 'Lease repair work to a node or actor.'),
    ('expire_reservation', 60, 'Expire stale capacity or replica reservations.'),
    ('cleanup_conversion', 70, 'Clean up converted or superseded metadata records.'),
    ('capacity_report', 80, 'Ingest node capacity and pressure information.'),
    ('accept_invite', 90, 'Accept an invitation and create actor or node metadata.'),
    ('revoke_actor_or_node', 100, 'Revoke actor or node access.'),
    ('claim_outbox', 110, 'Claim outbox work for delivery.'),
    ('append_audit_checkpoint', 120, 'Append an audit checkpoint hash.'),
    ('evaluate_recovery_gate', 130, 'Evaluate authority recovery state and gates.')
ON CONFLICT (name) DO UPDATE SET
    display_order = excluded.display_order,
    description = excluded.description;
