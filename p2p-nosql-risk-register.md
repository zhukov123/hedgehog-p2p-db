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
- Follow [p2p-nosql-implementation-roadmap.md](p2p-nosql-implementation-roadmap.md): build `hedgehog-metadata-core` and `hedgehog-metadata-pg` first, then storage agents, head nodes, repair, admin, observability, and local-cluster polish.

Next decision:
- Keep the current v1 target as the head-mediated encrypted object store and treat local-first document database semantics as deferred unless explicitly reselected.

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
- Make the Milestone 1 beta blocker the real PostgreSQL migration set plus metadata-core transition tests before public API or storage protocol expansion.

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

## 2026-06-05 03:05 UTC Severus Observability/Admin Review

Accepted findings:
- PostgreSQL metadata state is the operational source of truth, storage-agent reports are evidence, and outbox/audit logs are the timeline.
- Metrics must align to object/version/replica/lease/repair/capacity/security states, while avoiding object/version IDs as metric labels.
- Required admin pages before beta: cluster overview, objects/versions, replicas, repair, capacity, security/authority, and audit.
- Required Grafana dashboards before beta: cluster SLO, replication health, capacity, storage agents, security, PostgreSQL, and outbox.
- Critical alerts must cover replica deficits, PostgreSQL primary outage, PITR/WAL failure, revoked principal acceptance, stale outbox events, emergency capacity, and restore drill failure.
- Admin actions must go through the same `metadata-core` transactions as normal protocol traffic.
- Beta requires runbooks for repair backlog, capacity pressure, node revocation, head compromise, failed restore, and stale outbox events.

Risk register changes:
- Added `p2p-nosql-admin-observability-ops.md` as the canonical observability/admin slice.
- Raised stale outbox, restore uncertainty, dashboard-derived authority, and admin bypasses as beta-blocking risks.
- Moved the next design decision to implementation roadmap and Rust workspace sequencing.

## 2026-06-05 03:05 UTC Roadmap Risk Review

New or sharpened risks:
- The roadmap is now strong enough to start implementation, but the first code choices can still split invariants across crates if `hedgehog-types`, `hedgehog-crypto`, `hedgehog-metadata-core`, and `hedgehog-metadata-pg` each define their own state, error, serialization, or transaction semantics.
- PostgreSQL remains the right v1 authority, but the roadmap leaves the database access choice open. Mixing `sqlx` and `tokio-postgres`, or starting migrations before choosing the transaction/test approach, would create avoidable integration drag.
- The first migrations are listed, but beta safety depends on migration fixtures, restore checks, invariant checkers, and outbox replay tests being built with the schema rather than after services already depend on it.
- `hedgehog-crypto` is early in the build order, which is correct, but signed-envelope canonicalization must be frozen before API glue exists. Retrofitting canonical bytes after clients, heads, and agents sign messages risks incompatible signatures and downgrade holes.
- `hedgehog-storage-agent` is intentionally later, yet its manifest and command journal are correctness-critical. If local durability is treated as ordinary file plumbing, crash recovery can violate idempotency, fencing, orphan cleanup, and final ACK replay.
- `hedgehog-local-cluster` appears last, but the project needs a thin local-cluster harness earlier than polished admin/observability so metadata, repair, storage-agent restart, and PostgreSQL transaction behavior can be tested together.
- Whole-object replication still needs an explicit v1 maximum object size before capacity fixtures are meaningful; otherwise a single large object can invalidate temp reserve, repair reserve, head bandwidth, and storage-agent worker assumptions.

Mitigation ideas:
- Make `hedgehog-types` the only crate allowed to define canonical state enums, IDs, epochs, and protocol error categories.
- Choose one PostgreSQL client before migrations. Prefer `sqlx` unless there is a concrete reason to need lower-level `tokio-postgres`, because compile-time query checking and migration tooling fit this roadmap.
- Treat migration 1 as a test product: schema, forward migration, seeded fixtures, invariant checker stub, restore/replay notes, and metadata-pg integration tests land together.
- Freeze deterministic signed-envelope encoding and golden vectors before head-node or CLI signing workflows exist.
- Pull a minimal `hedgehog-local-cluster` smoke harness forward once metadata-pg can create tenants, datasets, nodes, and object write intents.
- Create crash tests for storage-agent manifest/journal before adding network service behavior.
- Decide v1 `max_object_size` and transfer classes before capacity admission tests are considered passing.

Next decision:
- Pick the first implementation contract: `sqlx` vs `tokio-postgres`, deterministic envelope encoding, storage-agent manifest store, v1 max object size, and how early the local-cluster harness starts. The highest-leverage first choice is the PostgreSQL access and migration/test stack, because it shapes every metadata-core and metadata-pg boundary.

## Next Design Decision To Resolve

The first implementation contract is now captured in [p2p-nosql-implementation-contract.md](p2p-nosql-implementation-contract.md).

Accepted choices:
- PostgreSQL client: `sqlx`
- signed-envelope encoding: deterministic CBOR
- canonical state labels owned by `hedgehog-types`
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
