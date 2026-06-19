# P2P Object Store SQLite-First SQL Schema Plan

## Slice

This pass turns the metadata and replication/repair state-machine design into a concrete SQLite-first SQL schema plan.

Principles:
- the metadata store enforces identity, uniqueness, monotonic epochs, and no-duplicate-active-work invariants.
- Rust `metadata-core` enforces workflow semantics, state transitions, placement policy, and repair priority.
- Storage agents never decide object liveness.
- Triggers should be avoided for the main state machine; use explicit Rust transactions and tests.

SQLite conventions:
- IDs are stored as lowercase text UUIDs unless a later implementation chooses 16-byte BLOB IDs consistently.
- Hashes, signatures, and encrypted blobs are stored as `blob`.
- Timestamps are stored as integer Unix milliseconds.
- Boolean values are stored as integer `0 | 1`.
- JSON-like payloads are stored as canonical JSON text or deterministic CBOR blobs; signed or hashed data should prefer deterministic CBOR.
- `PRAGMA foreign_keys = ON` is required for every connection.

## Tables

### `objects`

Purpose:
- one row per tenant-visible opaque object
- owns the current head pointer, placement epoch, and delete epoch

Key columns:
- `object_id text primary key`
- `tenant_id text not null`
- `dataset_id text not null`
- `object_lookup_hash blob not null`
- `lookup_key_id text not null`
- `encrypted_name_metadata blob null`
- `current_version_id text null`
- `state text not null`: `active | delete_marker | deleted`
- `placement_epoch integer not null default 1`
- `delete_epoch integer not null default 0`
- `created_at_ms integer not null`
- `updated_at_ms integer not null`

Required indexes:
- unique `(tenant_id, dataset_id, object_lookup_hash)`

### `object_versions`

Purpose:
- immutable object-version metadata
- writes create new versions
- deletes create delete-marker versions

Key columns:
- `version_id text primary key`
- `object_id text not null references objects(object_id)`
- `version_no integer not null`
- `state text not null`: `writing | committed | under_replicated | quarantined | delete_marker | gc_eligible | garbage_collected`
- `content_hash blob null`
- `size_bytes integer null`
- `encryption_alg text not null`
- `data_key_id text not null`
- `wrapped_object_data_key blob null`
- `encryption_metadata blob null`
- `placement_epoch integer not null`
- `delete_epoch integer not null default 0`
- `required_replica_count integer not null`
- `committed_at_ms integer null`
- `created_at_ms integer not null`
- `updated_at_ms integer not null`

Required indexes:
- unique `(object_id, version_no)`
- partial unique `(object_id) where state = 'writing'`

### `replicas`

Purpose:
- expected and observed replica lifecycle for a version on a node

Key columns:
- `replica_id text primary key`
- `version_id text not null references object_versions(version_id)`
- `node_id text not null`
- `state text not null`: `planned | streaming | verifying | healthy | suspect | corrupt | stale | delete_pending | deleted`
- `placement_epoch integer not null`
- `delete_epoch integer not null default 0`
- `fencing_token integer not null`
- `byte_count integer null`
- `hash_confirmed integer not null default 0`
- `last_verified_at_ms integer null`
- `created_at_ms integer not null`
- `updated_at_ms integer not null`

Required indexes:
- unique `(version_id, node_id)`
- partial index `(version_id) where state = 'healthy'`
- partial index `(node_id) where state in ('planned', 'streaming', 'verifying', 'healthy', 'suspect')`

### `leases`

Purpose:
- active ownership and fencing for storage-agent and repair work

Key columns:
- `lease_id text primary key`
- `resource_type text not null`
- `resource_id text not null`
- `holder_id text not null`
- `fencing_token integer not null`
- `expires_at_ms integer not null`
- `created_at_ms integer not null`
- `renewed_at_ms integer null`

Required indexes:
- unique `(resource_type, resource_id)`
- index `(expires_at_ms)`

### `repair_jobs`

Purpose:
- deduped and prioritized repair/delete/GC work

Key columns:
- `job_id text primary key`
- `version_id text not null references object_versions(version_id)`
- `replica_id text null references replicas(replica_id)`
- `kind text not null`: `under_replicated | suspect_verify | missing_replace | delete_cleanup | gc`
- `priority int not null`
- `state text not null`: `pending | leased | running | verifying | completed | retry_wait | failed_final | canceled_superseded`
- `attempt_count integer not null default 0`
- `lease_id text null references leases(lease_id)`
- `not_before_ms integer not null`
- `last_error text null`
- `idempotency_key text not null`
- `created_at_ms integer not null`
- `updated_at_ms integer not null`

Required indexes:
- unique `(idempotency_key)`
- partial unique `(version_id, kind) where state in ('pending', 'leased', 'running', 'verifying', 'retry_wait')`
- index `(state, priority desc, not_before_ms, created_at_ms)`

### `tombstones`

Purpose:
- correctness state for deletes, delayed completions, stale repair, and GC

Key columns:
- `tombstone_id text primary key`
- `object_id text not null references objects(object_id)`
- `version_id text null references object_versions(version_id)`
- `delete_epoch integer not null`
- `reason text not null`
- `retain_until_ms integer not null`
- `gc_completed_at_ms integer null`
- `created_at_ms integer not null`

Required indexes:
- unique `(object_id, delete_epoch)`
- partial index `(retain_until_ms) where gc_completed_at_ms is null`

### `idempotency_records`

Purpose:
- dedupe client write/delete requests and internal repair attempts

Key columns:
- `scope text not null`
- `key text not null`
- `request_hash blob not null`
- `status text not null`: `started | succeeded | failed`
- `response_json text null`
- `expires_at_ms integer not null`
- `created_at_ms integer not null`
- `updated_at_ms integer not null`

Required indexes:
- primary key `(scope, key)`
- index `(expires_at_ms)`

### `outbox_events`

Purpose:
- durable bridge from metadata transactions to head workers, storage-agent commands, repair schedulers, audit sinks, and notifications

Key columns:
- `event_id text primary key`
- `aggregate_type text not null`
- `aggregate_id text not null`
- `event_type text not null`
- `payload_json text not null`
- `dedupe_key text not null`
- `published_at_ms integer null`
- `created_at_ms integer not null`

Required indexes:
- unique `(dedupe_key)`
- partial index `(published_at_ms, created_at_ms) where published_at_ms is null`

## Transaction Patterns

### Write Intent

Steps:
1. Insert or check `idempotency_records`.
2. Lock or create the `objects` row by `object_id`, or by `(tenant_id, dataset_id, object_lookup_hash)` for human-name lookup.
3. Insert `object_versions` as `writing` with next `version_no` and current `placement_epoch`.
4. Insert planned `replicas`.
5. Insert `outbox_events` for placement/upload work.
6. Commit.

Notes:
- object creation and first version creation happen in one transaction
- the idempotency response records the chosen `object_id` and `version_id`

### Replica Completion

Steps:
1. Validate idempotency or command identity.
2. Lock `replicas(version_id, node_id)`.
3. Require matching `fencing_token`, `placement_epoch`, and non-stale `delete_epoch`.
4. Transition `planned/streaming/verifying -> healthy`.
5. Record byte count and hash verification.
6. Count healthy replicas inside the same transaction.
7. If required count is met, transition version to `committed` and update `objects.current_version_id`.
8. Insert outbox event.

Core rule:
- stale completions fail closed and may enqueue cleanup

### Delete Marker Creation

Steps:
1. Lock `objects`.
2. Increment `delete_epoch`.
3. Insert an `object_versions` row with `state = 'delete_marker'`.
4. Update object to `delete_marker` or `deleted`.
5. Insert `tombstones`.
6. Mark old live replicas `delete_pending` where `delete_epoch < new_delete_epoch`.
7. Queue delete cleanup jobs and outbox events.

### Repair Leasing

Steps:
1. Select pending job using guarded claim updates.
2. Insert or update `leases` with incremented fencing token.
3. Move job `pending -> leased/running`.
4. Worker includes fencing token on every completion mutation.
5. Completion succeeds only if lease token still matches and lease is unexpired.

### GC Eligibility

Steps:
1. Find tombstones with `retain_until_ms < now_ms`.
2. Confirm no live replicas for old versions except `deleted/corrupt`.
3. Confirm no active repair jobs for affected versions.
4. Confirm version is not `objects.current_version_id`.
5. Mark versions `gc_eligible`.
6. Delete physical metadata in small batches after outbox confirmation.

## SQL Constraints vs Rust Checks

SQL constraints should enforce:
- identity uniqueness
- foreign keys
- non-negative size and count fields
- unique active write intent
- unique active repair job per version/kind
- idempotency uniqueness
- monotonic uniqueness of `(object_id, version_no)` and `(object_id, delete_epoch)`
- replica uniqueness per node/version

Rust `metadata-core` should enforce:
- legal state transitions
- quorum and required-replica semantics
- placement policy
- fencing-token interpretation
- repair priority calculation
- tombstone retention policy
- cross-row invariants too complex for constraints, under row locks

Warning:
- do not bury the state machine in SQL triggers
- keep transition logic in Rust with explicit transactions and deterministic tests

## Beta Migration and Rollback Policy

Rules:
- forward-only migrations during beta
- every migration must be transactional unless it performs a documented concurrent index build
- add nullable columns first, backfill second, enforce `not null` and constraints last
- no destructive migrations without a snapshot and verified restore
- rollback means restore database plus redeploy previous app, not hand-written down migrations for stateful metadata
- binaries refuse to run against unsupported future schema versions

## Backup, Restore, and Recovery Requirements

Before beta:
- local SQLite backup/export and restore tested against a named fixture point
- daily full backup/export during beta
- weekly restore drill into a clean environment during beta
- restart/restore drill proving app reconnect, lease expiry behavior, and no duplicate repair corruption
- backup integrity check through invariant queries
- declared RPO/RTO, with target RPO under 5 minutes
- outbox replay test after restore/failover

Post-restore invariant checks:
- no object has `current_version_id` pointing to a non-committed and non-delete-marker version
- no committed version has fewer than required healthy replicas unless an active repair exists
- no two active repair jobs for the same `(version_id, kind)`
- no stale fencing token can complete a replica or repair
- tombstones are retained long enough to dominate delayed replica completions

## Research Incorporated

This document was originally reviewed as a PostgreSQL schema plan. It is now the SQLite-first SQL schema plan; PostgreSQL-specific production behavior is deferred.

Accepted findings:
- the metadata store should reject identity, race, and duplicate-active-work errors.
- Rust should own state-machine semantics.
- The core tables are `objects`, `object_versions`, `replicas`, `leases`, `repair_jobs`, `tombstones`, `idempotency_records`, and `outbox_events`.
- SQLite repair leasing should use guarded claim updates; PostgreSQL may later use skip-locked leasing behind the same workflow API.
- Recovery drills must prove outbox replay and fencing behavior, not merely database restore.

## Next Unresolved Portion

The capacity admission and repair-reserve slice is captured in `p2p-nosql-capacity-admission.md`.

The security authority slice is captured in `p2p-nosql-security-authority.md`.

The next design slice should define observability and admin operations:
- metrics names aligned to object/version/replica states
- admin dashboard pages and actions
- audit query surfaces
- incident runbooks
- Grafana dashboards
- alert thresholds
- operator workflows for repair, revocation, capacity, and restore
