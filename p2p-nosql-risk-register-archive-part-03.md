# P2P NoSQL Risk Register Archive Part 03

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

- Repair can starve under low capacity because the same reserve must cover re-replication, tombstones, orphan cleanup, snapshots, and emergency compaction.
- Hinted replay remains dangerous unless it is explicitly tied to object version, placement epoch, delete epoch, command idempotency, and max replay age.
- Signed envelopes need a single canonical byte representation and downgrade policy before protobuf evolution begins, or different Rust crates can verify different messages.
- The storage-agent manifest is now a safety-critical database. Manifest corruption, partial fsync, rename failure, or duplicate command replay can silently break fencing and idempotency.
- Capacity telemetry is adversarial input. A malicious or broken agent can under-report usage, over-report free space, or flap watermarks to manipulate placement and repair scheduling.

Mitigation ideas:
- Add a degraded-mode matrix covering metadata quorum loss, revocation freshness, read-cache max age, telemetry buffering, and audit replay.
- Define a monotonic lease model: metadata assigns fencing tokens; agents compare tokens, not wall-clock trust, and tests inject clock skew.
- Split effective free capacity into live, reserved, temp, tombstone, orphan, snapshot, repair reserve, and emergency cleanup buckets.
- Require every hinted replay to pass the same metadata state-machine checks as a fresh repair copy.
- Freeze protocol-envelope canonicalization before service implementation; include cross-version signature vectors in CI.
- Treat agent manifests as a separate crash-tested Rust crate with property tests around fsync, atomic rename, duplicate final ACK, and stale command rejection.
- Reconcile agent capacity reports against metadata accounting and quarantine nodes with impossible or unstable reports.

Next decision:
- Define the replication/repair state machine and capacity-reserve formula together, because repair legality is inseparable from available physical and metadata-accounted space.

## 2026-06-05 01:05 UTC Review Notes

New or sharpened risks:
- The observability and production-readiness notes still use older local-first database terms such as conflicts, oplog, partition, index lag, and peer replication. If those names carry into v1, operators will debug the wrong system model.
- Metadata cache behavior remains a high-severity ambiguity: account status, dataset config, routing hints, placement records, revocation, and audit reads need different TTLs and failure rules.
- Repair admission can deadlock if the capacity formula treats emergency cleanup, tombstones, orphan cleanup, snapshots, and repair copies as one shared reserve instead of separately protected budgets.
- Agent reconnect is now part of the correctness path. Duplicate final results, missed head acknowledgements, and stale local command journals can create false commits or endless cleanup loops.
- Security auditability depends on canonical envelopes plus a downgrade policy, but also on canonical actor attribution across clients, heads, metadata nodes, and storage agents.
- Rust crate boundaries could hide invariants if metadata-core, agent-core, proto, and store crates each validate a partial state machine differently.
- Head-mediated repair simplifies v1 networking, but concentrates data-transfer bandwidth and backpressure at the head tier; overloaded heads could delay durability restoration even while storage agents are healthy.

Mitigation ideas:
- Rename v1 telemetry and admin surfaces around object/version/replica states: under-replicated objects, suspect replicas, repair leases, tombstone backlog, orphan bytes, placement epoch, and capacity admission blockers.
- Add a metadata cache policy table with per-record max age, quorum-loss behavior, revocation behavior, and whether stale reads are allowed.
- Make the capacity formula reserve explicit buckets for live data, reserved writes, temp files, tombstones, orphans, snapshots, repair copies, and emergency cleanup.
- Treat agent reconnect replay as a first-class state-machine test scenario with durable command journals and duplicate final ACK vectors.
- Put signature canonicalization, actor attribution, protocol versioning, and downgrade rejection in shared proto tests before service work.
- Keep all legal object/replica/lease transitions in one shared Rust core model and have metadata and agent crates consume generated or shared transition definitions.
- Add head-tier bandwidth and queue-depth limits to the repair scheduler so durability repair cannot starve normal writes or control traffic.

Next decision:
- Decide the canonical v1 vocabulary and telemetry taxonomy before implementing metrics or dashboards, then fold it into the replication/repair state-machine slice.

## 2026-06-05 02:05 UTC Review Notes

New or sharpened risks:
- Head-mediated repair is now a deliberate v1 simplification, but it moves repair bandwidth, queueing, and retry pressure onto the head tier. A busy head can delay durability restoration even when metadata quorum and storage capacity are healthy.
- The storage-agent command journal and manifest are correctness-critical state, not local implementation detail. Corruption, partial writes, or lost final-result replay can break idempotency, fencing, orphan cleanup, and duplicate ACK handling.
- The signed-envelope design includes `sent_at_hlc`, `expires_at`, metadata revisions, payload hashes, and protocol versions, but the exact canonical bytes and downgrade rejection policy are still unresolved. This can create cross-crate verification splits once protobufs evolve.
- Metadata-plane degraded reads are still policy-shaped rather than implementable. Account status, revocation, placement records, routing hints, and audit reads need separate freshness and fail-closed rules.
- Capacity reporting is advisory, but the placement path has not yet defined how to combine physical free bytes, local temp budgets, metadata reservations, tombstone backlog, orphan bytes, snapshot headroom, and repair reserve into one reject/accept decision.
- Whole-object replication keeps v1 simple, but large objects can still monopolize head streams, storage-agent worker pools, temp space, and repair reserve without per-object size classes and transfer backpressure.
- Rust async hazards now center on cross-service retry loops: reconnect replay, duplicate command delivery, late final ACKs, and command expiry can interact badly with bounded channels, cancellation, and `spawn_blocking` disk work.

Mitigation ideas:
- Treat head repair capacity as a scheduled resource with explicit per-head bandwidth, queue-depth, and concurrency budgets; expose repair backpressure separately from storage-node health.
- Make `agent-core` and `agent-store` crash-test targets before service glue, with deterministic tests for journal fsync, atomic manifest replacement, duplicate final-result replay, stale fencing rejection, and orphan unreadability.
- Freeze a canonical envelope encoding and protocol downgrade matrix before adding generated protobuf compatibility layers; include cross-version signature vectors in CI.
- Add a metadata cache/degraded-mode table with one row per record type: max age, quorum-loss behavior, revocation behavior, audit requirements, and whether stale reads are client-visible.
- Define the effective-free-capacity formula as a named invariant and require both metadata and agent local admission checks to pass before streaming object bytes.
- Add size-aware transfer classes for store and repair commands so large whole-object copies cannot starve control traffic, small writes, or urgent corruption repair.
- Model reconnect and cancellation as first-class property tests across metadata-core and agent-core rather than relying on tonic handler behavior.

Next decision:
- Write the replication/repair state-machine slice with three coupled tables: legal object/replica/lease transitions, effective capacity reserve formula, and head-tier repair scheduling/backpressure policy.

## 2026-06-05 02:30 UTC Severus Metadata Review

Accepted findings:
- PostgreSQL should be the v1 authoritative metadata plane. It is less elegant than a Rust-native Raft service, but it gives the team mature transactions, schema constraints, WAL durability, migrations, backups, PITR, and HA tooling while the product semantics are still being locked.
- FoundationDB is a credible later backend if metadata scale or distributed transactional requirements become real, but it adds KV modeling and operational complexity too early.
- `openraft + redb/RocksDB` should not be the v1 default unless the project intentionally wants to own a database implementation from day one.
- The architecture is naive if it treats "P2P" as a reason to underbuild the control plane. With public heads and outbound-only agents, metadata and head coordination remain the heart of correctness.
- Client-side encryption does not hide metadata. Size, timing, access pattern, owner graph, placement, capacity, retention policy, and namespace shape must be treated as sensitive.

Risk register changes:
- Recast metadata risk around PostgreSQL availability, failover, migrations, PITR, and restore drills.
- Treat metadata minimization and admin/dashboard exposure as security requirements, not polish.
- Require mutation outbox, idempotency keys, fencing tokens, placement epochs, and delete epochs before service implementation.

## 2026-06-05 02:35 UTC Severus Replication/Repair Review

Accepted findings:
- PostgreSQL must remain the sole authority for object/version intent, placement, replica lifecycle, repair scheduling, fencing, and delete visibility.
- Storage agents are blob holders and proof reporters; they must not independently decide liveness or repair ownership.
- Object versions are immutable. Writes create new versions, and deletes create delete markers with newer `delete_epoch` values.
- Replica completion callbacks must be guarded by `version_id`, `fencing_token`, `placement_epoch`, `delete_epoch`, and expected state in one transaction.
- Fencing tokens, placement epochs, delete epochs, and idempotency keys are separate concepts and should not be collapsed.
- Tombstones are correctness state. They must survive longer than replication lag, repair retry horizon, audit interval, client retry window, and clock-skew allowance.
- Whole-object replication still needs explicit object-size limits and transfer classes so large repairs do not monopolize head bandwidth and temp capacity.

Risk register changes:
- Added `p2p-nosql-replication-repair-state-machine.md` as the canonical state-machine slice.
- Raised stale worker completions, delete resurrection, and invalid durable replica counting as first-class correctness risks.
- Moved the next design decision from abstract state-machine definition to concrete PostgreSQL schema, indexes, outbox semantics, and migration/failover plan.

## 2026-06-05 02:45 UTC Severus PostgreSQL Schema Review
