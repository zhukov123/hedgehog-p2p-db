# P2P NoSQL Risk Register Archive Part 06

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

New or sharpened risks:
- Threat controls are now named, but several are still policy-level until converted into fixtures: compromised-head bypass tests, stale-cache outage tests, false capacity-report reconciliation, manifest corruption, and tenant flood scenarios.
- The deterministic CBOR choice still needs a concrete crate wrapper and canonical-map enforcement test; otherwise the signed-envelope row in the threat model is not yet enforceable.
- PostgreSQL restore must prove authority consistency, not only data availability. Security roots, revocation epochs, idempotency records, outbox events, and audit hash checkpoints must line up after PITR.
- Degraded-mode behavior is now the largest unresolved operational/security edge. Without a table, operators and heads will improvise during metadata outages.

Mitigation ideas:
- Convert every threat-model row into at least one metadata-core or integration fixture before service glue.
- Implement an `authority_cache_policy` table in docs before code: record type, max age, allowed outage operations, fail-open/fail-closed, revocation rule, audit replay behavior, and admin visibility.
- Treat PostgreSQL restore drills as security drills: verify security roots, revoked principals, invite use counts, idempotency replay, outbox lag, and audit checkpoint continuity.
- Keep head nodes boring: authenticate, rate-limit, verify envelopes, forward, and stream bytes only after PostgreSQL grants leases and placement.

Next decision:
- Define the degraded-mode authority and cache policy table for tenant status, admin identity, revocation epochs, invitations, node status, placement records, routing hints, object visibility, audit append, and outbox delivery.

## 2026-06-05 06:04 UTC Degraded-Mode Cache Policy Review

Accepted findings:
- Added [p2p-nosql-degraded-mode-cache-policy.md](p2p-nosql-degraded-mode-cache-policy.md) as the canonical degraded-mode authority and cache policy slice.
- Degraded mode is read-mostly and fail-closed. PostgreSQL outage does not permit authority creation, placement decisions, write visibility changes, repair ownership changes, admin mutations, invitation acceptance, capacity admission, or durable audit/outbox claims.
- Head-node cache entries need typed policy, hard max age, metadata revision, authority epoch, loaded time, and deny-aware lookup results.
- Specific-version committed reads may continue only when every required authority record is fresh and deny-free. Latest reads should fail once the head pointer or read token is not fresh and version-specific.
- Revocation behavior is intentionally asymmetric: a cached deny blocks immediately, while missing or stale revocation state cannot be used as an allow.
- Recovery from outage is its own state and requires migration, invariant, outbox, audit, and cache-rebuild gates before returning to normal operation.

New or sharpened risks:
- The strict cache policy protects data and authority, but it may surprise users who expect read availability during short metadata outages.
- Local audit buffering is useful only for denied attempts and status transitions. If future code buffers allowed privileged actions, it reintroduces split authority.
- Read caches now require careful privacy hygiene because outage-read audit rows can still reveal access timing and object/version identifiers.

Mitigation ideas:
- Implement cache lookup as typed `Fresh<T> | Deny(reason) | Unavailable(reason)` results rather than optional records.
- Add outage fixtures before service glue: write/delete/admin/invite/repair/capacity rejection, fresh committed version read success, stale latest read failure, cached revocation denial, outbox claim refusal, and recovery gate enforcement.
- Expose degraded-mode metrics by record kind: cache age, stale denies, outage allowed reads, outage rejected operations, audit buffer depth, outbox recovery lag, and recovery gate status.

Next decision:
- Define the Rust crate layout and first scaffold package: workspace members, crate boundaries, feature flags, shared error and ID types, migration embedding, deterministic CBOR vector location, agent-store crash-test boundary, and local-cluster harness ownership.

## 2026-06-05 06:15 UTC Risk Review

New or sharpened risks:
- The degraded-mode policy is now correct but intentionally availability-hostile. Product and API docs must make clear that v1 favors authority safety over broad outage reads, or operators will misdiagnose expected `metadata_unavailable` rejections as incidents.
- The next scaffold can still split authority if crate boundaries are vague. `hedgehog-types`, `hedgehog-crypto`, `hedgehog-metadata-core`, `hedgehog-metadata-pg`, `hedgehog-storage-agent`, and `hedgehog-observability` need a single ownership map for state labels, IDs, error categories, envelope structs, and metric labels.
- Deterministic CBOR remains a security dependency until a concrete wrapper and golden-vector directory exist. Generic `serde` use can accidentally permit map-order, default-field, unknown-field, or critical-field ambiguity.
- PostgreSQL recovery now has multiple gates, but there is no named invariant checker package yet. Without it, restore drills can prove the database is online while missing authority consistency across security roots, revocation epochs, idempotency records, audit checkpoints, and outbox rows.
- Storage-agent crash recovery remains the highest Rust durability hazard after metadata-core. `redb` plus file-per-object is simple, but cancellation between temp fsync, manifest update, final-result journal write, and ACK replay can create orphaned bytes or false durability evidence.
- Head-mediated reads during outage require fresh tenant, dataset, revocation, object visibility, placement, node status, routing, and token records at once. That compound freshness requirement can cause confusing partial availability and needs explicit metrics per missing record kind.
- Capacity exhaustion can still be triggered by test harness omissions: local-cluster fixtures must include temp-volume fill, repair reserve breach, stale capacity reports, and large-object transfer saturation before beta claims the formula works.

Mitigation ideas:
- Write the Rust crate-layout slice before scaffolding and make it a contract: crate list, owned types, forbidden dependencies, feature flags, error taxonomy, test-vector paths, migration embedding, and local-cluster ownership.
- Create `hedgehog-crypto` canonical-envelope vectors first, with tests for map ordering, omitted defaults, unknown critical fields, downgrade, actor/action rebinding, expiry/skew, and payload hash mismatch.
- Define `hedgehog-invariants` or an equivalent test module used by CI, migrator smoke tests, restore verification, and local-cluster recovery gates.
- Make storage-agent crash tests executable before networking: inject cancellation after each durable boundary and require startup reconciliation to classify bytes as healthy, orphaned, tombstoned, or unreadable evidence.
- Add degraded-read rejection metrics with record-kind reasons so operators can distinguish stale revocation, stale object head, stale placement, stale node status, and missing read token.
- Add local-cluster chaos scenarios for PostgreSQL pause/recover, temp disk full, head killed mid-upload, agent restart after ACK, repair reserve exhaustion, and revoked-node cached read attempts.

Next decision:
- Define the Rust crate layout and first scaffold package, including exact crate ownership boundaries, canonical test-vector locations, invariant checker ownership, and the minimal local-cluster chaos fixtures that must exist before service glue expands.

## 2026-06-05 07:15 UTC Risk Review

New or sharpened risks:
- The next Rust scaffold can accidentally create three authorities: typed state in `hedgehog-types`, semantic transitions in `hedgehog-metadata-core`, and SQL constraints in `hedgehog-metadata-pg`. If the ownership boundary is not exact, tests may pass in one crate while services encode different legal transitions.
- Metadata-plane safety now depends on serializable-looking workflows even if PostgreSQL uses ordinary row locks. Missing lock-order rules, retry policy, or idempotency semantics can produce duplicate commits, leaked reservations, or outbox gaps under concurrent writes and repair completions.
- The invariant checker is still unnamed. Without a crate or module that owns invariant definitions, restore drills, migration smoke tests, recovery gates, admin diagnostics, and CI can drift into separate partial checks.
- Replication edge cases remain most dangerous around partial writes: one or two replicas may have fsynced ciphertext while the version never commits, expires, or converts to repair. Those bytes must not become readable, must not consume capacity forever, and must not be deleted before audit and repair decisions are durable.
- Capacity exhaustion can cascade through temp files, repair reserve, tombstone lag, orphan cleanup lag, and stale agent reports. The current formulas are sound, but early tests must prove pressure behavior before real streaming code hides it behind backpressure.
- Deterministic CBOR remains a concrete security gap until the project chooses either a strict wrapper over `ciborium` or a different crate with enforced canonical map ordering. Allowing generic `serde` maps into signed envelopes is enough to create replay or verification ambiguity.
- Rust async hazards extend beyond cancellation. Holding locks across `.await`, blocking fsync on the Tokio runtime, unbounded channels for repair queues, and task panics inside outbox publishers can all preserve process liveness while losing progress or ordering.
- Operational complexity is concentrating in recovery: after PostgreSQL pause, restore, migration, or network partition, the system needs one visible recovery path that checks migrations, invariants, audit append, outbox lag, cache rebuild, repair deficits, and capacity reservations before normal traffic resumes.

Mitigation ideas:
- Make the crate-layout slice an authority map: each shared concept has one owner crate, allowed dependents, forbidden dependents, SQL representation, metric label, admin label, and fixture location.
- Put invariant checks in a named `hedgehog-invariants` crate or a clearly owned `metadata-core` module reused by `metadata-pg`, migrator smoke tests, local-cluster recovery, and admin diagnostics.
- Define PostgreSQL workflow rules before migrations expand: lock order, isolation level, retryable error taxonomy, idempotency-key uniqueness, outbox insert timing, and audit insert timing.
- Add partial-write fixtures: fsynced minority then expiry, final ACK after expiry, repair conversion after head crash, orphan cleanup after abort, and revoked-node cleanup during degraded recovery.
- Make capacity chaos part of the first local cluster: temp volume full, repair reserve exhausted, stale capacity report accepted then locally rejected, tombstone backlog, orphan backlog, and large-object saturation.
- Build a tiny `hedgehog-crypto` envelope-vector CLI early so canonical bytes are generated and reviewed before service code depends on them.
- Treat Rust runtime hazards as test requirements: no locks across `.await` linting where practical, `spawn_blocking` for fsync, bounded queues, panic-supervision tests, cancellation injection, and replayable outbox publishing.
- Expose recovery as an operator-visible state machine rather than scattered readiness checks.

Next decision:
- Write the Rust crate-layout and scaffold contract now: exact workspace members, concept ownership map, dependency rules, invariant-checker location, PostgreSQL workflow rules, canonical envelope-vector path, storage-agent crash-test boundary, and first local-cluster chaos fixtures.

## 2026-06-05 08:06 UTC Risk Review

New or sharpened risks:
- The highest current risk is still authority drift in the first Rust scaffold. If state labels, transition legality, SQL constraints, metrics labels, and admin display labels are owned by different crates, the codebase can pass local tests while violating the documented metadata state machine.
- PostgreSQL workflow correctness is now a beta-critical implementation hazard. Ordinary row locks are acceptable only if lock order, isolation level, retryable errors, idempotency records, outbox writes, and audit writes are specified before concurrent write/repair fixtures.
- Partial-write cleanup remains the sharpest replication edge: minority fsynced replicas, expired reservations, late ACKs, head crashes, and repair conversion must converge to either committed visibility or unreadable orphan/cleanup state with no ambiguous middle.
- Capacity formulas are documented, but capacity exhaustion can still escape if local-cluster chaos arrives after streaming code. Temp disk fill, tombstone backlog, orphan backlog, stale capacity reports, and large-object transfer saturation need to be early fixtures, not late operational tests.
- Deterministic CBOR is selected but not yet enforceable. Without a crate wrapper, vector generator, and golden-vector directory, service code may accidentally sign non-canonical maps, omitted defaults, or unknown critical fields.
- Metadata-plane degraded mode is intentionally fail-closed, but compound read freshness across tenant, dataset, revocation, object visibility, placement, node status, routing, and read token can create noisy partial outages unless rejection reasons are first-class metrics.
- Rust runtime failure modes are moving from theory to scaffold requirements: cancellation at durable boundaries, locks held across `.await`, fsync on async workers, unbounded channels, and unsupervised task panics can all produce live services with stuck reservations or lost outbox progress.
