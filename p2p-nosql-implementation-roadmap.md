Exit code: 0
Wall time: 0.5 seconds
Output:
# P2P NoSQL V1 Implementation Roadmap

## Slice

This pass turns the canonical architecture into an implementation sequence.

The guiding rule is simple: build the metadata authority and state machine first. Do not start with the storage-agent protocol, P2P transport, or polished API surface. If the metadata rules are vague, every later component will encode slightly different semantics, and repair becomes a data-loss risk instead of a safety mechanism.

Inputs folded into this roadmap:
- PostgreSQL is the v1 metadata authority.
- Object versions are immutable.
- Replica, repair, lease, placement-epoch, delete-epoch, and fencing-token rules are canonical.
- Capacity admission is transactional in metadata and checked again locally by storage agents.
- Security authority, signed envelopes, invitations, revocation, audit, and observability are beta blockers.

## Crate Build Order

### 1. `hedgehog-types`

Shared model crate:
- tenant, dataset, object, version, replica, node, lease, repair-job, invitation, key, and audit IDs
- timestamps and epochs
- object/version/replica/repair states
- capacity bucket and reserve types
- protocol error types
- signed-envelope structs

This crate must stay boring and stable. Other crates should not invent their own state names or ID formats.

### 2. `hedgehog-crypto`

Cryptographic helper crate:
- envelope signing
- canonical serialization for signed data
- key IDs and key metadata
- invitation token verification helpers
- encryption metadata helpers

Payload encryption remains client-side. This crate may define metadata needed to describe encrypted payloads, but it must not make servers plaintext-capable.

### 3. `hedgehog-config`

Configuration crate:
- node config
- tenant and dataset limits
- PostgreSQL DSN handling
- security root config
- capacity reserve policy
- local-cluster config

### 4. `hedgehog-metadata-core`

Pure Rust state-machine crate with no database dependency.

Owns:
- object/version transitions
- replica transitions
- repair-job transitions
- lease and fencing-token validation
- placement epoch checks
- delete epoch checks
- idempotency semantics
- capacity admission math
- policy validation

This crate is the heart of the system. PostgreSQL enforces durable constraints, but `metadata-core` defines legal semantic moves.

### 5. `hedgehog-metadata-pg`

PostgreSQL implementation of metadata authority.

Owns:
- migrations
- transactional workflows
- row locking
- optional advisory locks
- outbox writes
- audit event writes
- idempotency records
- PostgreSQL integration tests

Use `sqlx` or `tokio-postgres`; pick one before writing migrations and do not mix both in v1.

### 6. `hedgehog-storage-agent`

Participant-machine storage agent:
- local disk store
- durable object write lifecycle
- replica fetch/delete lifecycle
- temp file handling
- local admission checks
- capacity reporting
- key rotation handling
- revocation handling

The agent stores ciphertext and evidence. It is not metadata authority.

### 7. `hedgehog-head`

Public head-node service:
- authenticates signed envelopes
- checks authorization through metadata authority
- coordinates write placement
- coordinates reads
- coordinates storage-agent sessions
- emits audit and outbox events
- exposes client/admin APIs

Head nodes are replaceable coordinators, not trust roots.

### 8. `hedgehog-repair`

Repair worker crate:
- repair scanner
- lease taker
- placement executor
- priority scheduler
- corrupt/suspect/lost replica workflow
- stale replica cleanup

Repair must respect fencing tokens, placement epochs, delete epochs, node revocation epochs, and capacity reserves.

### 9. `hedgehog-admin`

Admin API surface:
- node views and mutation actions
- capacity views
- repair views
- revocation actions
- audit queries
- restore verification hooks

### 10. `hedgehog-cli`

Developer/admin CLI.

Build it early enough to drive integration tests and local-cluster workflows. Do not wait for a polished API to begin CLI work.

### 11. `hedgehog-observability`

Shared observability crate:
- metrics names
- tracing helpers
- health endpoint helpers
- audit event schema helpers
- redaction helpers for logs and metrics

### 12. `hedgehog-local-cluster`

Development harness:
- starts PostgreSQL
- starts one or more head nodes
- starts storage agents
- starts repair worker
- drives CLI workflows
- supports chaos and restart tests

## First PostgreSQL Migrations

Write migrations in this order:

1. `tenants`, `datasets`, `security_roots`
2. `nodes`, `node_keys`, `node_capacity_reports`
3. `objects`
4. `object_versions`
5. `replicas`
6. `leases`
7. `repair_jobs`
8. `tombstones`
9. `idempotency_records`
10. `outbox_events`
11. `audit_events`
12. capacity reservation tables, if the reserve model cannot be fully derived from object/version/replica rows

Early indexes and constraints:
- unique `objects(tenant_id, dataset_id, object_key)`
- unique `object_versions(object_id, version_seq)`
- partial unique current/live version index
- unique `replicas(version_id, node_id)`
- partial indexes on `replicas(state)` for `pending`, `healthy`, `suspect`, and `repairing`
- partial index on `repair_jobs(state, priority, available_at)`
- unique `idempotency_records(scope, key)`
- index `outbox_events(state, created_at)`
- tombstone GC eligibility index on `(delete_epoch, retain_until)`

## Metadata-Core Test Harness

`hedgehog-metadata-core` must be database-free and heavily tested before network work begins.

Required harness:
- table-driven tests for every legal and illegal transition
- property tests for version, replica, repair, and capacity invariants
- model tests for fencing tokens, placement epochs, and delete epochs
- deterministic fake clock
- deterministic ID generator
- simulated concurrent operations represented as ordered transaction intents
- golden tests for signed-envelope canonicalization
- capacity admission fixtures for normal, pressure, reserve breach, and emergency cleanup modes

Core invariant tests:
- no committed object version without the required healthy replica count
- no stale placement epoch can create or heal replicas
- no stale delete epoch can resurrect data
- no revoked node can accept new writes after its revocation epoch
- idempotent retry returns the same semantic result
- repair never reduces durability while trying to improve it
- GC never deletes a replica still referenced by a live version or retained tombstone

Mirror these scenarios in `hedgehog-metadata-pg` integration tests using real PostgreSQL transactions.

## Minimal Local Cluster

The first reliable local cluster should include:
- 1 PostgreSQL instance
- 1 head node
- 3 storage agents
- 1 repair worker
- 1 admin/CLI process
- optional second head node for idempotency, fencing, and concurrency tests

Required local-cluster workflow:

1. Create tenant and dataset.
2. Register storage agents.
3. Upload object.
4. Commit version after replicas complete.
5. Read metadata and fetch object.
6. Delete object.
7. Retain tombstone.
8. Kill one storage agent.
9. Repair restores replication.
10. Run GC after retention window.
11. Inspect audit, outbox, and metrics.

## CLI Workflows

Build these first:

```text
hedgehog init-local
hedgehog tenant create
hedgehog dataset create
hedgehog node register
hedgehog node list
hedgehog put <dataset> <key> <file>
hedgehog get <dataset> <key> <file>
hedgehog stat <dataset> <key>
hedgehog delete <dataset> <key>
hedgehog repair list
hedgehog repair run-once
hedgehog capacity status
hedgehog audit tail
hedgehog outbox list
hedgehog local-cluster up
hedgehog local-cluster down
hedgehog local-cluster status
```

Add after the core lifecycle works:

```text
hedgehog invite create
hedgehog invite revoke
hedgehog key rotate
hedgehog node revoke
hedgehog restore verify
hedgehog gc run-once
```

## Backlog and Milestones

### Milestone 0: Workspace and Foundations

Scope:
- crates
- config
- IDs
- errors
- serialization
- test harness
- CI

### Milestone 1: Metadata Schema and Core State Machine

Scope:
- PostgreSQL migrations
- metadata-core transitions
- SQL constraints
- integration tests

### Milestone 2: Local Cluster Write and Read Path

Scope:
- head node
- storage agents
- object put/get
- replica completion
- idempotent retries

### Milestone 3: Delete, Tombstones, and GC

Scope:
- delete markers
- delete epochs
- tombstone retention
- orphan cleanup
- safe GC

### Milestone 4: Repair and Capacity Admission

Scope:
- repair jobs
- leases
- priority order
- capacity reserves
- pressure behavior

### Milestone 5: Security Root, Invitations, and Audit

Scope:
- admin root
- signed envelopes
- invitations
- key rotation
- revocation
- audit events

### Milestone 6: Observability and Admin Ops

Scope:
- metrics
- dashboards
- alerts
- runbooks
- outbox visibility
- restore drills

### Milestone 7: Beta Hardening

Scope:
- chaos tests
- PITR restore
- failover drill
- load tests
- security review
- docs

## GitHub Labels

Use:
- `area:metadata`
- `area:postgres`
- `area:storage-agent`
- `area:head`
- `area:repair`
- `area:capacity`
- `area:security`
- `area:observability`
- `area:cli`
- `kind:migration`
- `kind:test`
- `kind:runbook`
- `risk:data-loss`
- `risk:security`
- `beta-blocker`

## Beta Exit Criteria

Do not call it beta until all are true:
- full put/get/delete/repair/GC lifecycle works in local cluster
- PostgreSQL PITR restore has been tested from backup
- metadata migrations have rollback or forward-fix policy documented
- repair backlog drains after one storage-agent failure
- capacity pressure blocks new writes before repair reserve is consumed
- node revocation prevents new writes and schedules affected replicas for repair
- stale fencing tokens and stale epochs are rejected transactionally
- signed-envelope canonicalization has golden tests
- all admin/security actions produce audit events
- outbox lag and stuck events alert correctly
- dashboards exist for object/version/replica/repair/capacity/security health
- chaos tests cover head restart, storage-agent restart, repair-worker restart, and PostgreSQL failover simulation

## Research Loop Rule

Every research turn must:
- ask at least one concrete architecture or implementation question
- include enough project context for the external research agent to answer without hidden history
- receive and inspect an answer
- incorporate accepted findings into a repository document
- commit the change locally
- push the accepted design update to GitHub
- queue the next research question

Each external research prompt should include:
- repository name and purpose
- current canonical architecture shape
- accepted v1 decisions relevant to the question
- documents already considered canonical
- the specific question being asked
- required output format
- a request to mention Boromir in the answer

This keeps the project moving like a campaign, not a council that never leaves Rivendell.

