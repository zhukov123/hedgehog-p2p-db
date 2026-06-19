PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS objects (
    object_id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    dataset_id TEXT NOT NULL REFERENCES datasets(dataset_id) ON DELETE CASCADE,
    object_lookup_hash BLOB NOT NULL,
    lookup_key_id TEXT NOT NULL,
    encrypted_name_metadata BLOB NULL,
    current_version_id TEXT NULL,
    state TEXT NOT NULL DEFAULT 'active'
        CHECK (state IN ('active', 'delete_marker', 'deleted')),
    placement_epoch INTEGER NOT NULL DEFAULT 1 CHECK (placement_epoch > 0),
    delete_epoch INTEGER NOT NULL DEFAULT 0 CHECK (delete_epoch >= 0),
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    deleted_at_ms INTEGER NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_objects_tenant_dataset_lookup
    ON objects (tenant_id, dataset_id, object_lookup_hash);

CREATE INDEX IF NOT EXISTS ix_objects_dataset_state_updated
    ON objects (tenant_id, dataset_id, state, updated_at_ms);

CREATE TABLE IF NOT EXISTS object_versions (
    version_id TEXT NOT NULL PRIMARY KEY,
    object_id TEXT NOT NULL REFERENCES objects(object_id) ON DELETE CASCADE,
    version_no INTEGER NOT NULL,
    state TEXT NOT NULL DEFAULT 'writing'
        CHECK (state IN ('writing', 'committed', 'under_replicated', 'quarantined', 'delete_marker', 'gc_eligible', 'garbage_collected')),
    content_hash BLOB NULL,
    size_bytes INTEGER NULL CHECK (size_bytes IS NULL OR size_bytes >= 0),
    encryption_alg TEXT NOT NULL,
    data_key_id TEXT NOT NULL,
    wrapped_object_data_key BLOB NULL,
    encryption_metadata BLOB NULL,
    placement_epoch INTEGER NOT NULL CHECK (placement_epoch > 0),
    delete_epoch INTEGER NOT NULL DEFAULT 0 CHECK (delete_epoch >= 0),
    required_replica_count INTEGER NOT NULL CHECK (required_replica_count > 0),
    committed_at_ms INTEGER NULL,
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_object_versions_object_version_no
    ON object_versions (object_id, version_no);

CREATE UNIQUE INDEX IF NOT EXISTS ux_object_versions_active_writer
    ON object_versions (object_id)
    WHERE state = 'writing';

CREATE INDEX IF NOT EXISTS ix_object_versions_object_state
    ON object_versions (object_id, state, created_at_ms);

CREATE TABLE IF NOT EXISTS replicas (
    replica_id TEXT NOT NULL PRIMARY KEY,
    version_id TEXT NOT NULL REFERENCES object_versions(version_id) ON DELETE CASCADE,
    node_id TEXT NOT NULL REFERENCES nodes(node_id) ON DELETE CASCADE,
    state TEXT NOT NULL DEFAULT 'planned'
        CHECK (state IN ('planned', 'streaming', 'verifying', 'healthy', 'suspect', 'corrupt', 'stale', 'delete_pending', 'deleted')),
    placement_epoch INTEGER NOT NULL CHECK (placement_epoch > 0),
    delete_epoch INTEGER NOT NULL DEFAULT 0 CHECK (delete_epoch >= 0),
    fencing_token INTEGER NOT NULL CHECK (fencing_token >= 0),
    byte_count INTEGER NULL CHECK (byte_count IS NULL OR byte_count >= 0),
    hash_confirmed INTEGER NOT NULL DEFAULT 0 CHECK (hash_confirmed IN (0, 1)),
    storage_ref_ciphertext BLOB NULL,
    last_verified_at_ms INTEGER NULL,
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_replicas_version_node
    ON replicas (version_id, node_id);

CREATE INDEX IF NOT EXISTS ix_replicas_version_healthy
    ON replicas (version_id)
    WHERE state = 'healthy';

CREATE INDEX IF NOT EXISTS ix_replicas_node_active
    ON replicas (node_id)
    WHERE state IN ('planned', 'streaming', 'verifying', 'healthy', 'suspect');
