PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS recovery_gates (
    gate_id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'open'
        CHECK (state IN ('open', 'acknowledged', 'closed')),
    severity TEXT NOT NULL DEFAULT 'warning'
        CHECK (severity IN ('info', 'warning', 'critical')),
    reason TEXT NOT NULL DEFAULT '',
    required_approvals INTEGER NOT NULL DEFAULT 1 CHECK (required_approvals > 0),
    approvals INTEGER NOT NULL DEFAULT 0 CHECK (approvals >= 0),
    blocks_json TEXT NOT NULL DEFAULT '[]',
    allowed_actions_json TEXT NOT NULL DEFAULT '[]',
    opened_at_ms INTEGER NULL,
    closed_at_ms INTEGER NULL,
    evaluated_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    idempotency_key TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS ix_recovery_gates_state_severity
    ON recovery_gates (state, severity, updated_at_ms DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_recovery_gates_idempotency
    ON recovery_gates (idempotency_key)
    WHERE idempotency_key <> '';
