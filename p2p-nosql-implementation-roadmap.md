# P2P Object Store V1 Implementation Roadmap

## Slice

This pass turns the canonical architecture into an implementation sequence.

The guiding rule is simple: build the metadata authority and state machine first. Do not start with the storage-agent protocol, P2P transport, or polished API surface. If the metadata rules are vague, every later component will encode slightly different semantics, and repair becomes a data-loss risk instead of a safety mechanism.

Inputs folded into this roadmap:
- SQLite is the v1-alpha metadata authority.
- The first implementation contract is frozen in [p2p-nosql-implementation-contract.md](p2p-nosql-implementation-contract.md): use `Microsoft.Data.Sqlite` with SQLite first, deterministic CBOR envelopes, canonical state labels, agent-local SQLite manifests/journals, 64 MiB whole-object limit, explicit transfer classes, and an early generated local-cluster harness.
- Object versions are immutable.
- Replica, repair, lease, placement-epoch, delete-epoch, and fencing-token rules are canonical.
- Capacity admission is transactional in metadata and checked again locally by storage agents.
- Security authority, signed envelopes, invitations, revocation, audit, and observability are beta blockers.

## Project Build Order

### 1. `Hedgehog.Types`

Shared model project:
- tenant, dataset, object, version, replica, node, lease, repair-job, invitation, key, and audit IDs
- timestamps and epochs
- object/version/replica/repair states
- capacity bucket and reserve types
- protocol error types
- signed-envelope structs

This project must stay boring and stable. Other projects should not invent their own state names or ID formats.

### 2. `Hedgehog.Crypto`

Cryptographic helper project:
- envelope signing
- deterministic CBOR canonical serialization for signed data
- key IDs and key metadata
- invitation token verification helpers
- encryption metadata helpers

Payload encryption remains client-side. This project may define metadata needed to describe encrypted payloads, but it must not make servers plaintext-capable.

### 3. `Hedgehog.Config`

Configuration project:
- node config
- tenant and dataset limits
- metadata database path/DSN handling
- security root config
- capacity reserve policy
- local-cluster config

### 4. `Hedgehog.Metadata.Core`

Pure .NET state-machine project with no database dependency.

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

This project is the heart of the system. The SQL metadata store enforces durable constraints, but `metadata-core` defines legal semantic moves.

### 5. `Hedgehog.Metadata.Sqlite`

SQLite-first SQL implementation of metadata authority.

Owns:
- migrations
- transactional workflows
- transaction boundaries
- guarded updates
- outbox writes
- audit event writes
- idempotency records
- SQLite integration tests

Use `Microsoft.Data.Sqlite` with SQLite for v1-alpha. Do not introduce backend-specific SQL clients in service projects.

### 6. `Hedgehog.StorageAgent`

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

Initial local storage contract:
- file-per-object ciphertext
- agent-local SQLite manifest and command journal
- crash tests for temp-file fsync, atomic rename, duplicate command replay, duplicate final ACK replay, stale fencing rejection, and delete during in-flight write before service networking

### 7. `Hedgehog.Head`

Public head-node service:
- authenticates signed envelopes
- checks authorization through metadata authority
- coordinates write placement
- coordinates reads
- coordinates storage-agent sessions
- emits audit and outbox events
- exposes client/admin APIs

Head nodes are replaceable coordinators, not trust roots.

### 8. `Hedgehog.Repair`

Repair worker project:
- repair scanner
- lease taker
- placement executor
- priority scheduler
- corrupt/suspect/lost replica workflow
- stale replica cleanup

Repair must respect fencing tokens, placement epochs, delete epochs, node revocation epochs, and capacity reserves.

### 9. `Hedgehog.Admin`

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

### 11. `Hedgehog.Observability`

Shared observability project:
- metrics names
- tracing helpers
- health endpoint helpers
- audit event schema helpers
- redaction helpers for logs and metrics

### 12. `Hedgehog.LocalCluster`

Development harness:
- creates or opens the SQLite metadata database
- starts one or more head nodes
- starts storage agents
- starts repair worker
- drives CLI workflows
- supports chaos and restart tests

Deployment contract details are now canonicalized in [p2p-nosql-deployment-stack.md](p2p-nosql-deployment-stack.md). Although this project appears late in the project list, a thin generated Compose harness should start earlier: as soon as `Hedgehog.Metadata.Sqlite` can create tenants, datasets, nodes, and object write intents. Waiting until polished services exist would delay migration, health, metrics, and restart testing too long.

The implementation contract pulls the first thin local-cluster harness into Milestone 1, before real upload streams exist.

## First SQLite Migrations

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
- unique `objects(tenant_id, dataset_id, object_lookup_hash)`
- unique `object_versions(object_id, version_seq)`
- partial unique current/live version index
- unique `replicas(version_id, node_id)`
- partial indexes on `replicas(state)` for `pending`, `healthy`, `suspect`, and `repairing`
- partial index on `repair_jobs(state, priority, available_at)`
- unique `idempotency_records(scope, key)`
- index `outbox_events(state, created_at)`
- tombstone GC eligibility index on `(delete_epoch, retain_until)`

## Metadata-Core Test Harness

`Hedgehog.Metadata.Core` must be database-free and heavily tested before network work begins.

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

Mirror these scenarios in `Hedgehog.Metadata.Sqlite` integration tests using real SQLite transactions.

## Minimal Local Cluster

The first reliable local cluster should include:
- 1 SQLite metadata database
- 1 head node
- 3 storage agents
- 1 repair worker
- 1 admin/CLI process
- optional second head node for idempotency, fencing, and concurrency tests

The first deployment target is a generated local-cluster runtime, with Compose only for services that benefit from containers. The local stack includes a SQLite metadata database, a migrator, one head, three storage agents, one repair worker, admin API/UI, Prometheus, Grafana, and optional OpenTelemetry collector. The metadata database lives in an ignored runtime directory; storage agents keep outbound-only connectivity; generated local secrets also live in ignored runtime state.

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
- projects
- config
- IDs
- errors
- serialization
- test harness
- CI

### Milestone 1: Metadata Schema and Core State Machine

Scope:
- SQLite migrations
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
- metadata restore
- failover drill
- load tests
- security review
- docs

## GitHub Labels

Use:
- `area:metadata`
- `area:metadata-sqlite`
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
- SQLite metadata restore has been tested from backup/export
- metadata migrations have rollback or forward-fix policy documented
- repair backlog drains after one storage-agent failure
- capacity pressure blocks new writes before repair reserve is consumed
- node revocation prevents new writes and schedules affected replicas for repair
- stale fencing tokens and stale epochs are rejected transactionally
- signed-envelope canonicalization has golden tests
- all admin/security actions produce audit events
- outbox lag and stuck events alert correctly
- dashboards exist for object/version/replica/repair/capacity/security health
- generated Compose local cluster can run lifecycle, restart, capacity-pressure, revocation, and restore drills
- chaos tests cover head restart, storage-agent restart, repair-worker restart, and metadata restart/restore simulation

## 2026-06-05 04:23 UTC Deployment Stack Review

Accepted design:
- Compose is the first supported deployment target.
- Kubernetes is deferred until health, metrics, config, and secret contracts are stable.
- The standard local stack includes SQLite metadata, migrator, head, three storage agents, repair worker, admin API/UI, Prometheus, Grafana, and optional OpenTelemetry collector.
- SQLite metadata lives in local runtime storage and is never exposed as a network service.
- Storage agents remain outbound-only and own persistent data, journal, and temp volumes.
- The migrator is a first-class service and uses the same migration path as CI.
- Local-cluster generation belongs in the .NET solution so integration tests, drills, dashboards, and secrets are reproducible.

Risk review:
- Deployment work cannot wait until the end of the project roadmap. A thin local-cluster harness should exist once metadata-sqlite can create tenants, datasets, nodes, and write intents.
- Static YAML alone will drift from code. Generate local Compose files from typed config and commit dashboard/provisioning sources.
- Secrets must be generated into an ignored runtime directory for local development and provided explicitly for beta.
- Health and readiness contracts need to be implemented before service behavior gets complicated, or restart and failure drills will become unreliable.

Next decision:
- Freeze the v1 implementation contract: choose `Microsoft.Data.Sqlite`, deterministic envelope encoding, canonical state glossary, write reservation lifecycle, max object size/transfer classes, and generated local-cluster file layout.

## 2026-06-05 05:04 UTC Implementation Contract Review

Accepted design:
- SQLite access uses `Microsoft.Data.Sqlite` in v1-alpha, with one migration path shared by CI, migrator service, CLI local cluster, and integration tests.
- Signed envelopes use deterministic CBOR with golden vectors before head/client/admin signing workflows.
- Canonical state labels live in `Hedgehog.Types` and must map to SQL values, metrics labels, admin filters, and display labels.
- Write reservations have explicit lifecycle states from `pending` through `leased`, `streaming`, `committed`, release/expiry/conversion, and cleanup-required paths.
- Storage agents start with file-per-object ciphertext plus an agent-local SQLite manifest and journal.
- V1 whole-object writes are capped at 64 MiB with small, medium, and large transfer classes.
- A thin generated local-cluster harness moves into Milestone 1 as soon as metadata-sqlite can run migrations and create early write reservations.

Risk review:
- The next implementation work should be a contract package, not service glue: `Hedgehog.Types`, `Hedgehog.Crypto`, `Hedgehog.Metadata.Core`, `Hedgehog.Metadata.Sqlite`, and a thin local-cluster harness.
- Storage-agent durability cannot wait for network behavior; manifest and journal crash tests are a beta blocker.
- Transfer classes and object size limits are now part of capacity and repair correctness, not tuning polish.

Next decision:
- Write the v1 threat model table covering compromised heads, malicious storage agents, stolen admin keys, invitation leakage, stale cached authority, metadata privacy leakage, capacity-report manipulation, replayed envelopes, manifest corruption, and abusive tenants.

## 2026-06-05 05:07 UTC Threat Model Review

Accepted design:
- The v1 threat model is captured in [p2p-nosql-threat-model.md](p2p-nosql-threat-model.md).
- Threat rows now map actor, capability, target, trust boundary, prevention control, detection signal, and recovery/runbook.
- The table covers compromised heads, malicious storage agents, stolen admin keys, leaked invitations, stale cached authority, metadata privacy leakage, false capacity reports, replayed envelopes, manifest corruption, abusive tenants, metadata operator errors, and .NET async cancellation bugs.

Risk review:
- The first service work must not allow head-local authority decisions. SQLite-backed metadata workflows remain the only path for placement, leases, revocation, invitations, and write visibility.
- Degraded-mode cache behavior is now the largest unresolved security/operations risk because it decides what happens when the metadata store is unavailable but heads still have stale authority records.
- The threat model must become fixtures, not prose: replay vectors, compromised-head bypass tests, capacity-report anomalies, manifest corruption, restore/invariant checks, and cancellation-at-durable-boundary tests.

Next decision:
- Define the degraded-mode authority and cache policy table before implementing head-node metadata caches or outage behavior.

## 2026-06-05 06:04 UTC Degraded-Mode Cache Policy Review

Accepted design:
- The degraded-mode authority policy is captured in [p2p-nosql-degraded-mode-cache-policy.md](p2p-nosql-degraded-mode-cache-policy.md).
- Head nodes have explicit metadata-connectivity states: `normal`, `degraded_read_only`, `authority_stale`, and `recovering`.
- metadata outages are read-mostly and fail-closed. Mutations, admin actions, invitations, capacity admission, write reservations, replica completions, repair leases, and authority-changing workflows are rejected.
- Cached reads are allowed only for already committed, version-specific object versions while tenant, dataset, object visibility, placement, node status, routing, read-token, and revocation records are all fresh enough.
- Revocation is asymmetric: cached deny can block immediately, but cached absence cannot prove allow after the record's max age.
- Audit and outbox remain metadata authority. Heads may buffer denied/status events locally, but cannot allow privileged actions that depend on buffered audit.

Risk review:
- Latest reads during metadata outage remain dangerous because the cached head pointer may miss deletes, newer committed versions, or policy changes.
- Short cache TTLs reduce outage read availability, but v1 chooses correctness and revocation safety over stale-read convenience.
- Recovery needs hard gates: migrations current, invariant checks passed, outbox lag bounded, audit append working, and caches rebuilt from the metadata store.

Next decision:
- Define the .NET project layout and first scaffold package before implementation begins: solution members, ownership boundaries, MSBuild properties, shared error/ID types, migration embedding, deterministic CBOR vector location, storage-agent manifest crash-test boundary, and local-cluster harness ownership.

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

## 2026-06-06 00:04 UTC State Label Reconciliation

Accepted design:
- `p2p-nosql-scaffold-contract.md` and `p2p-nosql-implementation-contract.md` have been reconciled on the same lower-case canonical implementation labels.
- Until .NET code exists, the scaffold contract is the seed source for the validator. Once `Hedgehog.Types` lands, label metadata in that project becomes the executable source of truth.
- `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract` is the first validation command. The earlier `validate-labels` wording is retired.

Next decision:
- Define the `Hedgehog.Types` label metadata API and the `Hedgehog.Xtask` validator seed format, then make the validator fail on drift between docs, .NET enums, SQL values, metrics, dashboards, admin filters, and fixtures.
