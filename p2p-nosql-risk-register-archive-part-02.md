# P2P NoSQL Risk Register Archive Part 02

This file preserves archived risk-register content split from `p2p-nosql-risk-register.md` so GitHub API publishing can avoid large single-file payload limits.

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
