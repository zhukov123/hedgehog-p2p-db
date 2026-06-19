PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS idempotency_records (
    idempotency_key TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    dataset_id TEXT NULL REFERENCES datasets(dataset_id) ON DELETE CASCADE,
    workflow TEXT NOT NULL REFERENCES workflow_definitions(name),
    actor_id TEXT NULL REFERENCES actors(actor_id) ON DELETE SET NULL,
    request_hash BLOB NOT NULL,
    response_hash BLOB NULL,
    result_state TEXT NOT NULL DEFAULT 'started'
        CHECK (result_state IN ('started', 'completed', 'failed')),
    created_at_ms INTEGER NOT NULL,
    completed_at_ms INTEGER NULL,
    expires_at_ms INTEGER NULL
);

CREATE INDEX IF NOT EXISTS ix_idempotency_records_expires
    ON idempotency_records (expires_at_ms);

CREATE TABLE IF NOT EXISTS outbox_events (
    outbox_id TEXT NOT NULL PRIMARY KEY,
    workflow TEXT NOT NULL REFERENCES workflow_definitions(name),
    destination_node_id TEXT NULL REFERENCES nodes(node_id) ON DELETE SET NULL,
    topic TEXT NOT NULL,
    payload BLOB NOT NULL,
    idempotency_key TEXT NOT NULL DEFAULT '',
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    available_at_ms INTEGER NOT NULL,
    claimed_by TEXT NULL,
    claimed_until_ms INTEGER NULL,
    delivered_at_ms INTEGER NULL,
    created_at_ms INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_outbox_events_available
    ON outbox_events (delivered_at_ms, available_at_ms, claimed_until_ms);

CREATE UNIQUE INDEX IF NOT EXISTS ux_outbox_events_idempotency_key
    ON outbox_events (idempotency_key)
    WHERE idempotency_key <> '';

CREATE TABLE IF NOT EXISTS audit_events (
    audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    workflow TEXT NOT NULL REFERENCES workflow_definitions(name),
    decision TEXT NOT NULL
        CHECK (decision IN ('allowed', 'denied', 'failed', 'replayed')),
    actor_id TEXT NULL REFERENCES actors(actor_id) ON DELETE SET NULL,
    node_id TEXT NULL REFERENCES nodes(node_id) ON DELETE SET NULL,
    object_id TEXT NULL REFERENCES objects(object_id) ON DELETE SET NULL,
    version_id TEXT NULL REFERENCES object_versions(version_id) ON DELETE SET NULL,
    correlation_id TEXT NOT NULL DEFAULT '',
    idempotency_key TEXT NOT NULL DEFAULT '',
    request_hash BLOB NULL,
    previous_checkpoint_hash BLOB NULL,
    checkpoint_hash BLOB NULL,
    encrypted_details BLOB NULL,
    occurred_at_ms INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_audit_events_workflow_occurred
    ON audit_events (workflow, occurred_at_ms DESC);

CREATE INDEX IF NOT EXISTS ix_audit_events_object_occurred
    ON audit_events (object_id, occurred_at_ms DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_audit_events_idempotency_key
    ON audit_events (idempotency_key)
    WHERE idempotency_key <> '';
