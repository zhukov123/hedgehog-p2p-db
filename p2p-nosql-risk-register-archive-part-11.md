# P2P NoSQL Risk Register Archive Part 11

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

- Put Rust runtime guardrails in the first crate templates: dedicated disk workers or `spawn_blocking`, supervised task groups, bounded queues with explicit overflow behavior, replayable outbox publisher, cancellation injection, `redb` startup reconciliation, and one test-controlled clock.

Next decision:
- Name the owner for `p2p-nosql-scaffold-contract.md` and implement the validation task first. No service crate should land until the scaffold contract is machine-checkable.

## 2026-06-05 23:07 UTC Risk Review

New or sharpened risks:
- The top implementation risk is now contract enforcement plus contract consistency. `p2p-nosql-scaffold-contract.md` exists, but its "Required first labels" diverge from `p2p-nosql-implementation-contract.md` for object-version, replica, repair-job, and reservation states; a validator built from the wrong table could freeze a second source of truth.
- Metadata-plane safety still depends on workflow semantics becoming executable. The PostgreSQL matrix names workflows, lock order, isolation target, audit timing, outbox timing, and invariant checks, but implementation can still drift unless `hedgehog-metadata-pg` exposes only named workflows and rejects raw mutation paths from head, repair, admin, and agent crates.
- The uppercase replication/repair state-machine document remains quarantined but not neutralized. Until validation denies those labels in migrations, metrics, dashboards, fixtures, and Rust enums, old terms can leak back through tests or admin filters.
- Partial-write classification remains the highest durability edge: minority fsync, late ACK after expiry, delete epoch bump, revoked-node final result, interrupted repair conversion, restore replay, and cleanup under pressure must not affect visibility, capacity, repair debt, or GC until metadata classifies them.
- Recovery can still go falsely green if readiness is assembled from local health checks. The new gate list is good, but it needs one authoritative status writer and fixtures for PostgreSQL pause/recover, outbox lag, manifest reconciliation, reservation reconciliation, audit continuity, and repair deficit.
- Capacity exhaustion risk has shifted to cross-crate policy agreement. Head admission, repair scheduling, metadata reservations, and agent-local admission must share the same pressure ordering or cleanup, minimum-survivability repair, tombstone retention, and new writes will make conflicting decisions under `critical` and `emergency`.
- Degraded-cache security remains API-shaped. If mutation-sensitive code can receive raw cached tenant, revocation, invitation, placement, capacity, or visibility records, the cache can become an unauthorized metadata authority during outage or recovery.
- Head-mediated whole-object transfer is still an operational bottleneck. Without reserved bounded queues, large uploads or repairs can starve final ACKs, lease expiry, revocation checks, outbox publishing, readiness checks, and repair leasing.
- Rust hazards remain concentrated around durable async boundaries: cancellation after fsync/rename, `redb` journal replay after crashes, blocking disk work on Tokio workers, locks across `.await`, unbounded channel growth, unsupervised task panics, non-testable clocks, and parser-light validation that misses real TOML/SQL/Rust structure.

Mitigation ideas:
- Resolve the label-table conflict before writing `cargo xtask validate-scaffold-contract`; choose one canonical list in `hedgehog-types`, then update either the scaffold contract or implementation contract so Rust, SQL, metrics, admin filters, dashboards, and fixtures all validate against the same labels.
- Implement `cargo xtask validate-scaffold-contract` as the first code task and fail it on non-canonical labels, uppercase quarantined labels, forbidden dependencies, missing workflow names, missing fixture manifest entries, missing recovery gates, and service-crate raw SQL mutations.
- Generate or golden-test state enums, SQL accepted values, metric labels, admin filter values, dashboard variables, and fixture names from `hedgehog-types` once that crate exists; do not let service crates hardcode labels.
- Put transition tests in `hedgehog-metadata-core` and workflow tests in `hedgehog-metadata-pg` before any head, repair, admin, or storage-agent service glue can mutate metadata.
- Add beta-blocking crash and chaos fixtures for the named partial-write, PostgreSQL pause/recover, temp disk full, repair reserve exhausted, orphan cleanup, cancellation, bounded-queue overflow, and clock-skew scenarios.
- Make pressure ordering, degraded-cache decisions, and recovery readiness shared APIs with fixtures, not service-local policies.
- Put Rust runtime guardrails into the scaffold templates: dedicated disk workers or `spawn_blocking`, supervised task groups, bounded queues with explicit overflow behavior, replayable outbox publisher, `redb` startup reconciliation, cancellation injection, and one test-controlled clock abstraction.

Next decision:
- Before creating service crates, decide the canonical state-label table owner and reconcile `p2p-nosql-scaffold-contract.md` with `p2p-nosql-implementation-contract.md`. Then assign `xtask`/`hedgehog-types` ownership and make `cargo xtask validate-scaffold-contract` the next committed implementation slice.

## 2026-06-06 00:04 UTC State Label Reconciliation Review

Accepted design:
- `p2p-nosql-scaffold-contract.md` and `p2p-nosql-implementation-contract.md` now use the same canonical lower-case implementation labels for object versions, replicas, repair jobs, reservations, capacity pressure, and degraded mode.
- The scaffold contract remains the seed source only until `hedgehog-types` exists. After that, `hedgehog-types` is the executable source for Rust enums, SQL accepted values, metrics labels, admin filters, dashboard variables, fixture names, and document validation.
- The uppercase labels in `p2p-nosql-replication-repair-state-machine.md` remain quarantined as conceptual transition language and must not appear in migrations, Rust enums, metrics, dashboards, admin filters, or fixtures.
- The scaffold validation command is consistently named `cargo xtask validate-scaffold-contract`.

Risk review:
- The remaining implementation risk is no longer label selection but enforcement. A human-readable table will drift unless the first scaffold makes label metadata machine-checkable.
- Reservation terminology changed from `leased`/`released`/`converted_to_repair` to `reserved`/`finalizing`/`aborted` to align the durable write lifecycle with the scaffold contract. Any old reservation wording in tests or migrations should be treated as non-canonical.
- Object state labels are still smaller than the object-version state space. Implementation should keep object-key/head state separate from version lifecycle state and avoid reusing version labels on object rows.

Next decision:
- Assign `hedgehog-types` as the label owner and `xtask` as the validator owner. The next implementation-facing document or code slice should define the exact label metadata shape, fixture manifest path, and parser strategy for `cargo xtask validate-scaffold-contract` before service crates or migrations are scaffolded.

## 2026-06-06 00:16 UTC Risk Review

New or sharpened risks:
- The highest near-term risk is now validator scope creep. `cargo xtask validate-scaffold-contract` must be useful before service crates exist, but if it tries to parse every future SQL/Rust/dashboard shape on day one, it can stall the scaffold instead of protecting it.
- The capacity admission document still carried old reservation labels until this review. This proves prose reconciliation is not enough; every design slice that names implementation states needs machine-checkable drift detection.
- Metadata-plane risk remains concentrated in workflow ownership. If `hedgehog-metadata-pg` exposes generic row mutation helpers or lets head/repair/admin crates import `sqlx` for authority tables, the PostgreSQL workflow matrix becomes advisory instead of enforceable.
- Partial-write classification remains the main data-loss edge. Late ACKs, minority fsync, delete epoch bumps, revoked-node completions, restore replay, and cleanup under pressure all need one metadata-core decision path before any storage-agent command is considered successful.
- Capacity exhaustion remains a cross-boundary risk: PostgreSQL reservations, stale capacity reports, local agent hard rejection, repair reserve use, emergency cleanup, and head transfer throttles can disagree unless the pressure policy is a shared tested API.
- Degraded-mode cache safety still depends on API shape. Raw cached authority records must not be reachable from mutation workflows, especially during `recovering`, when PostgreSQL is back but caches, outbox, audit, reservations, and manifests may still disagree.
- Head-mediated whole-object transfer remains an operational choke point. Large uploads and repairs can still starve final ACKs, lease expiry, revocation checks, outbox delivery, and readiness checks unless queue separation is present in the scaffold.
- Rust implementation hazards remain centered on durable async boundaries: cancellation after fsync or atomic rename, `redb` replay, blocking disk work on Tokio workers, locks across `.await`, unsupervised spawned tasks, unbounded channels, parser shortcuts in the validator, and non-test-controlled clocks.

Mitigation ideas:
- Ship the validator in phases: first hardcoded seed labels, ownership/dependency checks, workflow-name presence, fixture manifest presence, uppercase-state quarantine, and recovery-gate label coverage; then graduate to parser-backed SQL/Rust/dashboard checks.
- Add a `fixtures/scaffold/manifest` contract early with owner crate, scenario name, pressure/degraded labels covered, and beta-blocker flag for each crash/chaos fixture.
- Make `hedgehog-types` expose label metadata for Rust enum, SQL value, metric label, admin filter, and fixture slug; make docs a generated or validated consumer once the crate exists.
- Forbid `sqlx` in head, repair, admin, and storage-agent service crates except test harnesses; all authority mutations must call named `hedgehog-metadata-pg` workflows.
- Put partial-write and recovery gates into `metadata-core` as explicit decisions before implementing storage-agent final-result handling.
- Implement capacity pressure, degraded-cache decisions, head control queues, and runtime guardrails as shared crate APIs or wrappers so services cannot silently diverge.

Next decision:
- Define the first validator slice precisely: `hedgehog-types` label metadata shape, `xtask` seed data, fixture manifest path/schema, denylisted uppercase states, allowed crate dependency graph, and the minimal parser strategy. Then commit the empty workspace scaffold only after `cargo xtask validate-scaffold-contract` can pass and fail for the intended reasons.

## 2026-06-06 01:16 UTC Risk Review

New or sharpened risks:
- The main risk is now a sequencing trap: if the empty Rust workspace lands before the validator has real failure cases, the repo will look implementation-ready while labels, workflows, fixtures, and dependency direction are still only prose.
- Metadata-plane risk is still the place data loss will hide. The workflow matrix is strong, but `hedgehog-metadata-pg` needs named workflow APIs and tests for stale fencing, duplicate idempotency, deadlock retry, invariant failure, audit append, and outbox append before head or repair code can safely call it.
- Partial-write classification remains the beta gate. Minority fsync, late ACK after expiry, late ACK after delete epoch bump, revoked-node completion, interrupted repair conversion, restore replay, and cleanup under pressure must all resolve through one `metadata-core` decision path.
- Capacity exhaustion risk is no longer about the formula; it is about consistent enforcement. PostgreSQL reservations, agent local hard admission, repair reserve eligibility, emergency cleanup, tombstone retention, and head transfer throttles can still disagree under `critical` and `emergency`.
- Degraded-cache and recovery state can become a shadow metadata plane if `recovering` allows fresh-looking cached records to authorize writes before outbox, audit, manifests, reservations, and repair deficit are reconciled.
- Head-mediated whole-object transfers remain an operational choke point. A 64 MiB v1 limit helps, but without reserved bounded control queues, uploads and repair copies can starve final ACKs, lease expiry, revocation checks, outbox publishing, and readiness publication.
- Security gaps remain around metadata privacy and authority leakage: object names, tenant/dataset IDs, invite state, revocation state, capacity reports, and admin audit rows are sensitive even when payload bytes are encrypted.
- Rust hazards remain concentrated around durable async edges: cancellation after fsync/rename, blocking disk work on Tokio workers, locks across `.await`, unbounded channels, unsupervised spawned tasks, parser-light validation, test clocks that cannot simulate skew, and `redb` replay that silently accepts inconsistent local state.

Mitigation ideas:
- Implement `cargo xtask validate-scaffold-contract` with intentionally small first scope plus negative tests: canonical labels, uppercase quarantine, crate dependency direction, named workflow presence, fixture manifest entries, recovery gate names, and forbidden `sqlx` dependencies outside allowed crates.
- Add `fixtures/scaffold/manifest.toml` before service code, with one entry per beta-blocking crash/chaos scenario, owner crate, pressure/degraded labels covered, and `beta_blocker = true`.
- Make `hedgehog-types` label metadata the first non-empty crate API, then have docs, SQL tests, metrics labels, admin filters, dashboards, and fixtures validate against it instead of duplicating strings.
- Make pressure ordering, degraded-cache decisions, recovery readiness, and transfer-class queue budgets shared APIs with tests, not service-local constants.
- Start metadata-core with decision tests for partial writes, stale completions, delete epochs, revoked nodes, reservation expiry, and cleanup conversion before implementing storage-agent final-result handling.
- Add runtime wrappers early: supervised task groups, bounded channels with explicit overflow decisions, disk-worker or `spawn_blocking` boundaries, replayable outbox publication, cancellation-injection hooks, and a test-controlled clock.

Next decision:
- Commit the empty Rust workspace only after the first validator slice exists and demonstrates both pass and fail behavior. The next concrete decision is the `fixtures/scaffold/manifest.toml` schema and the initial `hedgehog-types` label metadata shape that `xtask` will consume.

## 2026-06-06 02:04 UTC Validator Seed And Fixture Manifest Slice

Accepted design:
