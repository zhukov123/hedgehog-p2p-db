# P2P NoSQL V1 Implementation Contract

## Slice

This pass freezes the first implementation contract for the Rust-first build.

The goal is to prevent the first crates from quietly becoming separate authorities. `hedgehog-types`, `hedgehog-crypto`, `hedgehog-metadata-core`, `hedgehog-metadata-pg`, storage agents, heads, admin tooling, and the local cluster must share the same database, state names, signature bytes, reservation lifecycle, object size classes, and test posture from the first scaffold.

## Contract Decisions

### PostgreSQL Client

Use `sqlx` for v1.

Reasons:
- compile-time checked SQL fits the explicit migration and transaction-heavy roadmap
- the built-in migration runner keeps CI, the migrator service, and local cluster aligned
- query macros make state-name drift visible early when SQL enum/text values change
- the project does not yet need lower-level protocol control from `tokio-postgres`

Rules:
- do not mix `sqlx` and `tokio-postgres` in v1 service crates
- use explicit transactions for all metadata mutations
- keep query text near the workflow that owns the transaction
- avoid SQL triggers for the main state machine
- use Rust state-transition functions before issuing update statements

`tokio-postgres` remains acceptable only for a later deliberately isolated subsystem with a documented reason.

### Migration Layout

Use a single migrations directory owned by `hedgehog-metadata-pg`:

```text
crates/hedgehog-metadata-pg/migrations/
  0001_security_roots_tenants_datasets.sql
  0002_nodes_keys_capacity.sql
  0003_objects_versions_replicas.sql
  0004_leases_repair_jobs_tombstones.sql
  0005_idempotency_outbox_audit.sql
  0006_capacity_reservations.sql
```

The migrator service, CI tests, CLI local-cluster boot, and integration tests all use this same path through `sqlx::migrate!`.

Migration policy:
- forward-only during beta
- transactional migrations where PostgreSQL allows it
- rollback means restore plus previous binary, not hand-written down migrations
- every migration adds or updates invariant checks in the test harness
- migrations that add states or labels must update the state glossary in `hedgehog-types`

First test database workflow:
1. Start disposable PostgreSQL from generated Compose or `testcontainers`.
2. Run migrations through the same migrator code path.
3. Seed tenant, dataset, admin identity, security root, three nodes, and capacity reports.
4. Run metadata invariant checks.
5. Execute idempotent write-intent, reservation, replica completion, commit, delete marker, repair lease, and outbox replay fixtures.
6. Drop the database.

CI can later add a matrix for PostgreSQL versions, but the first target should be one pinned supported version.

### Metadata Transaction Boundary

`hedgehog-metadata-core` is the semantic authority. `hedgehog-metadata-pg` is the durable authority.

Boundary:
- `metadata-core` defines commands, preconditions, state transitions, invariant checks, and semantic errors
- `metadata-pg` loads rows, locks the needed records, calls `metadata-core`, writes rows, writes idempotency records, writes outbox events, writes audit events, and commits
- service crates call workflow functions in `metadata-pg`, not raw SQL

Recommended shape:

```text
metadata_core::command::{CreateWriteIntent, CompleteReplica, CommitVersion, DeleteObject, LeaseRepair}
metadata_core::decision::{Decision, RowPatch, OutboxIntent, AuditIntent}
metadata_pg::workflow::{create_write_intent, complete_replica, commit_version, delete_object, lease_repair}
```

Do not expose a generic "update replica state" database API to heads, repair workers, admin UI, or storage agents. Every mutation should be a named workflow with idempotency and audit behavior.

### Canonical State Glossary

All v1 state values are defined in `hedgehog-types`. SQL, metrics, admin filters, dashboards, fixture names, and logs must use these exact stable labels unless a display layer explicitly maps them.

This table is reconciled with [p2p-nosql-scaffold-contract.md](p2p-nosql-scaffold-contract.md). The scaffold contract is the seed source until `hedgehog-types` exists; after that, `hedgehog-types` becomes the executable source and both documents must be checked against it.

Object state:
- `active`
- `delete_marker`
- `deleted`

Object version state:
- `writing`
- `committed`
- `under_replicated`
- `quarantined`
- `delete_marker`
- `gc_eligible`
- `garbage_collected`

Replica state:
- `planned`
- `streaming`
- `verifying`
- `healthy`
- `suspect`
- `corrupt`
- `stale`
- `delete_pending`
- `deleted`

Lease state:
- `issued`
- `completed`
- `expired`
- `cancelled`
- `fenced`

Repair job state:
- `pending`
- `leased`
- `running`
- `verifying`
- `completed`
- `retry_wait`
- `failed_final`
- `canceled_superseded`

Reservation state:
- `pending`
- `reserved`
- `streaming`
- `finalizing`
- `committed`
- `expired`
- `aborted`
- `failed_cleanup_required`

Capacity pressure:
- `normal`
- `pressure`
- `critical`
- `emergency`

Degraded mode:
- `normal`
- `degraded_read_only`
- `authority_stale`
- `recovering`

Node state:
- `joining`
- `active`
- `draining`
- `quarantined`
- `revoked`
- `retired`

Invitation state:
- `active`
- `accepted`
- `expired`
- `revoked`

Audit decision:
- `allowed`
- `denied`
- `failed`
- `replayed`

Each label should have:
- Rust enum variant
- SQL accepted value
- metrics label
- admin display label
- legal transition tests

### Write Reservation Lifecycle

Write reservation is the first capacity invariant to implement.

Lifecycle:
1. `pending`: metadata request accepted for evaluation, no durable capacity claim yet.
2. `reserved`: PostgreSQL has reserved logical bytes on selected nodes with placement epoch, delete epoch, and fencing token.
3. `streaming`: at least one selected storage agent accepted the command and passed local physical admission.
4. `finalizing`: enough final results arrived to evaluate commit, abort, or cleanup conversion in PostgreSQL.
5. `committed`: object version reached required healthy replica count and the reservation converted into committed logical bytes.
6. `expired`: lease exceeded max age before commit; no late completion can make the version visible.
7. `aborted`: no committed version is possible and metadata has classified durable side effects as cleanup, orphan, or repair-owned work.
8. `failed_cleanup_required`: metadata cannot safely release all local physical effects until storage-agent cleanup or audit completes.

Rules:
- new writes require both metadata reservation and agent local admission
- reservations are idempotent by tenant, dataset, object key, version intent, and idempotency key
- final replica completion must match reservation id, version id, node id, fencing token, placement epoch, and delete epoch
- expired reservations do not accept late completions
- leaked reservations alert before they consume the repair reserve
- expiry, abort, cleanup classification, and commit write audit and outbox events

First invariant:

```text
reserved_effective_free = healthy_usable_bytes
  - committed_bytes
  - active_write_reservations
  - active_repair_reservations
  - temp_headroom
  - tombstone_gc_lag_bytes
  - orphan_cleanup_lag_bytes
  - snapshot_headroom
  - emergency_reserve
```

A write is admissible only if all selected nodes remain above hard reject after applying the reservation and the cluster still retains required repair reserve.

### Signed Envelope Encoding

Use deterministic CBOR for v1 signed envelopes.

Recommended crate direction:
- `ciborium` or another well-maintained CBOR crate for encoding/decoding
- `serde` only over fixed structs from `hedgehog-types`
- canonical map/key ordering enforced by tests or an explicit wrapper
- no ad hoc JSON canonicalization

Envelope requirements:
- `envelope_version = 1`
- protocol version
- actor id
- actor kind
- tenant id
- key id
- action
- resource scope
- idempotency key
- nonce or request id
- issued-at
- expires-at
- payload hash
- critical fields list

Golden vectors before service code:
- valid admin command
- valid storage-agent rotation request
- valid client write intent
- unknown non-critical field accepted only if outside signed critical set
- unknown critical field rejected
- expired envelope rejected
- future issued-at beyond skew rejected
- downgraded protocol version rejected
- payload hash mismatch rejected
- actor/action rebinding rejected

The signature covers canonical envelope bytes and a domain-separation string such as `hedgehog-v1-envelope`.

### Storage-Agent Manifest And Journal

Use file-per-object ciphertext plus `redb` for the initial storage-agent manifest and command journal.

Layout:

```text
agent-data/
  objects/<tenant>/<dataset>/<version>/<replica>.ciphertext
  temp/
  redb/manifest.redb
  redb/journal.redb
```

Rationale:
- file-per-object keeps whole-object v1 easy to inspect and recover
- `redb` avoids bringing in RocksDB operational weight for volunteer PCs
- the manifest and journal can be crash-tested as a small Rust subsystem before network service behavior

Manifest records:
- replica id
- object/version ids
- local path
- byte length
- content hash
- state
- fencing token
- placement epoch
- delete epoch
- last durable command id
- last final result id

Journal records:
- command id
- command type
- received-at
- fencing token
- expected state
- local admission decision
- durable result
- retry/replay state

Crash-test requirements:
- temp file fsync then atomic rename
- manifest fsync after rename
- duplicate store command replay
- duplicate final ACK replay
- stale fencing rejection
- delete during in-flight write
- restart after journal write but before object rename
- restart after object rename but before final result publish

### Object Size And Transfer Classes

V1 uses whole-object replication with explicit limits.

Initial maximum object size:
- `64 MiB` hard limit for normal beta writes
- larger objects are rejected with `object_too_large`
- chunking and erasure coding remain non-goals for v1

Transfer classes:
- `small`: `0..1 MiB`
- `medium`: `1 MiB..16 MiB`
- `large`: `16 MiB..64 MiB`

Scheduling rules:
- control traffic has priority over all object transfer traffic
- repair has separate per-head and per-agent concurrency from client writes
- large repair copies cannot occupy every repair slot
- capacity admission accounts for temp amplification at least equal to one full object plus manifest/journal overhead
- metrics label transfer class, never object id

Default first limits:
- per-head client upload streams: 64 small, 16 medium, 4 large
- per-head repair streams: 16 small, 4 medium, 1 large
- per-agent store streams: 8 small, 4 medium, 1 large
- per-agent fetch streams: 16 small, 8 medium, 2 large

These are starting constants for tests and local cluster, not production tuning claims.

### Local Cluster Timing

Pull `hedgehog-local-cluster` forward into Milestone 1 as a thin harness.

The first harness exists when `metadata-pg` can:
- run migrations
- create tenant
- create dataset
- register nodes
- accept capacity reports
- create object write intent
- create and expire write reservations
- emit outbox and audit rows

It does not need real upload streams at first.

First generated files:

```text
target/local-cluster/
  compose.yaml
  .env
  secrets/
  postgres/
  grafana/provisioning/
  prometheus/prometheus.yaml
```

Repository-owned sources:

```text
crates/hedgehog-local-cluster/
  src/generate.rs
  templates/
  dashboards/
```

`target/local-cluster/secrets` is generated and ignored. Beta deployments require explicit operator-provided authority material instead of generated development roots.

## First Scaffold Order

1. Create `hedgehog-types` with IDs, epochs, errors, state enums, and glossary tests.
2. Create `hedgehog-crypto` with deterministic CBOR envelope fixtures.
3. Create `hedgehog-metadata-core` with reservation and replica transition tests.
4. Create `hedgehog-metadata-pg` with `sqlx`, migrations, fixtures, and invariant checks.
5. Create thin `hedgehog-local-cluster` migrator/PostgreSQL harness.
6. Add storage-agent manifest/journal crash tests before networked storage-agent service code.

## Beta Blockers Added By This Contract

- `sqlx` migration path works identically in CI, migrator service, CLI local cluster, and tests
- canonical state labels are shared by Rust, SQL, metrics, admin filters, and docs
- deterministic CBOR envelope golden vectors pass before head/client/admin signing flows
- write reservation lifecycle has expiry, release, conversion, and cleanup tests
- local cluster can run first metadata workflows before storage networking exists
- storage-agent manifest/journal crash tests pass before accepting real object uploads

## Next Unresolved Portion

The next design slice should define the Rust crate layout and first scaffold package:
- exact workspace members
- concept ownership map for IDs, states, errors, envelopes, migrations, invariants, metrics, and admin labels
- allowed and forbidden crate dependencies
- feature-flag policy
- PostgreSQL workflow rules for lock order, isolation, retry, idempotency, outbox, and audit writes
- canonical envelope-vector directory and generation command
- storage-agent manifest and journal crash-test boundary
- first local-cluster chaos fixtures for PostgreSQL pause/recover, temp disk full, stale capacity, repair reserve exhaustion, head crash during upload, and agent restart after ACK

This slice should be treated as a scaffold contract, not a prose roadmap. Service glue should wait until these ownership and fixture boundaries are clear.
