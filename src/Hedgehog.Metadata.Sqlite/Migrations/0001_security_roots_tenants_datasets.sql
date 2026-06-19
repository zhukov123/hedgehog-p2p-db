PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS labels (
    domain TEXT NOT NULL,
    wire TEXT NOT NULL,
    display TEXT NOT NULL,
    PRIMARY KEY (domain, wire)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS workflow_definitions (
    name TEXT NOT NULL PRIMARY KEY,
    display_order INTEGER NOT NULL,
    description TEXT NOT NULL DEFAULT ''
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS metadata_store (
    store_id TEXT NOT NULL PRIMARY KEY,
    schema_name TEXT NOT NULL,
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    capacity_pressure TEXT NOT NULL DEFAULT 'normal'
        CHECK (capacity_pressure IN ('normal', 'pressure', 'critical', 'emergency')),
    degraded_mode TEXT NOT NULL DEFAULT 'normal'
        CHECK (degraded_mode IN ('normal', 'degraded_read_only', 'authority_stale', 'recovering')),
    metadata TEXT NULL
);

CREATE TABLE IF NOT EXISTS security_roots (
    security_root_id TEXT NOT NULL PRIMARY KEY,
    key_id TEXT NOT NULL UNIQUE,
    public_key_fingerprint TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'active'
        CHECK (state IN ('active', 'retired', 'revoked')),
    created_at_ms INTEGER NOT NULL,
    retired_at_ms INTEGER NULL,
    revoked_at_ms INTEGER NULL,
    metadata TEXT NULL
);

CREATE TABLE IF NOT EXISTS tenants (
    tenant_id TEXT NOT NULL PRIMARY KEY,
    display_name TEXT NOT NULL DEFAULT '',
    state TEXT NOT NULL DEFAULT 'active'
        CHECK (state IN ('active', 'suspended', 'deleted')),
    security_root_id TEXT NULL REFERENCES security_roots(security_root_id) ON DELETE SET NULL,
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    metadata TEXT NULL
);

CREATE TABLE IF NOT EXISTS datasets (
    dataset_id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    display_name TEXT NOT NULL DEFAULT '',
    lookup_key_id TEXT NOT NULL,
    data_key_id TEXT NOT NULL,
    required_replica_count INTEGER NOT NULL DEFAULT 3 CHECK (required_replica_count > 0),
    state TEXT NOT NULL DEFAULT 'active'
        CHECK (state IN ('active', 'read_only', 'suspended', 'deleted')),
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    metadata TEXT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_datasets_tenant_display
    ON datasets (tenant_id, display_name)
    WHERE display_name <> '';

CREATE TABLE IF NOT EXISTS actors (
    actor_id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    display_name TEXT NOT NULL DEFAULT '',
    actor_kind TEXT NOT NULL DEFAULT 'user'
        CHECK (actor_kind IN ('user', 'admin', 'head', 'agent', 'system')),
    public_key_fingerprint TEXT NOT NULL DEFAULT '',
    state TEXT NOT NULL DEFAULT 'active'
        CHECK (state IN ('active', 'revoked', 'retired')),
    created_at_ms INTEGER NOT NULL,
    revoked_at_ms INTEGER NULL,
    metadata TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_actors_tenant_state
    ON actors (tenant_id, state);

CREATE TABLE IF NOT EXISTS invitations (
    invitation_id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    created_by_actor_id TEXT NULL REFERENCES actors(actor_id) ON DELETE SET NULL,
    accepted_by_actor_id TEXT NULL REFERENCES actors(actor_id) ON DELETE SET NULL,
    state TEXT NOT NULL DEFAULT 'active'
        CHECK (state IN ('active', 'accepted', 'expired', 'revoked')),
    invitee_hint TEXT NOT NULL DEFAULT '',
    token_hash BLOB NOT NULL,
    encrypted_payload BLOB NOT NULL,
    max_uses INTEGER NOT NULL DEFAULT 1 CHECK (max_uses > 0),
    use_count INTEGER NOT NULL DEFAULT 0 CHECK (use_count >= 0),
    created_at_ms INTEGER NOT NULL,
    expires_at_ms INTEGER NULL,
    accepted_at_ms INTEGER NULL,
    revoked_at_ms INTEGER NULL
);

CREATE INDEX IF NOT EXISTS ix_invitations_tenant_state_expires
    ON invitations (tenant_id, state, expires_at_ms);

INSERT INTO metadata_store (
    store_id,
    schema_name,
    created_at_ms,
    updated_at_ms,
    capacity_pressure,
    degraded_mode
)
VALUES (
    'default',
    'v1-alpha',
    CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER),
    CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER),
    'normal',
    'normal'
)
ON CONFLICT (store_id) DO NOTHING;
