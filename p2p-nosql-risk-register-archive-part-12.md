# P2P NoSQL Risk Register Archive Part 12

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

- `p2p-nosql-scaffold-contract.md` now defines the first machine-readable validator slice rather than leaving `cargo xtask validate-scaffold-contract` as prose.
- `hedgehog-types` should expose a static label registry with `LabelDomain`, `LabelSpec`, `label_specs()`, `labels_for()`, and `lookup_label(domain, wire)`.
- The scaffold validator may start with temporary seed data in `xtask/src/scaffold_contract/seed.rs`, but that seed must match the future `hedgehog-types` metadata shape and fail if both sources exist and disagree.
- The first fixture manifest path is fixed as `fixtures/scaffold/manifest.toml`.
- Fixture labels use `domain.wire_label` so duplicate words like `normal`, `pending`, or `expired` remain unambiguous.
- The initial manifest must contain one beta-blocking entry for each first crash and chaos scenario, including late ACKs, revoked-node completion, PostgreSQL recovery, outbox lag, repair reserve exhaustion, redb replay, cancellation after fsync, bounded queue overflow, and clock skew.
- The validator's first parser strategy is intentionally bounded: real TOML parsing for Cargo and fixtures, `serde_json` for dashboards, `syn` only where Rust semantics matter, bounded SQL token checks first, and Markdown scanning only for temporary seed comparison and uppercase quarantine.

New or sharpened risks:
- The main risk has shifted from "what should the validator check" to "does the first implementation actually prove failure behavior." A validator that passes an empty workspace without negative fixtures can still become ornamental.
- `hedgehog-types` can accidentally become a generated-file wrapper too early. A pure Rust static registry is safer for the first pass because it makes the crate the authority and keeps build scripts out of the trust path.
- Fixture manifest scope can sprawl if each scenario tries to encode full test steps. The first manifest should describe ownership and coverage, not replace real tests.
- Domain-qualified labels are now required. Any future parser or manifest shorthand that drops the domain can silently confuse labels reused across state machines.
- The uppercase denylist is deliberately blunt. It may need parser-aware exceptions for Rust enum variants, but the first version should prefer false positives in service code over allowing old conceptual labels into SQL, metrics, fixtures, or dashboards.

Mitigation ideas:
- Implement validator negative fixtures alongside the first passing path: non-canonical label, uppercase dashboard state, forbidden dependency, missing workflow name, service `sqlx` dependency, missing fixture scenario, missing pressure label, missing recovery gate, and unsupervised `tokio::spawn`.
- Keep `fixtures/scaffold/manifest.toml` as a contract manifest with scenario id, owner crate, owner test, beta blocker flag, workflow coverage, recovery gates, pressure/degraded labels, domain-qualified labels, and validator checks.
- Make `hedgehog-types` `labels.rs` the first non-empty crate module, with tests proving emitted strings are lowercase, fixture slugs are path-safe, duplicate labels require domain context, and every canonical label appears exactly once per domain.
- Treat docs as consumers after `hedgehog-types` exists: the validator should compare scaffold and implementation docs to the label registry, not parse Markdown as the source of truth.

Next decision:
- Start implementation with `xtask/src/scaffold_contract/seed.rs`, `fixtures/scaffold/manifest.toml`, `crates/hedgehog-types/src/labels.rs`, and `crates/hedgehog-types/tests/state_labels.rs`. Prefer a pure Rust static label registry over build-time generated metadata for the first scaffold. After this lands, write `p2p-nosql-crate-layout.md` with the actual workspace `Cargo.toml`, crate feature flags, public APIs, and first CI commands.

## 2026-06-06 02:07 UTC Risk Review

New or sharpened risks:
- The next failure mode is implementation theater: creating `xtask`, `hedgehog-types`, and fixture files without negative tests that prove the validator fails for the intended contract violations.
- Metadata-plane risk remains centered on bypass paths. If head, repair, admin, or agent crates can mutate authority tables through generic helpers or direct `sqlx`, the PostgreSQL workflow matrix loses its force.
- Partial-write classification is still the main durability edge. Minority fsync, late final results after expiry, delete epoch bumps, revoked-node completions, interrupted repair conversion, restore replay, and cleanup under pressure need one metadata-core decision path before service glue.
- Capacity exhaustion can still split across boundaries. PostgreSQL reservations, agent local hard rejection, repair reserve eligibility, emergency cleanup, tombstone retention, and head transfer throttles must agree under `critical` and `emergency`.
- Degraded mode can become a shadow authority plane during `recovering` if caches look fresh before outbox, audit, reservations, manifests, and repair deficit are reconciled.
- Metadata privacy remains easy to leak through implementation defaults: object labels, tenant IDs, invite state, capacity reports, node placement, high-cardinality metrics, trace bodies, and audit exports.
- Rust hazards remain at durable async boundaries: cancellation after fsync or atomic rename, blocking disk work on Tokio workers, locks across `.await`, unsupervised `tokio::spawn`, unbounded channels, parser-light validation, non-test-controlled clocks, and permissive `redb` replay.

Mitigation ideas:
- Build the first validator slice with pass and fail fixtures in the same commit: canonical-label drift, uppercase quarantined state, forbidden dependency, service `sqlx`, missing workflow name, missing fixture scenario, missing recovery gate, missing pressure label, and unsupervised spawn.
- Keep `hedgehog-types` as a pure Rust static registry first; require tests for lowercase wire labels, path-safe fixture slugs, domain-qualified lookup, and one entry per canonical label.
- Make `fixtures/scaffold/manifest.toml` a narrow coverage contract, not a full scenario runner: scenario id, owner crate, owner test, beta blocker flag, covered workflows, recovery gates, pressure/degraded labels, and validator checks.
- Forbid authority-table SQL outside `hedgehog-metadata-pg`, migrator code, and explicit test harnesses; enforce this with Cargo dependency checks before source scanning grows sophisticated.
- Start `hedgehog-metadata-core` with decision tests for stale completions, delete epochs, revocation, reservation expiry, cleanup conversion, and pressure ordering before storage-agent final-result handling.
- Add runtime wrappers early for supervised tasks, bounded queues with explicit overflow behavior, disk workers or `spawn_blocking`, replayable outbox publication, cancellation injection, and a test-controlled clock.

Next decision:
- Implement the validator seed and fixture manifest as the first Rust scaffold slice, and require demonstrated negative test failures before declaring the workspace ready for service crates.

## 2026-06-06 03:07 UTC Risk Review

New or sharpened risks:
- The leading risk remains proof, not prose: the first Rust scaffold can include `xtask`, `hedgehog-types`, and a fixture manifest yet still fail to demonstrate that contract violations are caught. Without negative fixtures in CI, the validator becomes a checklist banner rather than an enforcement boundary.
- Metadata-plane bypass remains the highest data-loss risk. Generic PostgreSQL mutation helpers, service-crate `sqlx` dependencies, or admin repair shortcuts would let head, repair, or agent code sidestep the named workflow matrix, including idempotency, lock order, audit, outbox, and invariant checks.
- Partial-write classification is still the most dangerous replication edge. Minority fsync, late final results after reservation expiry, delete epoch bumps, revoked-node completions, interrupted repair conversion, restore replay, and cleanup under pressure must converge through one `metadata-core` decision path before any object becomes visible or any bytes are freed.
- Capacity exhaustion risk is cross-boundary disagreement. PostgreSQL reservations, stale capacity reports, agent local hard rejection, emergency cleanup, tombstone retention, repair reserve use, and head transfer throttles can still make conflicting decisions under `critical` and `emergency`.
- Degraded cache and recovery state can still become a shadow authority plane. During `recovering`, caches may look fresh while outbox lag, audit continuity, reservation reconciliation, agent manifests, and repair deficit are not yet reconciled.
- Metadata privacy remains under-specified at implementation defaults: tenant IDs, dataset names, object labels, invite and revocation state, node placement, capacity reports, trace bodies, metric labels, and audit exports can leak sensitive structure even when payloads remain encrypted.
- Rust hazards remain concentrated around durable async boundaries: cancellation after fsync or atomic rename, blocking disk work on Tokio workers, locks held across `.await`, unsupervised `tokio::spawn`, unbounded channels, parser-light validation, non-test-controlled clocks, and permissive `redb` replay after crashes.

Mitigation ideas:
- Land the first validator slice with pass and fail fixtures in the same commit: canonical-label drift, uppercase quarantined state, forbidden dependency, service `sqlx`, missing workflow name, missing fixture scenario, missing recovery gate, missing pressure label, and unsupervised spawn.
- Keep `hedgehog-types` as a pure Rust static label registry for the first scaffold; validate lowercase emitted strings, path-safe fixture slugs, domain-qualified lookup, duplicate labels across domains, and one entry per canonical label.
- Treat `fixtures/scaffold/manifest.toml` as a coverage contract only: scenario id, owner crate, owner test, beta-blocker flag, covered workflows, recovery gates, pressure/degraded labels, domain-qualified labels, and validator checks.
- Enforce metadata authority by dependency policy first: only `hedgehog-metadata-pg`, migrator code, and explicit test harnesses may depend on `sqlx` or mutate authority tables.
- Implement `metadata-core` decision tests for stale completions, delete epochs, revocation, reservation expiry, cleanup conversion, repair-reserve exhaustion, and pressure ordering before storage-agent final-result handling.
- Add runtime wrappers early: supervised task groups, bounded queues with explicit overflow decisions, disk-worker or `spawn_blocking` boundaries, replayable outbox publication, cancellation injection, test-controlled clocks, and strict `redb` replay reconciliation.

Next decision:
- Build `xtask/src/scaffold_contract/seed.rs`, `fixtures/scaffold/manifest.toml`, and `crates/hedgehog-types/src/labels.rs` together, with required negative tests proving validator failure behavior before any service crate or migration is accepted.

## 2026-06-06 04:06 UTC Risk Review

New or sharpened risks:
- The next implementation risk is false confidence from a green validator that only checks the happy-path scaffold. If negative fixtures are not versioned beside the seed, future service crates can bypass labels, workflows, pressure policy, or runtime guardrails while `cargo xtask validate-scaffold-contract` still appears healthy.
- The validator itself can become an authority split. Temporary `xtask` seed data, `hedgehog-types` label metadata, fixture manifests, SQL accepted values, dashboards, and docs can drift unless the first implementation defines one comparison direction and fails when both sources exist but disagree.
- Metadata-plane bypass remains the central data-loss path. Even read-oriented helper APIs can become mutation backdoors if they expose loaded rows plus generic patch methods rather than named `hedgehog-metadata-pg` workflows that always append audit and outbox records.
- Partial-write handling still has too many cross-cutting triggers for ad hoc service decisions: late ACK after expiry, delete epoch bump, revoked node completion, interrupted repair conversion, restore replay, agent manifest anomaly, and cleanup under pressure all need one classification API before head or agent code interprets a final result.
- Capacity exhaustion can be amplified by the validator and fixture strategy itself. If early fixtures only name pressure labels but do not force `normal`, `pressure`, `critical`, and `emergency` behavior through shared APIs, services can implement compatible strings with incompatible admission semantics.
- Degraded recovery can still reopen too early. A `recovering` status that is computed per head instead of from a PostgreSQL readiness gate can let one head resume writes while another still has stale cache, outbox, audit, reservation, or manifest reconciliation state.
- Metadata privacy is likely to leak through the first local-cluster and observability scaffolds: fixture names, dashboard variables, trace fields, audit examples, and logs may normalize raw tenant, dataset, object, invite, node, or placement identifiers before redaction rules exist.
- Rust hazards now include validator parser shortcuts as well as service runtime bugs. Regex-only scans for TOML, JSON, Rust, or SQL can miss dependency bypasses, generated dashboard labels, public mutation functions, unsupervised task wrappers, blocking filesystem calls, and uppercase state imports.

Mitigation ideas:
- Commit the first validator slice only with both passing scaffold fixtures and failing fixture cases for each first-scope check; make the failure fixtures ordinary tests, not comments in the design doc.
- Treat `hedgehog-types` as the source once it exists and require `xtask` seed-vs-crate equality until the seed is deleted; docs, manifests, SQL, dashboards, and admin filters should be consumers.
- Keep `hedgehog-metadata-pg` workflow APIs narrow: no generic row patchers, no service-owned transactions for authority rows, no raw loaded authority records returned to mutation paths, and no audit/outbox-optional mutation helpers.
- Make partial-write classification an explicit `hedgehog-metadata-core` decision surface with fixtures for every late, stale, revoked, restored, pressure, and manifest-anomaly completion before storage-agent final-result handling lands.
- Require pressure-policy fixtures to prove ordering and denial behavior, not just label presence: emergency cleanup beats new writes, minimum-survivability repair beats desired top-up, and agent local hard reject beats metadata admission.
- Publish recovery readiness from one metadata-backed gate with per-gate failure reasons; heads may display local connectivity state but cannot independently declare the cluster back to `normal`.
- Add redaction and cardinality rules to the scaffold validator early for metrics, dashboards, traces, logs, and fixture names: stable class labels are allowed, raw tenant/object/invite/node identifiers are not.
- Use real parsers for Cargo TOML and fixture TOML from the first version, JSON parsing for dashboards, `syn` for Rust public APIs where needed, and bounded SQL token checks only until `sqlparser` is justified.

Next decision:
- Decide the validator authority handoff: after `hedgehog-types` lands, should `xtask` keep a temporary seed only as a parity check for one milestone, or should the seed be deleted immediately and replaced by crate-owned label metadata plus manifest-driven negative tests?
