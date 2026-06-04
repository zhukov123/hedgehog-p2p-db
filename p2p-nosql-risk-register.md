# P2P NoSQL Risk Register

## Purpose

Track implementation risks and unresolved decisions for the Rust-first architecture.

This register focuses on failure modes, operational complexity, security gaps, metadata-plane risk, replication edge cases, capacity exhaustion, and Rust implementation hazards.

## Current Highest Risks

### 1. Metadata Plane Becomes The Real Control Plane Bottleneck

Risk:
- The design correctly makes metadata strongly consistent, but every placement, lease, quota, repair, delete, and visibility transition depends on it.
- A single Raft cluster is the right first shape, but it can become a throughput, availability, and schema-migration choke point.

Failure modes:
- Metadata quorum loss freezes writes, admin mutations, repair ownership, and capacity changes.
- Slow metadata snapshots or compaction can delay write admissions.
- Head-node read caches may accidentally outlive revocation or account suspension policy.

Mitigations:
- Define explicit read-cache max ages by metadata type.
- Add load tests for write admission, repair-job churn, and heartbeat/capacity-report volume before feature expansion.
- Keep all metadata commands idempotent and replay-testable.
- Define a degraded-mode policy that allows only safe reads and telemetry buffering when metadata quorum is unavailable.

Next decision:
- Pick the first metadata durability stack: `openraft + redb`, `openraft + RocksDB`, or another Raft/store pairing.

### 2. Replication And Repair State Machine Is Still Underdefined

Risk:
- Metadata and storage-agent slices mention repair, leases, and orphan cleanup, but the complete object/replica state machine is not yet locked.

Failure modes:
- A repair copy races with a delete tombstone or a newer object version.
- A source replica becomes corrupt or lost during a repair copy.
- Re-replication consumes the remaining repair reserve and blocks more urgent repairs.
- Tombstones are collected too early and deleted data reappears from an offline node.

Mitigations:
- Define a formal replica state transition table with legal transitions and fencing requirements.
- Make repair source selection prefer recently verified committed replicas.
- Add tombstone retention rules tied to max offline duration and repair audit completion.
- Add repair priority classes: durability loss, corruption, node drain, policy change, audit.

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

Mitigations:
- Define the trust root: single owner key, admin quorum, or external IdP.
- Make revocation checks non-cacheable past a short hard max age.
- Bind invitations to expiry, trust domain, capacity ceiling, and one registration.
- Add metadata minimization and audit review for object-size and access-pattern exposure.

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

Next decision:
- Pick the initial embedded stores separately for metadata-node state and storage-agent manifests.

## Next Design Decision To Resolve

Define the replication and repair state machine next.

Required output:
- object and replica states
- legal transitions
- command ownership and fencing rules
- repair priority order
- tombstone retention and garbage collection rules
- capacity behavior during repair
- admin-visible repair progress schema

This is the highest leverage next step because it closes the biggest gap between the current architecture and an implementable Rust system.
