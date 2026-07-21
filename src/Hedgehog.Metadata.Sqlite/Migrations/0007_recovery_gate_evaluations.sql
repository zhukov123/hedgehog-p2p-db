PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS recovery_gate_evaluations (
    gate_name TEXT NOT NULL PRIMARY KEY,
    status TEXT NOT NULL
        CHECK (status IN ('passed', 'failed', 'unknown')),
    reason TEXT NOT NULL,
    evidence_count INTEGER NOT NULL DEFAULT 0 CHECK (evidence_count >= 0),
    evaluated_at_ms INTEGER NOT NULL,
    idempotency_key TEXT NOT NULL REFERENCES idempotency_records(idempotency_key) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_recovery_gate_evaluations_status
    ON recovery_gate_evaluations (status, evaluated_at_ms DESC);
