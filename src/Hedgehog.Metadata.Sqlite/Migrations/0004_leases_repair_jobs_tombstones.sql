PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS leases (
    lease_id TEXT NOT NULL PRIMARY KEY,
    resource_type TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    holder_id TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'issued'
        CHECK (state IN ('issued', 'completed', 'expired', 'cancelled', 'fenced')),
    fencing_token INTEGER NOT NULL CHECK (fencing_token >= 0),
    expires_at_ms INTEGER NOT NULL,
    created_at_ms INTEGER NOT NULL,
    renewed_at_ms INTEGER NULL,
    completed_at_ms INTEGER NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_leases_resource
    ON leases (resource_type, resource_id)
    WHERE state = 'issued';

CREATE INDEX IF NOT EXISTS ix_leases_state_expires
    ON leases (state, expires_at_ms);

CREATE TABLE IF NOT EXISTS repair_jobs (
    job_id TEXT NOT NULL PRIMARY KEY,
    version_id TEXT NOT NULL REFERENCES object_versions(version_id) ON DELETE CASCADE,
    replica_id TEXT NULL REFERENCES replicas(replica_id) ON DELETE SET NULL,
    kind TEXT NOT NULL
        CHECK (kind IN ('under_replicated', 'suspect_verify', 'missing_replace', 'delete_cleanup', 'gc')),
    priority INTEGER NOT NULL,
    state TEXT NOT NULL DEFAULT 'pending'
        CHECK (state IN ('pending', 'leased', 'running', 'verifying', 'completed', 'retry_wait', 'failed_final', 'canceled_superseded')),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    lease_id TEXT NULL REFERENCES leases(lease_id) ON DELETE SET NULL,
    not_before_ms INTEGER NOT NULL,
    last_error TEXT NULL,
    idempotency_key TEXT NOT NULL,
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    completed_at_ms INTEGER NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_repair_jobs_idempotency
    ON repair_jobs (idempotency_key);

CREATE UNIQUE INDEX IF NOT EXISTS ux_repair_jobs_active_version_kind
    ON repair_jobs (version_id, kind)
    WHERE state IN ('pending', 'leased', 'running', 'verifying', 'retry_wait');

CREATE INDEX IF NOT EXISTS ix_repair_jobs_state_priority
    ON repair_jobs (state, priority DESC, not_before_ms, created_at_ms);

CREATE TABLE IF NOT EXISTS tombstones (
    tombstone_id TEXT NOT NULL PRIMARY KEY,
    object_id TEXT NOT NULL REFERENCES objects(object_id) ON DELETE CASCADE,
    version_id TEXT NULL REFERENCES object_versions(version_id) ON DELETE SET NULL,
    delete_epoch INTEGER NOT NULL CHECK (delete_epoch >= 0),
    reason TEXT NOT NULL,
    retain_until_ms INTEGER NOT NULL,
    gc_completed_at_ms INTEGER NULL,
    created_at_ms INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_tombstones_object_delete_epoch
    ON tombstones (object_id, delete_epoch);

CREATE INDEX IF NOT EXISTS ix_tombstones_retain_pending
    ON tombstones (retain_until_ms)
    WHERE gc_completed_at_ms IS NULL;
