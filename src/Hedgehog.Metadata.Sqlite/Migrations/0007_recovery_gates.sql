PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS recovery_gates (
    gate_id TEXT NOT NULL PRIMARY KEY,
    node_id TEXT NOT NULL REFERENCES nodes(node_id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'open'
        CHECK (state IN ('open', 'closed')),
    severity TEXT NOT NULL DEFAULT 'warning'
        CHECK (severity IN ('info', 'warning', 'critical')),
    reason TEXT NOT NULL,
    migrations_current INTEGER NOT NULL DEFAULT 0 CHECK (migrations_current IN (0, 1)),
    invariants_passed INTEGER NOT NULL DEFAULT 0 CHECK (invariants_passed IN (0, 1)),
    outbox_lag_within_threshold INTEGER NOT NULL DEFAULT 0 CHECK (outbox_lag_within_threshold IN (0, 1)),
    audit_append_available INTEGER NOT NULL DEFAULT 0 CHECK (audit_append_available IN (0, 1)),
    authority_cache_rebuilt INTEGER NOT NULL DEFAULT 0 CHECK (authority_cache_rebuilt IN (0, 1)),
    opened_at_ms INTEGER NOT NULL,
    evaluated_at_ms INTEGER NOT NULL,
    closed_at_ms INTEGER NULL,
    idempotency_key TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_recovery_gates_state_severity
    ON recovery_gates (state, severity, evaluated_at_ms DESC);

CREATE INDEX IF NOT EXISTS ix_recovery_gates_node_state
    ON recovery_gates (node_id, state, evaluated_at_ms DESC);
