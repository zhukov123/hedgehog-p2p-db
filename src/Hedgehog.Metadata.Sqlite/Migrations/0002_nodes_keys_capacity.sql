PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS nodes (
    node_id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NULL REFERENCES tenants(tenant_id) ON DELETE SET NULL,
    display_name TEXT NOT NULL DEFAULT '',
    advertise_endpoint TEXT NULL,
    trust_domain TEXT NOT NULL DEFAULT '',
    public_key_fingerprint TEXT NOT NULL DEFAULT '',
    state TEXT NOT NULL DEFAULT 'joining'
        CHECK (state IN ('joining', 'active', 'draining', 'quarantined', 'revoked', 'retired')),
    capacity_pressure TEXT NOT NULL DEFAULT 'normal'
        CHECK (capacity_pressure IN ('normal', 'pressure', 'critical', 'emergency')),
    degraded_mode TEXT NOT NULL DEFAULT 'normal'
        CHECK (degraded_mode IN ('normal', 'degraded_read_only', 'authority_stale', 'recovering')),
    capacity_bytes INTEGER NOT NULL DEFAULT 0 CHECK (capacity_bytes >= 0),
    used_bytes INTEGER NOT NULL DEFAULT 0 CHECK (used_bytes >= 0),
    reserved_bytes INTEGER NOT NULL DEFAULT 0 CHECK (reserved_bytes >= 0),
    free_bytes INTEGER NOT NULL DEFAULT 0 CHECK (free_bytes >= 0),
    joined_at_ms INTEGER NOT NULL,
    last_seen_at_ms INTEGER NULL,
    retired_at_ms INTEGER NULL,
    metadata TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_nodes_state_last_seen
    ON nodes (state, last_seen_at_ms);

CREATE TABLE IF NOT EXISTS node_keys (
    node_key_id TEXT NOT NULL PRIMARY KEY,
    node_id TEXT NOT NULL REFERENCES nodes(node_id) ON DELETE CASCADE,
    key_id TEXT NOT NULL,
    public_key_fingerprint TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'active'
        CHECK (state IN ('active', 'revoked', 'retired')),
    created_at_ms INTEGER NOT NULL,
    revoked_at_ms INTEGER NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_node_keys_node_key
    ON node_keys (node_id, key_id);

CREATE TABLE IF NOT EXISTS capacity_reports (
    report_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    node_id TEXT NOT NULL REFERENCES nodes(node_id) ON DELETE CASCADE,
    capacity_pressure TEXT NOT NULL
        CHECK (capacity_pressure IN ('normal', 'pressure', 'critical', 'emergency')),
    capacity_bytes INTEGER NOT NULL DEFAULT 0 CHECK (capacity_bytes >= 0),
    used_bytes INTEGER NOT NULL DEFAULT 0 CHECK (used_bytes >= 0),
    reserved_bytes INTEGER NOT NULL DEFAULT 0 CHECK (reserved_bytes >= 0),
    free_bytes INTEGER NOT NULL DEFAULT 0 CHECK (free_bytes >= 0),
    observed_at_ms INTEGER NOT NULL,
    raw_report BLOB NULL
);

CREATE INDEX IF NOT EXISTS ix_capacity_reports_node_observed
    ON capacity_reports (node_id, observed_at_ms DESC);
