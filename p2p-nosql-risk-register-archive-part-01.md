# P2P NoSQL Risk Register Archive Part 01

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

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
