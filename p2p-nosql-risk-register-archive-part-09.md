# P2P NoSQL Risk Register Archive Part 09

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

- Make the next committed artifact a real scaffold contract, not another roadmap: concept ownership map, canonical state glossary, allowed/forbidden crate dependencies, SQL representation, metric/admin labels, invariant owners, generated/golden sources, and fixture paths.
- Reconcile or quarantine older terminology before scaffolding. Either update the replication/repair document to the contract labels or add an explicit "historical terminology, not implementation labels" mapping that tests and migrations must not import.
- Add a PostgreSQL workflow matrix for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, outbox claim, and recovery gate checks.
- Turn recovery into one operator-visible state machine with gates for migration version, invariant checks, audit hash continuity, outbox lag, cache rebuild, agent manifest reconciliation, repair deficit, capacity reservation reconciliation, and readiness publication.
- Make degraded-cache APIs return only typed decisions such as `Fresh<T>`, `Deny(reason)`, or `Unavailable(reason)` for authority-sensitive paths; raw cache access belongs only in read-only status rendering.
- Define pressure-mode ordering as executable policy and fixtures: emergency-reserve eligibility, cleanup versus repair priority, stale metadata admission losing to agent-local hard rejection, and large-object transfer throttling under repair pressure.
- Reserve head-node control capacity from the scaffold: separate bounded queues and concurrency budgets for uploads, repairs, control RPCs, final ACKs, lease expiry, revocation, outbox publishing, and admin readiness checks.
- Build `agent-store` crash/startup reconciliation tests before networked storage-agent service code: temp fsync, atomic rename, manifest commit, journal final-result replay, duplicate ACK, late delete, revoked-node cleanup, and orphan release under pressure.
- Bake Rust guardrails into crate templates: dedicated blocking disk workers, bounded channels with explicit overflow behavior, supervised task groups, replayable outbox publishers, cancellation injection tests, and a single test-controlled clock abstraction.

Next decision:
- Commit the scaffold-contract slice before service glue: one reconciled state vocabulary, one crate ownership/dependency map, one PostgreSQL workflow matrix, one degraded-cache API contract, one recovery state machine, one pressure-ordering policy, and one first-wave crash/chaos fixture list.

## 2026-06-05 16:18 UTC Risk Review

New or sharpened risks:
- The risk loop is now repeatedly finding the same blocker, which means the architecture is ready for a code-facing contract rather than more broad prose. Continuing to add slices before that contract will make later Rust scaffolding reconcile documents instead of implementing decisions.
- Metadata authority still has a transaction-shape gap. The design says PostgreSQL plus `metadata-core` is authoritative, but each workflow still needs exact lock order, isolation level, retry rule, idempotency scope, audit insert timing, outbox insert timing, and invariant check placement.
- The replication/repair document remains unsafe as an implementation input because its uppercase state names conflict with the implementation contract's lowercase Rust/SQL labels. This is an immediate migration, metrics, admin-filter, and fixture-drift risk.
- Partial writes remain the highest data-loss edge case. Minority fsynced replicas after head crash, late final ACKs after reservation expiry, delete epoch bumps, node revocation, or repair conversion must be classified deterministically before they can affect reads, capacity, repair debt, or cleanup.
- Recovery has too many local "green" signals. PostgreSQL reachability, migration success, cache rebuild, outbox worker liveness, agent reconnect, manifest reconciliation, capacity reservation reconciliation, and repair deficit calculation need one cluster readiness state.
- Capacity exhaustion now needs deterministic pressure ordering more than another formula. The unresolved question is which operations may consume emergency reserve, when minimum-durability repair preempts cleanup, when cleanup preempts top-up repair, and when local disk rejection overrides metadata admission.
- Degraded-mode cache helpers are a likely security footgun unless their crate API prevents raw cached authority records from reaching mutation paths. Prose fail-closed rules are not enough once head-node code starts composing helpers.
- Head-mediated whole-object transfer is a durability bottleneck. Upload and repair streams can still starve control RPCs, final ACK processing, lease expiry, revocation checks, outbox publishing, and recovery gates unless control capacity is reserved in the scaffold.
- Rust hazards are concentrated where durable side effects meet cancellation: temp fsync and rename, `redb` manifest updates, journal final-result replay, outbox publication, bounded-channel overflow, supervised task shutdown, and clock-controlled lease expiry.

Mitigation ideas:
- Make the next artifact `p2p-nosql-scaffold-contract.md` and keep it code-facing: ownership map, dependency rules, canonical state glossary, SQL labels, metric/admin labels, invariant owner, golden source, and fixture path for each concept.
- Add a PostgreSQL workflow matrix for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, outbox claim, audit append, and recovery gate.
- Reconcile the replication/repair state-machine terminology before tests or migrations exist; either rewrite it to the canonical lowercase labels or add a clear historical mapping that must not be imported by implementation fixtures.
- Define partial-write fixtures as first-wave tests: head crash after one fsynced replica, late ACK after expiry, late ACK after delete epoch bump, revoked-node final result, interrupted repair conversion, restore with outbox lag, and orphan cleanup under critical capacity.
- Turn recovery into a single state machine with gates for migration version, metadata invariants, audit continuity, outbox lag, cache freshness, manifest reconciliation, reservation reconciliation, repair deficit, and readiness publication.
- Make pressure-mode ordering executable policy with fixtures for emergency-reserve eligibility, cleanup versus repair priority, stale capacity rejection, local hard admission rejection, and large-object throttling.
- Design degraded-cache APIs around typed outcomes such as `Fresh<T>`, `Deny(reason)`, and `Unavailable(reason)` on authority-sensitive paths; raw cache access should be limited to read-only status rendering.
- Reserve head control capacity in the first crate templates: separate bounded queues and concurrency budgets for uploads, repairs, control RPCs, final ACKs, lease work, revocation checks, outbox publishing, and admin readiness checks.
- Add Rust runtime guardrails before service glue: dedicated blocking disk workers, supervised task groups, explicit queue overflow policy, replayable outbox publisher, cancellation-injection tests, `redb` startup reconciliation tests, and one test-controlled clock abstraction.

Next decision:
- Stop broad risk churn and commit the scaffold-contract slice next: one ownership/dependency map, one reconciled state glossary, one PostgreSQL workflow matrix, one degraded-cache API contract, one recovery state machine, one pressure-ordering policy, and one first-wave crash/chaos fixture list.

## 2026-06-05 17:20 UTC Risk Review

New or sharpened risks:
- The unresolved risk is no longer discovery; it is decision latency. Repeating the same broad review without committing the scaffold contract increases the chance that the first Rust crates encode whichever document a developer happened to read last.
- Metadata-plane risk is concentrated in PostgreSQL workflow semantics: lock order, isolation level, retry envelope, idempotency key scope, audit append timing, outbox append timing, and invariant checks are still prose-level, not implementation-level.
- The replication/repair state-machine document still conflicts with the implementation contract's lowercase state labels. That is a direct hazard for Rust enums, SQL `CHECK` constraints, migration fixtures, metrics labels, dashboard filters, and chaos tests.
- Partial-write classification remains the main data-loss edge: a minority of fsynced replicas after head crash, late final ACK, delete epoch bump, node revocation, repair conversion, or restore must stay unreadable until metadata reclassifies it.
- Recovery readiness can be falsely green if PostgreSQL, caches, outbox workers, agents, manifests, capacity reservations, and repair deficits each report local health without one authoritative cluster gate.
- Capacity exhaustion needs deterministic priority ordering. Emergency reserve, minimum-durability repair, orphan cleanup, tombstone retention, stale reports, local hard rejection, and large-object transfer throttles must not be left to ad hoc scheduler behavior.
- Degraded-cache helpers can become a hidden security authority if they expose raw cached authority records to mutation paths instead of typed fresh/deny/unavailable decisions.
- Head-mediated whole-object transfer remains an operational bottleneck: data streams can starve final ACKs, lease work, revocation checks, outbox delivery, admin readiness, and repair scheduling unless those paths have reserved queues and budgets.
- Rust hazards remain at durable async boundaries: fsync or rename followed by cancellation, `redb` journal replay after crash, locks across `.await`, blocking disk work on Tokio workers, unbounded channel growth, unsupervised task failure, and non-testable clock behavior.

Mitigation ideas:
- Commit `p2p-nosql-scaffold-contract.md` before service glue. It should be code-facing and include ownership/dependency rules, canonical labels, SQL labels, metric/admin labels, invariant owners, golden-source files, and fixture paths.
- Add the PostgreSQL workflow matrix for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, outbox claim, audit append, and recovery gates.
- Reconcile or quarantine the uppercase replication/repair terminology immediately; implementation tests and migrations should import only the contract labels.
- Make first-wave crash fixtures cover head crash after one fsynced replica, late ACK after expiry, late ACK after delete epoch bump, revoked-node completion, interrupted repair conversion, restore with outbox lag, and orphan cleanup under critical capacity.
- Define one recovery state machine with gates for migration version, metadata invariants, audit continuity, outbox lag, cache freshness, agent manifest reconciliation, reservation reconciliation, repair deficit, and readiness publication.
- Turn pressure ordering into executable policy with fixtures for emergency-reserve eligibility, repair versus cleanup priority, tombstone retention under pressure, stale capacity report rejection, local hard rejection, and large-object throttling.
- Restrict degraded-cache APIs on authority-sensitive paths to `Fresh<T>`, `Deny(reason)`, or `Unavailable(reason)`; keep raw cache reads for read-only status rendering only.
- Reserve head control capacity in the scaffold with separate bounded queues and concurrency limits for uploads, repairs, control RPCs, final ACKs, lease work, revocation checks, outbox publishing, and readiness checks.
- Bake Rust guardrails into the first crates: dedicated disk workers or `spawn_blocking`, supervised task groups, explicit queue overflow behavior, replayable outbox publisher, cancellation-injection tests, `redb` startup reconciliation tests, and one test-controlled clock abstraction.

Next decision:
- Stop the hourly broad-review loop until `p2p-nosql-scaffold-contract.md` exists. The next decision is the scaffold-contract content and owner: state vocabulary, crate dependency map, PostgreSQL workflow matrix, degraded-cache API, recovery gate, pressure-ordering policy, and first crash/chaos fixtures.

## 2026-06-05 18:23 UTC Risk Review

New or sharpened risks:
- The highest risk is now execution drift, not missing architecture. The repo has a frozen implementation contract, but no `p2p-nosql-scaffold-contract.md`; first Rust crates can still encode conflicting labels, dependencies, SQL predicates, metrics, and fixtures.
- Metadata-plane safety still depends on unresolved PostgreSQL workflow details: lock order, isolation level, retry boundary, idempotency scope, audit append timing, outbox append timing, and invariant-check placement per workflow.
- The replication/repair state-machine document remains a direct implementation hazard because its uppercase states conflict with the lowercase contract labels used for Rust, SQL, metrics, admin filters, and tests.
- Partial writes remain the main data-loss edge: minority fsynced replicas, late ACKs after expiry, delete epoch bumps, revocation, repair conversion, restore, and cleanup must be classified before they can influence reads, capacity, or repair.
- Recovery readiness can still go falsely green if PostgreSQL, migrations, caches, outbox workers, agents, manifests, reservations, audit continuity, and repair deficits report separately instead of through one cluster gate.
- Capacity exhaustion risk is unresolved at the scheduler boundary: emergency reserve use, repair versus cleanup priority, tombstone retention, stale capacity reports, local hard rejection, and large-object throttling need deterministic pressure ordering.
- Degraded-cache helpers are a security gap unless authority-sensitive paths receive typed `Fresh`, `Deny`, or `Unavailable` decisions instead of raw cached metadata.
- Head-mediated whole-object transfer can starve durability control traffic unless uploads, repairs, final ACKs, lease expiry, revocation checks, outbox publishing, and readiness checks have separate bounded queues and budgets.
- Rust correctness hazards remain concentrated around durable async boundaries: fsync/rename cancellation, `redb` journal replay, blocking disk work on async executors, locks across `.await`, unbounded queues, unsupervised task failure, and non-testable clocks.

Mitigation ideas:
- Create and commit `p2p-nosql-scaffold-contract.md` before service glue. Make it code-facing: concept owner, allowed/forbidden crate dependencies, canonical labels, SQL labels, metric/admin labels, invariant owner, golden source, and fixture path.
- Add a PostgreSQL workflow matrix for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, outbox claim, audit append, and recovery gates.
- Reconcile or quarantine uppercase replication/repair terminology immediately; migrations and tests should import only contract labels.
- Define first-wave crash fixtures for head crash after one fsynced replica, late ACK after expiry, late ACK after delete epoch bump, revoked-node completion, interrupted repair conversion, restore with outbox lag, and orphan cleanup under critical capacity.
- Define one recovery state machine with gates for migration version, metadata invariants, audit continuity, outbox lag, cache freshness, manifest reconciliation, reservation reconciliation, repair deficit, and readiness publication.
- Make pressure ordering executable, including emergency-reserve eligibility, repair/cleanup priority, stale metadata rejection, local agent hard rejection, tombstone retention, and large-object throttles.
- Restrict degraded-cache APIs on mutation-sensitive paths to typed outcomes; raw cache reads should be read-only status rendering only.
- Put Rust guardrails into the first crate templates: dedicated disk workers or `spawn_blocking`, supervised task groups, explicit queue overflow behavior, replayable outbox publisher, cancellation-injection tests, `redb` startup reconciliation tests, and a test-controlled clock.

Next decision:
- The next run should create the scaffold-contract slice instead of repeating broad risk review: state vocabulary, crate dependency map, PostgreSQL workflow matrix, degraded-cache API contract, recovery gate, pressure-ordering policy, and first crash/chaos fixtures.

## 2026-06-05 19:05 UTC Risk Review

