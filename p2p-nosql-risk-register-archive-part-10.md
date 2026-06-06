# P2P NoSQL Risk Register Archive Part 10

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

New or sharpened risks:
- The architecture has crossed from discovery risk into execution risk. Repeating hourly reviews without creating `p2p-nosql-scaffold-contract.md` is now a process failure mode: the first Rust crates may encode conflicting labels, dependency boundaries, SQL predicates, metric labels, fixture names, and admin states from different documents.
- The metadata-plane transaction contract is still too implicit for implementation. `metadata-core` and `metadata-pg` have a clear conceptual split, but write intent, replica completion, commit, delete, repair lease, reservation expiry, revocation, outbox claim, and audit append still need exact lock order, isolation level, retry envelope, idempotency scope, and invariant-check timing.
- The state vocabulary conflict remains an immediate build hazard. `p2p-nosql-replication-repair-state-machine.md` still uses uppercase implementation-looking states while the implementation contract requires lowercase canonical labels in Rust, SQL, metrics, admin filters, and tests.
- Recovery readiness can still be falsely green. PostgreSQL reachability, current migrations, rebuilt caches, running outbox workers, connected agents, reconciled manifests, clean reservation accounting, audit hash continuity, and repair deficit calculation need one authoritative gate before normal traffic resumes.
- Partial-write and late-evidence handling is still the main data-loss edge. Minority fsynced replicas, late ACKs after reservation expiry, delete epoch bumps, revocation, interrupted repair conversion, restore, and cleanup under pressure must be classified before affecting reads, committed capacity, repair deficit, or GC.
- Capacity exhaustion remains unresolved at the ordering boundary. Emergency reserve use, minimum-durability repair, orphan cleanup, tombstone retention, stale capacity reports, local hard rejection, and 64 MiB transfer throttling need deterministic priority rules rather than scheduler-local behavior.
- Degraded-mode cache policy is strong in prose but still needs API-level enforcement. Any raw cached tenant, revocation, placement, invitation, capacity, or object-visibility record that reaches mutation-sensitive head code can become a hidden authority plane during PostgreSQL outage or recovery.
- Head-mediated transfers remain an operational correctness risk. Uploads and repairs can starve final ACK processing, lease expiry, revocation checks, outbox publication, admin readiness, and recovery gates unless control paths have reserved bounded queues and budgets.
- Rust hazards remain concentrated at durable async seams: cancellation after fsync or rename, `redb` manifest/journal replay, blocking disk work on Tokio workers, locks across `.await`, unbounded repair/outbox queues, unsupervised task panics, and non-testable time around leases and expiry.

Mitigation ideas:
- Create and commit `p2p-nosql-scaffold-contract.md` before any service glue. It should be code-facing: concept owner, allowed and forbidden crate dependencies, canonical Rust/SQL/metric/admin labels, invariant owner, golden source, fixture path, and generation or validation command.
- Add the PostgreSQL workflow matrix in that scaffold contract for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, outbox claim, audit append, and recovery gate checks.
- Reconcile or quarantine uppercase replication/repair terminology now; tests, migrations, metrics, and admin filters should import only the contract labels.
- Define first-wave crash and chaos fixtures: head crash after one fsynced replica, late ACK after expiry, late ACK after delete epoch bump, revoked-node final result, interrupted repair conversion, PostgreSQL pause and recover, restore with outbox lag, temp disk full, repair reserve exhausted, and orphan cleanup under critical capacity.
- Make recovery one operator-visible state machine with gates for migration version, metadata invariants, audit continuity, outbox lag, cache freshness, agent manifest reconciliation, reservation reconciliation, repair deficit, and readiness publication.
- Turn pressure ordering into executable policy with fixtures for emergency-reserve eligibility, repair versus cleanup priority, tombstone retention under pressure, stale metadata admission rejection, local agent hard rejection, and large-object throttling.
- Restrict degraded-cache APIs on authority-sensitive paths to typed outcomes such as `Fresh<T>`, `Deny(reason)`, and `Unavailable(reason)`; raw cache reads should be allowed only in read-only status rendering.
- Bake Rust guardrails into the first crate templates: dedicated disk workers or `spawn_blocking`, supervised task groups, explicit bounded-channel overflow behavior, replayable outbox publisher, cancellation-injection tests, `redb` startup reconciliation tests, and one test-controlled clock abstraction.

Next decision:
- Stop the broad-review loop until `p2p-nosql-scaffold-contract.md` is created. The next decision is the scaffold owner and exact content: state vocabulary, crate dependency map, PostgreSQL workflow matrix, degraded-cache API contract, recovery gate, pressure-ordering policy, and first crash/chaos fixtures.

## 2026-06-05 20:05 UTC Risk Review

New or sharpened risks:
- The architecture risk review has converged. The implementation risk is now that the repo keeps accumulating analysis while the first Rust scaffold still lacks a single code-facing contract for labels, crate ownership, SQL workflows, metrics, admin states, and fixtures.
- Metadata-plane correctness remains the highest unresolved implementation detail. PostgreSQL is selected, but write intent, replica completion, commit, delete, repair lease, reservation expiry, revocation, outbox claim, audit append, and recovery gate workflows still need exact lock order, isolation level, retry envelope, idempotency scope, and invariant-check timing.
- The state vocabulary split is still an immediate build hazard. `p2p-nosql-replication-repair-state-machine.md` uses uppercase pre-contract states while `p2p-nosql-implementation-contract.md` requires lowercase canonical Rust/SQL/metric/admin labels.
- Partial-write classification is still the main data-loss edge: minority fsynced replicas, late ACKs after expiry, delete epoch bumps, revocation, interrupted repair conversion, restore, and cleanup under pressure must not affect reads, capacity, repair debt, or GC until metadata classifies them.
- Recovery readiness can still be falsely green. PostgreSQL reachability, migration version, rebuilt caches, outbox workers, connected agents, manifest reconciliation, reservation reconciliation, audit continuity, and repair deficit need one operator-visible readiness state.
- Capacity exhaustion risk is now scheduler-ordering risk. Emergency reserve use, minimum-durability repair, orphan cleanup, tombstone retention, stale report rejection, local agent hard rejection, and 64 MiB transfer throttling need executable priority rules.
- Degraded-cache helpers remain a security gap unless the API shape prevents raw cached tenant, revocation, invitation, placement, capacity, or object-visibility records from reaching mutation-sensitive code.
- Head-mediated whole-object transfer is part of the durability path. Upload and repair streams can starve final ACKs, lease expiry, revocation checks, outbox publication, repair scheduling, and readiness checks without reserved bounded control queues.
- Rust hazards remain concentrated at durable async boundaries: cancellation after fsync or rename, `redb` journal replay, blocking disk work on Tokio workers, locks across `.await`, unbounded channels, unsupervised task failure, and non-testable lease clocks.

Mitigation ideas:
- Create `p2p-nosql-scaffold-contract.md` as the next committed artifact and make it implementation-facing: concept owner, allowed/forbidden dependencies, canonical labels, SQL labels, metric/admin labels, invariant owner, golden source, fixture path, and validation command.
- Include a PostgreSQL workflow matrix for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, outbox claim, audit append, and recovery gate.
- Reconcile or quarantine uppercase replication/repair terminology before migrations, Rust enums, dashboards, or tests import it.
- Define first-wave crash and chaos fixtures: head crash after one fsynced replica, late ACK after expiry, late ACK after delete epoch bump, revoked-node final result, interrupted repair conversion, PostgreSQL pause and recover, restore with outbox lag, temp disk full, repair reserve exhausted, and orphan cleanup under critical capacity.
- Define one recovery state machine with gates for migration version, metadata invariants, audit continuity, outbox lag, cache freshness, manifest reconciliation, reservation reconciliation, repair deficit, and readiness publication.
- Turn pressure ordering into executable policy, including emergency-reserve eligibility, repair versus cleanup priority, tombstone retention under pressure, stale metadata rejection, local hard admission rejection, and large-object throttling.
- Restrict degraded-cache APIs on authority-sensitive paths to typed outcomes such as `Fresh<T>`, `Deny(reason)`, and `Unavailable(reason)`; raw cache access should be read-only status rendering only.
- Reserve head control capacity in the scaffold with separate bounded queues and concurrency limits for uploads, repairs, control RPCs, final ACKs, lease work, revocation checks, outbox publishing, and readiness checks.
- Bake Rust guardrails into the first crate templates: dedicated disk workers or `spawn_blocking`, supervised task groups, explicit bounded-channel overflow behavior, replayable outbox publisher, cancellation-injection tests, `redb` startup reconciliation tests, and one test-controlled clock abstraction.

Next decision:
- Assign the scaffold-contract owner and create `p2p-nosql-scaffold-contract.md` before the next risk-review cycle. No new broad architecture slices should be added until that contract freezes the state vocabulary, crate dependency map, PostgreSQL workflow matrix, degraded-cache API, recovery gate, pressure-ordering policy, and first crash/chaos fixtures.

## 2026-06-05 21:05 UTC Risk Review

New or sharpened risks:
- The dominant risk is now governance drift: the implementation contract names the first Rust boundaries, but the repo still lacks one scaffold contract that implementers can treat as the source of truth for crate ownership, labels, SQL workflows, metrics, admin filters, and fixtures.
- Metadata-plane risk remains the highest correctness risk. PostgreSQL is selected, yet the write intent, replica ACK, version commit, delete marker, repair lease, reservation expiry, revocation, outbox claim, audit append, and recovery-gate workflows still lack exact lock order, isolation level, retry envelope, idempotency scope, and invariant-check placement.
- The replication terminology conflict is still a direct implementation hazard. `p2p-nosql-replication-repair-state-machine.md` uses uppercase states that look executable, while `p2p-nosql-implementation-contract.md` requires lowercase canonical labels across Rust enums, SQL accepted values, metrics, admin filters, and tests.
- Partial-write handling remains the main data-loss edge. A minority of fsynced replicas, late ACKs after lease expiry, delete epoch bumps, node revocation, interrupted repair conversion, restore replay, or cleanup under pressure must be classified before it can affect reads, capacity accounting, repair debt, or GC.
- Recovery readiness can still be falsely green because PostgreSQL reachability, migration version, cache rebuild, outbox liveness, agent reconnect, `redb` manifest reconciliation, reservation reconciliation, audit continuity, and repair deficit can each report success while the cluster is not safe for normal traffic.
- Capacity exhaustion is now an execution-ordering risk. Emergency reserve eligibility, minimum-durability repair, orphan cleanup, tombstone retention, stale capacity reports, local hard rejection, and large-object throttling need executable priority rules instead of service-local scheduler decisions.
- Degraded-mode cache policy is not yet API-enforced. Raw cached tenant, revocation, invitation, placement, capacity, or visibility records exposed to mutation-sensitive code can become a hidden authority plane during PostgreSQL outage or recovery.
- Head-mediated whole-object transfer can still starve durability control traffic. Uploads and repairs need separate budgets from final ACKs, lease expiry, revocation checks, outbox delivery, repair scheduling, and readiness checks.
- Rust hazards remain concentrated at durable async boundaries: cancellation after fsync or rename, blocking disk work on Tokio workers, locks held across `.await`, unbounded repair/outbox queues, unsupervised task panics, non-testable clocks, and incomplete `redb` journal replay after crashes.

Mitigation ideas:
- Create and commit `p2p-nosql-scaffold-contract.md` as the next artifact, with concept owners, allowed/forbidden dependencies, canonical Rust/SQL/metric/admin labels, invariant owners, golden-source files, fixture paths, and validation commands.
- Put a PostgreSQL workflow matrix in that contract for write intent, replica completion, version commit, delete marker, repair lease, reservation expiry, cleanup conversion, capacity report, invite acceptance, revocation, outbox claim, audit append, and recovery-gate checks.
- Reconcile or quarantine the uppercase replication/repair document before any migration, enum, dashboard, or fixture imports those names.
- Define first-wave crash and chaos fixtures for head crash after one fsynced replica, late ACK after expiry, late ACK after delete epoch bump, revoked-node final result, interrupted repair conversion, PostgreSQL pause/recover, restore with outbox lag, temp disk full, repair reserve exhausted, and orphan cleanup under critical capacity.
- Make recovery one operator-visible state machine with gates for migration version, metadata invariants, audit hash continuity, outbox lag, cache freshness, agent manifest reconciliation, reservation reconciliation, repair deficit, and readiness publication.
- Turn pressure ordering into executable policy, including emergency-reserve eligibility, repair versus cleanup priority, tombstone retention under pressure, stale metadata rejection, local agent hard admission rejection, and large-object throttling.
- Restrict degraded-cache APIs on authority-sensitive paths to typed outcomes such as `Fresh<T>`, `Deny(reason)`, and `Unavailable(reason)`; keep raw cache reads for read-only status rendering only.
- Reserve head control capacity in the scaffold with separate bounded queues and concurrency limits for uploads, repairs, control RPCs, final ACKs, lease work, revocation checks, outbox publishing, and readiness checks.
- Add Rust guardrails to the first crate templates: dedicated disk workers or `spawn_blocking`, supervised task groups, explicit bounded-channel overflow behavior, replayable outbox publisher, cancellation-injection tests, `redb` startup reconciliation tests, and one test-controlled clock abstraction.

Next decision:
- Stop repeating the broad hourly risk loop until the scaffold contract exists. The next decision is to name the owner and create `p2p-nosql-scaffold-contract.md` with the state vocabulary, crate dependency map, PostgreSQL workflow matrix, degraded-cache API, recovery gate, pressure-ordering policy, and first crash/chaos fixtures.

## 2026-06-05 22:05 UTC Risk Review

New or sharpened risks:
- The scaffold contract now exists, so the top risk shifts from missing artifact to enforcement. If the first Rust crates do not validate against `p2p-nosql-scaffold-contract.md`, implementation drift can still reappear through hand-written labels, direct SQL mutation, service-local queue policy, or ad hoc cache helpers.
- Metadata-plane risk remains the highest correctness concern until every PostgreSQL workflow has implemented lock order, isolation target, retry taxonomy, idempotency scope, audit timing, outbox timing, and invariant checks from the new workflow matrix.
- The uppercase replication/repair state-machine document is still dangerous as an implementation input. The scaffold contract explicitly quarantines those labels, but the repo still needs validation that migrations, metrics, admin filters, and tests use only canonical lowercase labels.
- Partial-write and late-evidence edges remain beta-critical: minority fsync, late ACK after expiry, late ACK after delete epoch bump, revoked-node completion, interrupted repair conversion, and restore replay must be fixtures before service glue.
- Capacity exhaustion is now an executable-policy risk. The scaffold contract defines pressure ordering, but the scheduler, metadata workflows, and storage-agent local admission must all reject the same operations in the same pressure states.
- Degraded-mode security depends on API shape, not prose. Mutation-sensitive head code must receive only `Fresh`, `Deny`, or `Unavailable` cache decisions and must never use raw cached authority records as allow-state.
- Rust hazards remain concentrated at durable async boundaries: cancellation after fsync/rename, `redb` manifest replay, blocking disk work on Tokio workers, locks across `.await`, bounded-queue overflow, unsupervised task panics, and non-testable clocks.

Mitigation ideas:
- Wire `cargo xtask validate-scaffold-contract` before scaffolding service crates; fail it on non-canonical labels, forbidden dependencies, missing workflow names, missing fixture paths, or uppercase implementation-state imports.
- Generate or golden-test Rust enums, SQL accepted values, metric labels, admin filters, and fixture names from one label source in `hedgehog-types`.
- Implement `metadata-core` transition tests and `metadata-pg` workflow tests before head, repair, or storage-agent service glue can mutate metadata.
- Add first-wave crash/chaos fixtures exactly as named in the scaffold contract, especially partial writes, PostgreSQL pause/recover, temp disk full, repair reserve exhaustion, and clock-skewed leases.
- Treat pressure ordering and degraded-cache decisions as shared crate APIs with tests, not local behavior inside the head or repair scheduler.
