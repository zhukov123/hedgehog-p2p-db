# P2P NoSQL Risk Register Archive Part 04

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.


Accepted findings:
- The concrete v1 schema centers on `objects`, `object_versions`, `replicas`, `leases`, `repair_jobs`, `tombstones`, `idempotency_records`, and `outbox_events`.
- PostgreSQL should enforce identity, uniqueness, non-negative fields, no duplicate active work, idempotency, foreign keys, and partial unique indexes for active write/repair paths.
- Rust `metadata-core` should own legal state transitions, quorum semantics, placement policy, fencing interpretation, repair priority, and tombstone retention.
- Avoid SQL triggers for the main state machine. Use explicit Rust transactions with row locks and deterministic tests.
- Beta migrations should be forward-only, transactional where possible, and rollback through restore plus previous binary deployment.
- Backup readiness must include WAL archiving, PITR to a named timestamp, weekly restore drills during beta, failover drills, outbox replay tests, and invariant checks after restore.

Risk register changes:
- Added `p2p-nosql-postgresql-schema-plan.md` as the concrete schema bridge from architecture to implementation.
- Raised migration, restore, outbox replay, and stale fencing after failover as beta-blocking risks.
- Moved the next design decision to exact capacity admission and repair-reserve formulas.

## 2026-06-05 02:55 UTC Severus Capacity Review

Accepted findings:
- Capacity admission must be conservative and reservation-based.
- PostgreSQL owns logical capacity reservations, while storage agents enforce physical local disk admission.
- Raw aggregate free bytes are unsafe because placement diversity, node freshness, repair debt, temp amplification, deletion lag, tenant quota, and emergency reserve all matter.
- Track separate buckets for committed bytes, write reservations, repair reservations, temp bytes, GC lag, emergency reserve, unhealthy bytes, and repair debt.
- Effective free capacity must exclude reserved write, repair, temp, GC lag, emergency reserve, and placement-unavailable bytes.
- Repair reserve should cover at least the largest single-node-loss repair need, a configured floor, or a percentage of healthy usable capacity.
- Capacity pressure should prioritize delete markers, orphan/temp cleanup, tombstone-eligible GC, and minimum-durability repair before new writes.
- Stale capacity reports make a node ineligible for new placement.

Risk register changes:
- Added `p2p-nosql-capacity-admission.md` as the canonical capacity slice.
- Raised multidimensional capacity and stale physical reports as top correctness risks.
- Moved the next design decision to security roots and protocol authority.

## 2026-06-05 03:00 UTC Severus Security Authority Review

Accepted findings:
- V1 should use PostgreSQL-backed authority with offline/root admin signing keys and short-lived operational admin tokens.
- Head nodes are not trust roots. They authenticate, rate-limit, verify envelopes, forward mutations, and enforce obvious protocol checks, but PostgreSQL makes final decisions.
- Privileged admin changes require signed envelopes, idempotency keys, scoped roles, and audit rows.
- Invitations are one-time scoped bearer secrets with short expiry, secret hashes, policy hashes, revocation, and transactional acceptance.
- Signed envelope canonicalization must be locked before implementation, preferably deterministic CBOR or strict deterministic protobuf rather than ad hoc JSON.
- Storage-agent revocation increments a revocation epoch, revokes active keys/sessions, blocks new placement, and makes existing replicas suspect until verified or repaired.
- Metadata privacy controls must cover logs, metrics, admin views, APIs, audit, and tracing.
- Incident drills for compromised agents/admins, invite invalidation, head quarantine, revocation-cache rebuild, audit export, and PITR authority consistency are beta blockers.

Risk register changes:
- Added `p2p-nosql-security-authority.md` as the canonical security-authority slice.
- Raised head-node overtrust, weak invite handling, missing canonical signatures, and loose revocation caching as top security risks.
- Moved the next design decision to observability and admin operations against the now-canonical object/version/replica/capacity/security model.

## 2026-06-05 03:05 UTC Severus Observability/Admin Review

Accepted findings:
- PostgreSQL metadata state is the operational source of truth, storage-agent reports are evidence, and outbox/audit logs are the timeline.
- Metrics must align to object/version/replica/lease/repair/capacity/security states, while avoiding object/version IDs as metric labels.
- Required admin pages before beta: cluster overview, objects/versions, replicas, repair, capacity, security/authority, and audit.
- Required Grafana dashboards before beta: cluster SLO, replication health, capacity, storage agents, security, PostgreSQL, and outbox.
- Critical alerts must cover replica deficits, PostgreSQL primary outage, PITR/WAL failure, revoked principal acceptance, stale outbox events, emergency capacity, and restore drill failure.
- Admin actions must go through the same `metadata-core` transactions as normal protocol traffic.
- Beta requires runbooks for repair backlog, capacity pressure, node revocation, head compromise, failed restore, and stale outbox events.

Risk register changes:
- Added `p2p-nosql-admin-observability-ops.md` as the canonical observability/admin slice.
- Raised stale outbox, restore uncertainty, dashboard-derived authority, and admin bypasses as beta-blocking risks.
- Moved the next design decision to implementation roadmap and Rust workspace sequencing.

## 2026-06-05 03:05 UTC Roadmap Risk Review

New or sharpened risks:
- The roadmap is now strong enough to start implementation, but the first code choices can still split invariants across crates if `hedgehog-types`, `hedgehog-crypto`, `hedgehog-metadata-core`, and `hedgehog-metadata-pg` each define their own state, error, serialization, or transaction semantics.
- PostgreSQL remains the right v1 authority, but the roadmap leaves the database access choice open. Mixing `sqlx` and `tokio-postgres`, or starting migrations before choosing the transaction/test approach, would create avoidable integration drag.
- The first migrations are listed, but beta safety depends on migration fixtures, restore checks, invariant checkers, and outbox replay tests being built with the schema rather than after services already depend on it.
- `hedgehog-crypto` is early in the build order, which is correct, but signed-envelope canonicalization must be frozen before API glue exists. Retrofitting canonical bytes after clients, heads, and agents sign messages risks incompatible signatures and downgrade holes.
- `hedgehog-storage-agent` is intentionally later, yet its manifest and command journal are correctness-critical. If local durability is treated as ordinary file plumbing, crash recovery can violate idempotency, fencing, orphan cleanup, and final ACK replay.
- `hedgehog-local-cluster` appears last, but the project needs a thin local-cluster harness earlier than polished admin/observability so metadata, repair, storage-agent restart, and PostgreSQL transaction behavior can be tested together.
- Whole-object replication still needs an explicit v1 maximum object size before capacity fixtures are meaningful; otherwise a single large object can invalidate temp reserve, repair reserve, head bandwidth, and storage-agent worker assumptions.

Mitigation ideas:
- Make `hedgehog-types` the only crate allowed to define canonical state enums, IDs, epochs, and protocol error categories.
- Choose one PostgreSQL client before migrations. Prefer `sqlx` unless there is a concrete reason to need lower-level `tokio-postgres`, because compile-time query checking and migration tooling fit this roadmap.
- Treat migration 1 as a test product: schema, forward migration, seeded fixtures, invariant checker stub, restore/replay notes, and metadata-pg integration tests land together.
- Freeze deterministic signed-envelope encoding and golden vectors before head-node or CLI signing workflows exist.
- Pull a minimal `hedgehog-local-cluster` smoke harness forward once metadata-pg can create tenants, datasets, nodes, and object write intents.
- Create crash tests for storage-agent manifest/journal before adding network service behavior.
- Decide v1 `max_object_size` and transfer classes before capacity admission tests are considered passing.

Next decision:
- Pick the first implementation contract: `sqlx` vs `tokio-postgres`, deterministic envelope encoding, storage-agent manifest store, v1 max object size, and how early the local-cluster harness starts. The highest-leverage first choice is the PostgreSQL access and migration/test stack, because it shapes every metadata-core and metadata-pg boundary.

## Next Design Decision To Resolve

The first implementation contract is now captured in [p2p-nosql-implementation-contract.md](p2p-nosql-implementation-contract.md).

Accepted choices:
- PostgreSQL client: `sqlx`
- signed-envelope encoding: deterministic CBOR
- canonical state labels owned by `hedgehog-types`
