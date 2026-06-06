# P2P NoSQL Risk Register Archive Part 07

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

- Operational recovery is becoming a single system-level state machine. PostgreSQL restore, migration mismatch, outbox replay, audit hash continuity, cache rebuild, repair deficits, and capacity reservation reconciliation must produce one visible operator path.

Mitigation ideas:
- Make the next design document a scaffold contract with a concept ownership map: IDs, epochs, states, errors, envelopes, migrations, invariants, metric labels, admin labels, and fixture locations each have exactly one owner.
- Define PostgreSQL transaction rules alongside crate layout: lock order, selected isolation levels, retry/backoff policy, idempotency uniqueness, outbox timing, audit timing, and invariant checks run before commit or immediately after recovery.
- Add partial-write fixtures to `metadata-core`/`metadata-pg` before service glue: fsynced minority then expiry, final ACK after expiry, head crash before commit, repair conversion after interruption, cleanup after abort, and revoked-node cleanup.
- Pull capacity chaos into the minimal local cluster: temp volume full, stale report accepted by metadata but rejected by agent, repair reserve exhaustion, tombstone/orphan backlog, and 64 MiB large-object saturation.
- Create `hedgehog-crypto` envelope vectors and a small generator CLI first; reject generic signed-envelope map types unless the wrapper enforces canonical ordering and critical-field behavior.
- Add rejection metrics by degraded-cache record kind so operators can see which authority record blocked a read or mutation.
- Treat runtime hazards as CI gates where practical: Clippy or local linting for locks across `.await`, `spawn_blocking` boundaries for fsync, bounded queue tests, cancellation injection, task-panic supervision, and replayable outbox publishing.
- Model recovery as an explicit state with gates and admin output, not a collection of readiness checks.

Next decision:
- Write the Rust crate-layout and scaffold contract as the next design slice, with exact crate ownership boundaries, PostgreSQL workflow rules, invariant-checker ownership, deterministic CBOR vector tooling, storage-agent crash-test boundary, degraded-cache metric labels, and first local-cluster chaos fixtures.

## 2026-06-05 09:06 UTC Risk Review

New or sharpened risks:
- The first scaffold can still pass "green" while encoding partial truths: Rust enums, SQL constraints, metrics labels, admin labels, and docs must be generated from or checked against one ownership map, or authority drift will reappear immediately.
- PostgreSQL transaction semantics are the most likely metadata-plane failure mode now. Missing lock order, isolation choices, retry boundaries, audit/outbox timing, and idempotency uniqueness can produce double visibility commits, leaked reservations, or repair jobs that never reconcile.
- The degraded-mode cache policy is strict, but its implementation can accidentally become a second control plane if heads expose helper APIs that return cached records instead of typed `Fresh`, `Deny`, or `Unavailable` decisions.
- Recovery after PostgreSQL pause or restore remains under-specified as an operator workflow. A database that accepts connections is not enough; migrations, invariant checks, audit hash continuity, outbox replay, authority cache rebuild, repair deficits, and capacity reservations all need one recovery gate.
- Storage-agent durability is now a security and capacity risk, not just a local storage detail. Cancellation between temp fsync, manifest update, journal final-result write, and ACK replay can create false durability evidence, unreadable bytes, or capacity leaks.
- Partial-write edge cases remain the sharpest replication hazard: a minority of fsynced replicas after head crash, expiry, revocation, or repair conversion must never become readable through repair, hinted replay, or stale cache paths.
- Capacity exhaustion can be triggered by good-faith operations if the local cluster does not test temp amplification, tombstone backlog, orphan backlog, repair reserve exhaustion, stale reports, and 64 MiB transfer saturation before real streaming hides pressure behind queues.
- Deterministic CBOR is still a concrete implementation gap until the wrapper rejects non-canonical maps, unknown critical fields, default-field ambiguity, downgrade, actor/action rebinding, expiry skew, and payload hash mismatch with golden vectors.
- Rust async hazards should be treated as correctness failures: locks across `.await`, fsync on runtime workers, unbounded repair/outbox queues, dropped cancellation cleanup, and unsupervised task panics can leave the system live but unable to make durable progress.

Mitigation ideas:
- Write the scaffold contract as a table, not prose only: concept, owner crate, allowed dependents, forbidden dependents, SQL representation, metric label, admin label, invariant check, and fixture path.
- Define metadata PostgreSQL workflow rules in the same slice: lock order, isolation per workflow, retryable error taxonomy, idempotency-key scope, outbox insert timing, audit insert timing, and pre/post-commit invariant checks.
- Make cache helpers return typed policy decisions only; forbid service crates from receiving raw cached authority records for mutating workflows.
- Add a named recovery state machine with visible gates for migration version, invariant checker, audit append/hash checkpoint, outbox lag, cache rebuild, repair queue reconciliation, and capacity reservation reconciliation.
- Put storage-agent crash tests before networking and inject cancellation after every durable boundary; startup reconciliation must classify local bytes as healthy evidence, orphaned, tombstoned, corrupt, or cleanup-required.
- Add first-class partial-write fixtures: fsynced minority then expiry, late final ACK after delete epoch bump, head crash before commit, repair conversion after interrupted upload, and revoked-node cleanup during recovery.
- Pull capacity chaos into the generated local cluster from day one: temp disk full, stale report accepted then agent-local reject, repair reserve exhausted, tombstone/orphan backlog, and max-size transfer saturation.
- Build `hedgehog-crypto` envelope vectors and generator CLI before service signing code; make generic `serde` map signing unavailable outside the wrapper.
- Add CI gates where practical for `spawn_blocking` fsync boundaries, bounded channels, task supervision, cancellation injection, and replayable outbox publishing.

Next decision:
- Commit to the Rust scaffold contract as the next design artifact, with a single authority ownership map plus PostgreSQL workflow rules and recovery gates; do not scaffold service glue until those boundaries are written.

## 2026-06-05 10:06 UTC Risk Review

New or sharpened risks:
- The implementation contract now names the major crate and storage choices, but the "Next Unresolved Portion" still asks for the same scaffold contract. This creates a planning ambiguity: teams may think the scaffold boundary is settled while the concept ownership map, dependency rules, and recovery gates are not yet concrete enough to code against.
- State-label drift remains the highest Rust-first risk. The risk is no longer just enum-vs-SQL mismatch; object/version/replica labels in the older replication state-machine slice differ from the implementation contract glossary, so migrations, fixtures, admin labels, and metrics can fork before the first crate exists.
- PostgreSQL workflow rules are still underspecified relative to the migration plan. `sqlx` and explicit transactions are chosen, but lock order, isolation levels by workflow, retry boundaries, outbox/audit timing, and idempotency scopes are not yet a testable contract.
- Degraded-mode cache decisions can leak into service ergonomics. If head-node APIs expose cached account, revocation, placement, or routing records directly, later callers may accidentally use stale authority for writes, repair, or admin decisions despite the fail-closed degraded-mode policy.
- Storage-agent durability tests are named, but the durable boundary is still broad. A crash between temp fsync, atomic rename, redb manifest update, journal final-result write, and ACK replay can still produce false durability evidence unless the startup reconciler owns every intermediate state.
- Capacity exhaustion remains tied to execution order. Local agent admission, metadata reservation release, orphan cleanup, repair conversion, and tombstone GC need deterministic ordering under pressure, or cleanup work can consume the emergency headroom needed to escape the incident.
- Whole-object `64 MiB` beta writes are simple but operationally sharp. Hashing, fsync, repair copy, and head-mediated streaming can saturate per-head queues and per-agent worker pools even when byte capacity formulas are correct.
- Recovery is still the place where independent subsystems can tell different truths. A restored PostgreSQL primary, rebuilt head cache, redb manifest, outbox publisher, and Grafana readiness view need one shared recovery state or operators will resume traffic too early.
- Rust async hazards now need mechanical guardrails, not just review discipline. Bounded channels, task supervision, cancellation cleanup, blocking fsync isolation, and "no lock across await" checks should be scaffold defaults.

Mitigation ideas:
- Resolve the implementation-contract ambiguity by writing the scaffold contract as a concrete table: concept, owner crate, allowed dependents, forbidden dependents, SQL representation, metric label, admin label, invariant check, and fixture path.
- Reconcile the state vocabularies before code generation. Either update older slices to the implementation-contract glossary or mark their labels as pre-contract terminology so tests cannot encode both sets.
- Add a PostgreSQL workflow matrix covering write intent, replica completion, commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, and recovery gate checks.
- Make degraded-cache helpers return only `Fresh<T>`, `Deny(reason)`, or `Unavailable(reason)` decisions and forbid raw cached authority records outside read-only rendering paths.
- Define the storage-agent startup reconciler before network service code: every local byte must classify as healthy evidence, pending journal result, orphan, tombstoned, corrupt, or cleanup-required.
- Make pressure-mode fixtures deterministic: temp disk full, emergency reserve only, stale report plus local reject, repair reserve exhausted, orphan backlog, tombstone backlog, and large-object saturation.
- Treat head-mediated transfer limits as correctness controls: reserve control-channel capacity, cap large-object concurrency, and expose repair backpressure separately from node health.
- Model recovery as a single operator-visible state machine with gates for migration version, invariant checks, audit hash continuity, outbox lag, cache rebuild, manifest reconciliation, repair deficits, and capacity reservation reconciliation.
- Put Rust runtime rules in the scaffold: `spawn_blocking` or dedicated workers for fsync, bounded queues with explicit overflow policy, supervised tasks, replayable outbox publishing, cancellation injection tests, and linting where practical for locks across `.await`.

Next decision:
- Freeze the scaffold contract and vocabulary reconciliation before creating service glue: one concept ownership map, one state glossary, one PostgreSQL workflow matrix, one recovery state machine, and one first-wave chaos/crash fixture list.

## 2026-06-05 11:08 UTC Risk Review

New or sharpened risks:
- The architecture decisions are now mostly coherent, but implementation enforceability is still thin. Without a scaffold contract that code, migrations, metrics, admin labels, and tests can all point at, v1 can drift while appearing to follow the docs.
- The current state vocabulary conflict is concrete: the replication state-machine slice uses pre-contract labels like `INITIATED`, `AVAILABLE`, `UNDER_REPLICATED`, and `PURGED`, while the implementation contract uses lowercase stable labels like `writing`, `committed`, `gc_eligible`, and `garbage_collected`.
- PostgreSQL remains the right v1 authority, but concurrent workflow rules are not yet precise enough. Lock order, isolation level, retry scope, idempotency scope, outbox timing, and audit timing need one matrix before migrations and `metadata-pg` workflows are written.
- Recovery is still a metadata-plane risk, not just an ops runbook. A restored database, rebuilt head cache, replayed outbox, reconciled agent manifests, and clean Grafana readiness state can each be true independently while the system is not safe to accept normal traffic.
- Storage-agent startup reconciliation is the highest unresolved durability edge. Every byte found after restart must map to a manifest and journal state, or be classified as orphaned, tombstoned, corrupt, cleanup-required, or unreadable evidence.
- Degraded-mode helpers could accidentally bypass their own policy if they expose raw cached records for convenience. Heads should only receive typed policy decisions for authority-sensitive paths.
- Capacity pressure ordering still needs fixture-backed proof. Emergency reserve, orphan cleanup, tombstone GC, repair conversion, and stale-report rejection can interact badly if cleanup consumes the same headroom needed for minimum-durability repair.
- Whole-object transfer classes mitigate head saturation, but the head tier is still a durability resource in v1. Queue limits, large-object concurrency, repair backpressure, and control-channel priority must be treated as correctness controls, not later tuning.
- Rust hazards remain concentrated at durable async boundaries: cancellation after fsync, locks across `.await`, unbounded queues, task panics in outbox publishers, blocking disk work on runtime workers, and lost cleanup after dropped futures.

Mitigation ideas:
- Write the scaffold contract as a table with concept, owner crate, allowed dependents, forbidden dependents, SQL representation, metric label, admin label, invariant check, fixture path, and generated/golden source where applicable.
- Reconcile or explicitly deprecate pre-contract state labels before scaffolding. Add a doc test or generation check that fails if SQL, Rust enums, metrics, or admin labels introduce undocumented states.
- Add a PostgreSQL workflow matrix for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, and recovery gate checks.
- Define one operator-visible recovery state machine with gates for migration version, invariant checks, audit append/hash continuity, outbox lag, authority cache rebuild, agent manifest reconciliation, repair deficits, and capacity reservation reconciliation.
- Make cache APIs return only `Fresh<T>`, `Deny(reason)`, or `Unavailable(reason)` for authority-sensitive paths; allow raw cached records only in explicitly read-only status rendering.
- Put storage-agent crash and startup reconciliation tests before network service code, injecting cancellation after temp fsync, rename, redb manifest update, journal final-result write, ACK publish, delete marker, and cleanup release.
- Add pressure fixtures to the first local cluster: temp disk full, stale capacity accepted by metadata but rejected locally, repair reserve exhausted, emergency reserve only, orphan backlog, tombstone backlog, and 64 MiB transfer saturation.
- Make runtime guardrails scaffold defaults: `spawn_blocking` or dedicated disk workers, bounded queues with overflow policy, supervised tasks, replayable outbox publishing, cancellation injection, and linting or review checks for locks held across `.await`.

Next decision:
- Write and commit the Rust scaffold contract before code scaffolding: one ownership map, one reconciled state glossary, one PostgreSQL workflow matrix, one recovery state machine, one storage-agent reconciliation boundary, and one first-wave chaos/crash fixture list.

