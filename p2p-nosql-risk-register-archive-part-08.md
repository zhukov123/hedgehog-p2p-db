# P2P NoSQL Risk Register Archive Part 08

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

## 2026-06-05 12:10 UTC Risk Review

New or sharpened risks:
- The repo now has enough design slices that the main risk is no longer missing ideas; it is enforceability. If the next artifact is not a code-facing scaffold contract, implementers can still choose locally plausible labels, SQL predicates, metric names, cache helpers, and test fixtures that do not compose into one authority model.
- The replication state-machine document still contains pre-contract uppercase states such as `INITIATED`, `AVAILABLE`, `UNDER_REPLICATED`, `PURGED`, `PLANNED`, and `TRANSFER_ASSIGNED`, while the implementation contract freezes lowercase v1 labels such as `writing`, `committed`, `gc_eligible`, `pending`, `streaming`, `healthy`, and `orphaned`. This is now a concrete source of migration, fixture, and admin-label drift.
- PostgreSQL workflow risk remains beta-critical. The design names `sqlx`, transactions, guarded updates, idempotency, outbox, and audit rows, but it does not yet freeze lock order, isolation level per workflow, retry boundaries, idempotency scope, or exact audit/outbox insert timing.
- Metadata recovery is still under-modeled as a single operator-visible state. A primary can recover, migrations can pass, caches can rebuild, and outbox workers can restart while replicas, reservations, audit checkpoints, and agent manifests still disagree.
- Partial writes remain the sharpest replication edge case. Minority fsynced replicas after head crash, reservation expiry, delete epoch bump, revocation, or repair conversion must never become readable, must not leak capacity forever, and must not be garbage-collected before metadata has durable evidence.
- Capacity exhaustion can still cascade through ordering rather than formula mistakes. Emergency reserve, repair reserve, local temp bytes, orphan backlog, tombstone GC, stale capacity reports, and 64 MiB transfer saturation need deterministic priority rules under pressure.
- Head-mediated whole-object transfer makes the head tier part of the durability path. Large upload and repair streams can starve control messages, final ACK processing, outbox publishing, and repair leases even when storage agents and PostgreSQL are healthy.
- Degraded-mode cache helpers are a security boundary. If service code can obtain raw cached authority records, later write, repair, or admin paths can accidentally reinterpret stale records as permission.
- Rust implementation hazards are still concentrated around durable async boundaries: cancellation after temp fsync or rename, blocking fsync on Tokio workers, locks held across `.await`, unbounded repair/outbox queues, unsupervised task panics, and startup reconciliation gaps in the `redb` manifest/journal.

Mitigation ideas:
- Make the next committed design artifact a scaffold contract table with columns for concept, canonical label, owner crate, allowed dependents, forbidden dependents, SQL representation, metric label, admin label, invariant owner, golden/generated source, and fixture path.
- Reconcile the older replication vocabulary immediately: either update `p2p-nosql-replication-repair-state-machine.md` to the implementation-contract glossary or mark its uppercase labels as pre-contract terminology and add a mapping table.
- Add a PostgreSQL workflow matrix for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, and recovery gate checks.
- Treat recovery as a first-class state machine with gates for migration version, invariant checks, audit hash continuity, outbox replay lag, authority cache rebuild, agent manifest reconciliation, repair deficits, capacity reservation reconciliation, and readiness publication.
- Put partial-write and cleanup fixtures before network service code: fsynced minority then expiry, final ACK after expiry, final ACK after delete epoch bump, head crash before commit, repair conversion after interruption, revoked-node cleanup, and orphan release under capacity pressure.
- Define pressure-mode ordering as executable policy: what can consume emergency reserve, when repair may preempt cleanup, when cleanup preempts repair, and when stale metadata admission must lose to local agent hard rejection.
- Reserve head-tier control capacity: separate upload/repair stream concurrency from control RPCs, prioritize final ACK and lease expiry processing, cap 64 MiB transfer classes, and expose head backpressure as a durability metric.
- Make degraded-cache APIs return only typed decisions such as `Fresh<T>`, `Deny(reason)`, or `Unavailable(reason)` on authority-sensitive paths; raw cached records should be limited to read-only status rendering.
- Scaffold Rust guardrails from day one: dedicated blocking disk workers, bounded channels with explicit overflow policy, supervised tasks, replayable outbox publisher, cancellation injection tests, and review/lint checks for locks across `.await`.

Next decision:
- Freeze and commit the Rust scaffold contract and vocabulary reconciliation before any service glue: one ownership map, one state glossary, one PostgreSQL workflow matrix, one recovery state machine, one pressure-mode ordering policy, and one first-wave crash/chaos fixture list.

## 2026-06-05 13:12 UTC Risk Review

New or sharpened risks:
- The open decision is now itself a schedule risk: every additional architecture slice written before the scaffold contract increases the chance that incompatible labels, SQL predicates, metrics, and fixtures become "reasonable" local defaults.
- The state vocabulary conflict remains the clearest implementation trap. The replication/repair state-machine slice still uses pre-contract uppercase states, while the implementation contract freezes lowercase Rust/SQL labels; this can break migrations, admin filters, repair fixtures, and alerts in subtle ways.
- PostgreSQL metadata authority is sound in direction, but still not testable enough. Without a workflow matrix for lock order, isolation, retry scope, idempotency scope, audit timing, and outbox timing, concurrency bugs will surface only after service code exists.
- Recovery is still too easy to split across components. PostgreSQL readiness, head cache freshness, outbox publisher progress, audit continuity, agent manifest reconciliation, repair deficits, and capacity reservation reconciliation need one shared recovery gate.
- Partial-write behavior is the highest replication edge case: minority fsynced replicas after head crash, expiry, revocation, delete epoch bump, or repair conversion must remain unreadable, auditable, capacity-accounted, and cleanup-safe.
- Capacity exhaustion risk is now about priority ordering more than formulas. Emergency reserve, repair reserve, temp bytes, orphan cleanup, tombstone GC, stale capacity reports, and large-object saturation need explicit rules for what work may consume scarce headroom.
- Head-mediated whole-object transfer makes the head tier part of the durability path. Large writes and repair copies can starve control RPCs, final ACK processing, lease expiry, and outbox delivery unless these queues are separated and bounded.
- Degraded-mode cache APIs can become a security bypass if they return raw cached records. Authority-sensitive paths need typed decisions only, or stale tenant, revocation, placement, and routing records will eventually be reused for mutation.
- Rust hazards remain correctness hazards, not style concerns: cancellation after durable disk steps, blocking fsync on Tokio workers, locks across `.await`, unbounded channels, unsupervised task panics, and incomplete `redb` startup reconciliation can leave live services stuck or lying.

Mitigation ideas:
- Stop adding broad design slices until the scaffold contract is committed as a code-facing artifact: ownership map, state glossary, PostgreSQL workflow matrix, recovery state machine, pressure ordering, and fixture list.
- Reconcile the replication/repair terminology now by updating that document to the implementation-contract labels or adding a pre-contract mapping table that tests must not import as canonical state.
- Define the PostgreSQL workflow matrix for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, and recovery gate checks.
- Make recovery a single operator-visible state machine with hard gates for migration version, invariant checks, audit hash continuity, outbox lag, cache rebuild, manifest reconciliation, repair deficits, and capacity reservation reconciliation.
- Add crash fixtures before networking: fsynced minority then expiry, late ACK after expiry, late ACK after delete epoch bump, head crash before commit, interrupted repair conversion, revoked-node cleanup, and orphan release under pressure.
- Define pressure priority as executable policy: when repair preempts cleanup, when cleanup preempts repair, which operations may use emergency reserve, and when local agent hard rejection overrides metadata admission.
- Reserve head control capacity from day one with separate bounded queues for upload/repair streams versus control RPCs, plus priority for final ACK, lease, outbox, and revocation traffic.
- Restrict degraded-cache helpers to `Fresh<T>`, `Deny(reason)`, or `Unavailable(reason)` on authority-sensitive paths; expose raw cached records only to explicitly read-only status rendering.
- Scaffold Rust runtime guardrails early: dedicated blocking disk workers, bounded channels with overflow policy, supervised tasks, replayable outbox publisher, cancellation injection tests, and checks for locks held across `.await`.

Next decision:
- Commit the Rust scaffold contract before any service glue or additional broad architecture work: one canonical ownership map, one reconciled state glossary, one PostgreSQL workflow matrix, one recovery state machine, one pressure-mode ordering policy, and one first-wave crash/chaos fixture list.

## 2026-06-05 14:14 UTC Risk Review

New or sharpened risks:
- The architecture is now at risk of analysis churn. The same blocker has appeared across several reviews: the implementation contract names tools and high-level boundaries, but there is still no code-facing scaffold contract that can prevent local choices from drifting.
- Metadata-plane correctness depends on PostgreSQL workflow details that are still unresolved. A `sqlx` transaction wrapper is not enough without explicit lock order, isolation choices, retry taxonomy, idempotency scope, audit timing, outbox timing, and invariant-check placement.
- The state vocabulary conflict is still the most concrete implementation hazard. Older replication/repair terms remain in the repo while the implementation contract freezes lowercase canonical labels, so generated Rust enums, SQL checks, metrics, admin filters, and fixtures can diverge immediately.
- Degraded-mode cache policy is correctly fail-closed in prose, but helper API ergonomics can still turn it into a hidden authority plane if heads receive raw cached account, revocation, placement, invite, or capacity records.
- Recovery remains a split-brain operator risk. PostgreSQL availability, migration status, outbox lag, audit continuity, cache freshness, agent manifest reconciliation, repair deficit, and capacity reservation reconciliation can each look locally healthy while the cluster is still unsafe for normal traffic.
- Partial writes and late evidence remain the sharpest replication edge. Fsynced minority replicas, late final ACKs after expiry, delete epoch bumps, revocation, repair conversion, or restore must not become readable or be garbage-collected before metadata has classified them.
- Capacity exhaustion can still cascade through scarce-resource ordering. Temp amplification, orphan backlog, tombstone GC, repair reserve, emergency reserve, stale reports, and 64 MiB transfer saturation need priority rules that determine which work may consume the last safe bytes.
- Head nodes are part of the v1 durability path. Large upload and repair streams can starve final ACK handling, lease expiry, revocation checks, outbox publication, and control RPCs unless control capacity is reserved by design.
- Rust runtime hazards are still correctness hazards: blocking fsync on async workers, locks across `.await`, dropped futures after durable side effects, unsupervised task panics, unbounded queues, and incomplete `redb` startup reconciliation can make the service live but untruthful.

Mitigation ideas:
- Stop adding broad architecture slices until the scaffold contract is committed. Make it a table with concept, canonical label, owner crate, allowed dependents, forbidden dependents, SQL representation, metric label, admin label, invariant owner, generated or golden source, and fixture path.
- Add a PostgreSQL workflow matrix covering write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, and recovery gates.
- Reconcile the replication/repair vocabulary now: update the older state-machine document to canonical labels or mark its uppercase labels as historical/pre-contract and forbid tests from importing them.
- Restrict degraded-cache APIs to typed outcomes such as `Fresh<T>`, `Deny(reason)`, and `Unavailable(reason)` for authority-sensitive paths; raw cached records should be available only to read-only status rendering.
- Define one recovery state machine with operator-visible gates for migration version, invariant checks, audit hash continuity, outbox lag, cache rebuild, manifest reconciliation, repair deficit, capacity reservation reconciliation, and readiness publication.
- Add crash and edge fixtures before service networking: minority fsync then expiry, late ACK after expiry, late ACK after delete epoch bump, head crash before commit, interrupted repair conversion, revoked-node cleanup, restore with outbox lag, and orphan release under pressure.
- Define pressure-mode ordering as executable policy: emergency-reserve eligibility, repair versus cleanup priority, cleanup versus tombstone retention, stale metadata admission losing to agent-local hard rejection, and large-object transfer throttles.
- Reserve head control capacity from the first scaffold with separate bounded queues for upload streams, repair streams, control RPCs, final ACKs, lease work, revocation checks, and outbox publishing.
- Make Rust guardrails default in the scaffold: dedicated disk workers or `spawn_blocking`, bounded channels with overflow behavior, supervised task groups, replayable outbox publisher, cancellation injection tests, and checks for locks held across `.await`.

Next decision:
- Do the scaffold-contract slice now, not another broad review: one ownership map, one reconciled state glossary, one PostgreSQL workflow matrix, one degraded-cache API contract, one recovery state machine, one pressure-mode ordering policy, and one first-wave crash/chaos fixture list.

## 2026-06-05 15:16 UTC Risk Review

New or sharpened risks:
- The highest implementation risk remains contract drift, but the failure mode is now specific: the implementation contract freezes `hedgehog-*` crate names, `sqlx`, deterministic CBOR, `redb`, lowercase state labels, transfer classes, and local-cluster expectations, while older design slices still describe earlier crate names and uppercase state machines.
- The replication/repair state-machine document is no longer safe as a direct implementation source without reconciliation. It still uses `INITIATED`, `AVAILABLE`, `UNDER_REPLICATED`, `PURGED`, `PLANNED`, `TRANSFER_ASSIGNED`, `UPLOADING`, and `VERIFYING`, while the contract requires `writing`, `committed`, `gc_eligible`, `garbage_collected`, `pending`, `streaming`, `healthy`, and related lowercase labels.
- PostgreSQL workflow behavior is the main metadata-plane risk left before code. The docs require guarded updates, idempotency, audit, outbox, leases, and invariant checks, but do not yet pin lock order, isolation level, retry boundaries, deadlock handling, command replay scope, and exact audit/outbox write timing per workflow.
- Recovery is still under-specified at the operator boundary. A cluster can have PostgreSQL reachable, migrations current, caches rebuilt, outbox workers running, and agents connected while reservation accounting, manifest reconciliation, audit hash continuity, and repair deficits still disagree.
- Degraded-mode cache policy is strong in prose, but it needs to become a crate-level API contract. Any helper that returns raw cached authority records to head-node service code can become a hidden mutation authority during PostgreSQL outage or recovery.
- Capacity policy has formulas and pressure states, but not enough executable ordering. Under low free space, orphan cleanup, tombstone GC, emergency reserve use, minimum-durability repair, stale capacity rejection, and 64 MiB transfer throttles need deterministic priority fixtures.
- Head-mediated whole-object transfer remains a durability bottleneck. The current transfer-class limits are useful defaults, but final ACKs, lease expiry, revocation checks, outbox delivery, and repair scheduling need reserved control capacity separate from upload and repair streams.
- Storage-agent `redb` manifest and journal behavior is now a beta-critical correctness surface. Startup reconciliation must classify every local byte and journal result as healthy evidence, pending final result, orphan, tombstoned, corrupt, cleanup-required, or unreadable before the agent participates in normal work.
- Rust implementation hazards are concentrated at cancellation and supervision points: fsync or rename followed by dropped futures, blocking disk work on async executors, locks across `.await`, unbounded channels under repair pressure, unsupervised outbox/repair task panics, and non-deterministic time handling around leases and expiry.

Mitigation ideas:
