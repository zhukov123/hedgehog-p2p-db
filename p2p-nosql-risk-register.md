Exit code: 0
Wall time: 0.5 seconds
Output:
# P2P NoSQL Risk Register

## Purpose

Track implementation risks and unresolved decisions for the Rust-first architecture.

This register focuses on failure modes, operational complexity, security gaps, metadata-plane risk, replication edge cases, capacity exhaustion, and Rust implementation hazards.

## Current Highest Risks

### 0. Architecture Boundary Drift Between Local-First P2P And Head-Mediated Storage

Risk:
- The older architecture/MVP documents describe local-first peer-to-peer database semantics, while the Rust-first design has moved toward public head nodes, a Raft-backed metadata plane, and outbound storage agents storing encrypted objects.
- If this boundary is not made explicit, implementation can accidentally mix two incompatible systems: an offline-first document database and a coordinated encrypted object store.

Failure modes:
- Engineers build local-first writes that bypass metadata reservations, leases, and capacity admission.
- Replication code assumes peer-to-peer object exchange while the v1 storage-agent protocol assumes head-mediated repair.
- Product promises offline writes even though the Rust-first storage design currently requires metadata quorum for new visible writes.
- Observability and runbooks use replication/conflict terms from the document database shape that do not map cleanly to object placement and repair.

Mitigations:
- Add a short canonical architecture note that names the v1 as head-mediated encrypted object storage with strong metadata, not the original local-first database.
- Keep peer-to-peer document database semantics as a separate future track unless explicitly reselected.
- Rename metrics, runbooks, and admin pages where needed to distinguish object repair from document conflict replication.
- Require every write-path design slice to state whether it depends on metadata quorum, local offline acceptance, or both.

Next decision:
- Decide whether the current v1 target is the head-mediated encrypted object store or the earlier local-first document database, then mark the non-selected shape as deferred.

### 1. Metadata Plane Becomes The Real Control Plane Bottleneck

Risk:
- The design correctly makes metadata strongly consistent, but every placement, lease, quota, repair, delete, and visibility transition depends on it.
- PostgreSQL is the right v1 control plane choice, but the system still inherits database availability, failover, migration, backup, and restore risk.

Failure modes:
- Primary database loss freezes writes, admin mutations, repair ownership, and capacity changes until failover completes.
- Bad migrations or long-running locks can delay write admissions and repair scheduling.
- Head-node read caches may accidentally outlive revocation or account suspension policy.
- Backup/restore and PITR are assumed rather than rehearsed, leaving the project unable to recover cleanly from operator mistakes.

Mitigations:
- Define explicit read-cache max ages by metadata type.
- Add load tests for write admission, repair-job churn, and heartbeat/capacity-report volume before feature expansion.
- Keep all metadata commands idempotent and replay-testable.
- Define a degraded-mode policy that allows only safe reads and telemetry buffering when metadata quorum is unavailable.
- Require tested failover, PITR, restore drills, migration rollback plans, and durable mutation outbox processing before beta.

Next decision:
- Pick the first PostgreSQL deployment posture: managed Postgres, self-hosted primary plus standby, or containerized dev-only Postgres with a clearly separate production plan.

### 2. Replication, Hints, And Repair State Machine Is Still Underdefined

Risk:
- Metadata and storage-agent slices mention repair, leases, hinted replay, and orphan cleanup, but the complete object/replica state machine is not yet locked.

Failure modes:
- A repair copy races with a delete tombstone or a newer object version.
- A source replica becomes corrupt or lost during a repair copy.
- Re-replication consumes the remaining repair reserve and blocks more urgent repairs.
- Tombstones are collected too early and deleted data reappears from an offline node.
- A hinted write is replayed after the object was deleted, overwritten, or its placement epoch changed.
- Read repair serves an opportunistic replica that has durable bytes but no valid metadata visibility.

Mitigations:
- Define a formal replica state transition table with legal transitions and fencing requirements.
- Make repair source selection prefer recently verified committed replicas.
- Add tombstone retention rules tied to max offline duration and repair audit completion.
- Add repair priority classes: durability loss, corruption, node drain, policy change, audit.
- Bind hinted replay to object version, delete epoch, placement epoch, lease fencing token, and max replay age.
- Keep repair reads explicitly separate from normal client-visible reads in protocol and metrics.

Next decision:
- Write the replication and repair state-machine slice before adding more product surface.

### 3. Capacity Exhaustion Can Cascade

Risk:
- The design has warning, soft, and hard watermarks, but capacity pressure interacts badly with repair, tombstones, temporary files, snapshots, and orphan cleanup.

Failure modes:
- The cluster admits writes based on metadata reservations while agents run out of physical temp space.
- Repair cannot proceed because every healthy node is near the hard watermark.
- Deletes free metadata quota before physical cleanup, hiding real disk pressure.
- Compaction and snapshot work need disk headroom that the admission policy did not reserve.

Mitigations:
- Reserve separate capacity budgets for live data, temp files, repair, tombstones, snapshots, and emergency cleanup.
- Treat metadata committed/reserved accounting and agent physical reports as two different constraints; placement must satisfy both.
- Keep delete and compaction progress visible in admin and Grafana views.
- Add failure tests where writes, repair, and cleanup all contend for the last free bytes.
- Define per-agent local admission checks for temp files and manifests so metadata reservations cannot overrun real disk.

Next decision:
- Define exact formulas for effective free capacity and repair reserve.

### 4. Security Model Needs A Root Of Trust

Risk:
- The current design requires invitations, signed envelopes, TLS, node identities, revocation, and audit logs, but the authority model is still open.

Failure modes:
- A compromised head node can issue valid-looking control messages unless metadata and agents can verify authority boundaries.
- A stolen storage-agent key can preserve access until revocation propagates.
- Invitation leakage enables Sybil joins or capacity abuse.
- Metadata leakage reveals user object counts, sizes, access patterns, and placement topology even though payloads are encrypted.
- Agent-reported capacity or health can be falsified to attract placement or trigger denial-of-service repairs.
- Signed envelopes without an explicit canonical encoding can create verification splits across versions.

Mitigations:
- Define the trust root: single owner key, admin quorum, or external IdP.
- Make revocation checks non-cacheable past a short hard max age.
- Bind invitations to expiry, trust domain, capacity ceiling, and one registration.
- Add metadata minimization and audit review for object-size and access-pattern exposure.
- Treat agent telemetry as untrusted input; reconcile it with committed metadata accounting and anomaly thresholds.
- Specify canonical serialization for signed envelopes and include protocol/schema versions in signature tests.

Next decision:
- Choose the admin authority and revocation propagation model.

### 5. Operational Complexity May Outrun The MVP

Risk:
- The product includes head nodes, metadata nodes, storage agents, clients, dashboards, Grafana, Prometheus or OpenTelemetry, repair workers, and backup/restore.

Failure modes:
- Operators cannot tell whether a write failure is due to metadata quorum, capacity admission, storage-agent disconnect, lease expiry, or policy rejection.
- Repair jobs pile up without a clear safe intervention path.
- Rolling upgrades break protocol compatibility between head, metadata, and agent versions.

Mitigations:
- Keep v1 deployment to one Compose stack with three metadata/head nodes and a small number of agents.
- Define error taxonomy across client, head, metadata, and agent APIs.
- Add runbooks before adding sharding, direct agent-to-agent repair, or erasure coding.
- Gate placement on agent protocol/schema compatibility.
- Make the admin dashboard show the exact admission blocker: metadata quorum, quota, node watermarks, repair reserve, lease failure, or revocation.

Next decision:
- Decide whether v1 bundles OpenTelemetry collector or Prometheus-only metrics.

### 6. Rust Async And Storage Hazards

Risk:
- Rust gives strong memory safety, but the design depends on tricky async networking, durable fsync semantics, local manifests, and consensus integration.

Failure modes:
- Holding locks across `.await` causes stalls or deadlocks under repair load.
- Bounded queues drop critical ACKs or anomaly reports without durable retry.
- Blocking disk fsyncs run on async executor threads and starve control streams.
- Protobuf/schema evolution accidentally breaks idempotency or signature verification.
- RocksDB bindings or custom storage layers introduce portability and operational burden on volunteer PCs.

Mitigations:
- Keep metadata state transitions in pure core crates with deterministic tests.
- Use bounded queues with explicit backpressure and durable command journals for critical events.
- Put blocking disk work behind `spawn_blocking` or dedicated worker pools.
- Add property tests for idempotency, fencing, tombstones, and duplicate delivery.
- Prefer the simplest durable local store that can pass crash-recovery tests before optimizing.
- Use one canonical time source abstraction for HLC, lease expiry checks, and test-controlled clock skew.
- Add crash tests around temp-file rename, manifest fsync, duplicate final ACK replay, and late delete/repair commands.

Next decision:
- Pick the initial embedded stores separately for metadata-node state and storage-agent manifests.

## 2026-06-04 23:05 UTC Review Notes

New or sharpened risks:
- The largest unresolved decision is not technical storage choice, but product semantics: the repo still contains both local-first P2P database language and the newer head-mediated encrypted object-store design.
- Hinted replay must be fenced as tightly as repair and delete; otherwise offline work can resurrect stale versions.
- Capacity admission needs both metadata reservations and local agent temp-space checks, because fsync, manifests, tombstones, and snapshots can fail after admission.
- Security needs a canonical signed-envelope encoding and an explicit stance that agent telemetry is adversarial until reconciled.
- Rust implementation risk is concentrated in durable async boundaries: fsync placement, lock scope around `.await`, retry journals, duplicate ACK handling, and clock-skew-dependent lease expiry.

Mitigation ideas:
- Make the next design slice a single canonical replication/repair state machine and include a preface that resolves the v1 architecture boundary.
- Add property/crash tests before service glue: command idempotency, stale fencing rejection, tombstone retention, hinted replay expiry, and capacity-reserve exhaustion.
- Define admin-visible blockers and metrics directly from the state machine so operators can distinguish capacity exhaustion, quorum loss, repair starvation, and security rejection.

Next decision:
- Confirm the v1 target as either head-mediated encrypted object storage or local-first document database. If head-mediated storage is confirmed, immediately write the replication/repair state-machine slice against that model.

## 2026-06-05 00:05 UTC Review Notes

New or sharpened risks:
- Metadata quorum loss is currently described as write rejection plus cache-bounded reads, but the exact cache freshness, revocation, and audit behavior is still not testable. Without hard rules, heads may diverge during outages.
- Lease expiry depends on time behavior across head nodes, metadata nodes, and agents. Clock skew can turn a safe late ACK into an accepted stale mutation or make healthy repair commands fail repeatedly.
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

## Next Design Decision To Resolve

Define observability and admin operations against the canonical v1 model next.

Required output:
- metrics names aligned to object/version/replica states
- admin dashboard pages and actions
- audit query surfaces
- incident runbooks
- Grafana dashboards
- alert thresholds
- operator workflows for repair, revocation, capacity, and restore

This is the highest leverage next step because the design now has enough state-machine detail to define operator visibility and response paths precisely.

