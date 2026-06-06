# P2P NoSQL Risk Register Archive Part 05

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

- write reservation lifecycle states and release/expiry/conversion rules
- storage-agent local manifest and journal: `redb` plus file-per-object ciphertext
- v1 maximum object size: 64 MiB
- transfer classes: small, medium, large
- thin generated local-cluster harness starts in Milestone 1

The next design decision is the v1 threat model. It should be concrete enough to drive tests, audit events, metrics, admin actions, and runbooks before implementation begins.

Required output:
- actor
- capability
- target
- trust boundary
- prevention control
- detection signal
- recovery/runbook

## 2026-06-05 04:05 UTC Risk Review

New or sharpened risks:
- The next implementation contract is now the main risk surface. If PostgreSQL access, migrations, transaction boundaries, and test database workflow are chosen late, `metadata-core` and `metadata-pg` can drift into two different authorities.
- The schema plan and state machine use slightly different names for states such as `AVAILABLE` vs `committed` and `HEALTHY` vs `present`. That naming gap is harmless in prose, but dangerous in Rust enums, SQL migrations, metrics, and admin filters.
- Effective capacity is defined, but the write path still needs a concrete "reservation lifecycle" from intent creation through transfer, commit, expiry, release, and repair conversion. Leaked reservations can become a slow denial of service.
- Head-mediated repair backpressure is still policy, not an enforceable scheduler contract. A busy head tier can preserve metadata correctness while durability silently lags.
- Revocation and degraded-mode behavior interact: if PostgreSQL is unavailable, heads must know whether cached revocation, invitation, tenant status, and node status can be used for reads, writes, repair, or admin operations.
- Signed-envelope canonicalization is a cross-language and cross-version compatibility risk. Choosing the format after protobuf or API handlers exist will create migration pain and possible downgrade bypasses.
- Rust async cancellation is still under-modeled. Dropping a future during upload, fsync, journal write, outbox publish, or repair completion must leave an explicit recoverable state, not an ambiguous half-command.

Mitigation ideas:
- Make the first scaffold a contract package, not a service: `hedgehog-types`, deterministic envelope vectors, `sqlx` migration harness, transaction trait shape, and state-name mapping tests.
- Add a single canonical state glossary mapping design states to Rust enum variants, SQL enum/text values, metrics labels, and admin display labels before writing migrations.
- Define reservation states and transitions explicitly: `pending`, `leased`, `streaming`, `committed`, `released`, `expired`, `converted_to_repair`, and `failed_cleanup_required`.
- Add head repair scheduler limits as named config and metrics: per-head repair bandwidth, queue depth, max large-object repairs, control-plane priority, and starvation age.
- Write a degraded-mode authority table covering each cached authority record: tenant status, revocation epoch, node status, invitation, placement, routing hint, and audit append.
- Pick deterministic CBOR or strict deterministic protobuf immediately and add golden vectors for unknown critical fields, protocol downgrade, expiry, actor binding, and payload hash mismatch.
- Treat cancellation as a required property-test axis for metadata, storage-agent manifest, command journal, and outbox publisher code.

Next decision:
- Freeze the v1 implementation contract in one short design update: choose `sqlx`, choose deterministic envelope encoding, define the canonical state glossary, define reservation lifecycle states, and pull a minimal local-cluster smoke harness forward to immediately test the first PostgreSQL transactions.

## 2026-06-05 04:23 UTC Deployment Stack Review

Accepted findings:
- Compose is the first supported deployment target, with Kubernetes deferred until health, metrics, config, and secret contracts stabilize.
- The standard local stack is PostgreSQL, migrator, head, three storage agents, repair worker, admin API/UI, Prometheus, Grafana, and optional OpenTelemetry collector.
- PostgreSQL stays private to the control network; storage agents remain outbound-only and persist separate data, journal, and temp volumes.
- The migrator is a first-class service and must use the same migration path as CI.
- Local-cluster generation belongs inside the Rust workspace so tests, drills, generated secrets, dashboards, and Compose output stay reproducible.

New or sharpened risks:
- If deployment work waits until the end of the crate roadmap, migration failures, health contract gaps, restart behavior, and dashboard drift will be discovered too late.
- Static hand-written Compose files can drift from typed Rust config and test assumptions.
- Local development secrets can leak into docs, git, or images unless the generated runtime directory and ignore rules are defined before scaffolding.
- Health checks that only test process liveness will hide PostgreSQL migration mismatch, stale authority caches, unwritable agent volumes, and paused repair workers.
- Beta Compose can give a false sense of production readiness if PostgreSQL PITR, admin auth, Grafana protection, and restore drills are not required gates.

Mitigation ideas:
- Build a thin generated local-cluster harness as soon as metadata-pg can create tenants, datasets, nodes, and object write intents.
- Treat `migrator`, health endpoints, Prometheus scrape config, and dashboard provisioning as part of the first integration contract.
- Keep generated local secrets under an ignored runtime directory and require explicit operator-provided authority material for beta.
- Add failure drills directly to the local-cluster CLI: storage-agent kill/restart, repair-worker kill, head kill during upload, PostgreSQL pause, temp-volume fill, node revocation, and PostgreSQL restore.

Next decision:
- Freeze the v1 implementation contract: choose `sqlx`, deterministic envelope encoding, canonical state glossary, write reservation lifecycle, max object size and transfer classes, and generated local-cluster file layout.

## 2026-06-05 05:04 UTC Implementation Contract Review

Accepted findings:
- V1 should use `sqlx` for PostgreSQL access and migrations. Mixing `tokio-postgres` into service crates would create avoidable split-brain around transaction style, migration tooling, and tests.
- The first scaffold should be a contract package: `hedgehog-types` state labels and IDs, `hedgehog-crypto` deterministic CBOR envelope vectors, `hedgehog-metadata-core` transition decisions, `hedgehog-metadata-pg` `sqlx` migrations/workflows, and a thin local-cluster harness.
- Signed envelopes use deterministic CBOR and must have golden vectors for unknown critical fields, expiry, protocol downgrade, payload hash mismatch, and actor/action rebinding before service signing code.
- Write reservations are now correctness state with explicit states and outbox/audit events for release, expiry, repair conversion, and cleanup-required paths.
- Storage-agent manifest and command journal use `redb` initially, and crash tests must precede networked storage-agent behavior.
- Whole-object v1 needs a hard 64 MiB maximum object size and small/medium/large transfer classes so capacity, repair, and head-tier backpressure tests are meaningful.
- The local-cluster harness moves earlier into Milestone 1 once metadata-pg can run migrations, create tenants/datasets/nodes, accept capacity reports, create write intents, and emit outbox/audit rows.

New or sharpened risks:
- Deterministic CBOR still needs a precise crate choice and canonical-map enforcement tests; otherwise "deterministic" can become an assumption instead of a property.
- `redb` is a pragmatic v1 manifest choice, but it makes agent-store crash tests non-negotiable because the manifest controls fencing, local admission, orphan cleanup, and final-result replay.
- 64 MiB simplifies beta, but product docs must not imply arbitrary large-object database behavior until chunking or erasure coding is intentionally designed.
- The threat model is now the largest missing implementation gate. Current documents name many controls, but do not yet map attacker capability to prevention, detection, and recovery.

Next decision:
- Write `p2p-nosql-threat-model.md` as the next slice, with one table covering compromised head nodes, malicious storage agents, stolen admin keys, leaked invitations, stale cached authority, metadata privacy leakage, false capacity reports, replayed envelopes, manifest corruption, and abusive tenants.

## 2026-06-05 05:07 UTC Threat Model Review

Accepted findings:
- Added [p2p-nosql-threat-model.md](p2p-nosql-threat-model.md) as the v1 attacker-capability table.
- The most dangerous security failure is still not raw cryptography; it is accidental authority bypass where a head node, repair worker, admin API, or cache makes decisions PostgreSQL must own.
- Malicious storage agents need to be treated as unreliable evidence providers. Hash verification, fsynced ACKs, verify jobs, capacity reconciliation, and repair-away runbooks must be built before beta.
- Stale cached authority is a cross-cutting failure mode. Revocation, tenant status, admin identity, invitations, placement, routing, reads, repair, and audit append need explicit degraded-mode rules.
- Metadata privacy remains a first-class security risk even with client-side encryption. Logs, metrics, traces, admin views, and APIs can leak object size, timing, placement, namespace shape, and tenant relationships.
- Rust implementation hazards belong in the threat model because async cancellation, manifest corruption, duplicate final-result replay, and outbox gaps can produce security-relevant state confusion.

