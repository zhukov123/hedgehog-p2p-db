# P2P NoSQL Production Readiness: Next Few Hours

## Objective

Turn the current architecture into a world-ready implementation plan by defining the highest-value missing pieces around observability, deployment, security, replication, admin UX, and operations.

## Highest-Value Missing Pieces

### 1. Observability Contract

Define the telemetry schema before building more features.

Build next:
- OpenTelemetry resource attributes for every node: `cluster_id`, `node_id`, `peer_id`, `version`, `role`, `region`, `trust_domain`.
- Prometheus metrics endpoint: `/metrics`.
- Health endpoints: `/health/live`, `/health/ready`, `/health/cluster`.
- Structured log envelope with `trace_id`, `span_id`, `node_id`, `peer_id`, `partition`, `operation_id`.
- Event taxonomy for replication, repair, membership, auth, compaction, backup, restore, and admin actions.

Minimum metrics:
- `p2p_peer_connected_total`
- `p2p_peer_connection_state`
- `p2p_replication_lag_seconds`
- `p2p_replication_queue_depth`
- `p2p_replication_bytes_total`
- `p2p_replication_conflicts_total`
- `p2p_repair_jobs_active`
- `p2p_repair_ranges_completed_total`
- `p2p_index_lag_seconds`
- `p2p_storage_bytes`
- `p2p_oplog_entries_total`
- `p2p_request_duration_seconds`
- `p2p_admin_actions_total`
- `p2p_auth_failures_total`

Decision needed:
- Use OpenTelemetry collector as standard, or expose Prometheus directly and add collector later?

Current local-runtime status:
- `/health/live`, `/health/ready`, and `/health/cluster` are implemented on `Hedgehog.LocalRuntime.Api` for the in-process runtime. Readiness verifies metadata availability plus running head and storage-node counts; the cluster endpoint returns the same contract without changing HTTP status for dashboards and diagnostics.

### 2. Grafana Dashboards

Ship dashboards as versioned product artifacts, not afterthoughts.

Build next:
- `dashboards/cluster-overview.json`
- `dashboards/node-health.json`
- `dashboards/replication-health.json`
- `dashboards/repair-health.json`
- `dashboards/storage-health.json`
- `dashboards/security-audit.json`
- `dashboards/request-performance.json`

Cluster Overview must answer:
- Which nodes are alive?
- Which peers are disconnected?
- Is replication falling behind?
- Are conflicts increasing?
- Are repair jobs stuck?
- Is storage or memory close to limits?
- Did auth failures spike?

Decision needed:
- Grafana-only dashboards, or also a native admin dashboard with the same panels embedded or linked?

### 3. Admin Dashboard

The admin dashboard should be the operator's first stop; Grafana should be the deep-dive surface.

Build next:
- Read-only cluster overview.
- Node inventory with peer IDs, versions, trust domains, addresses, and connection state.
- Replication page showing lag by peer and partition.
- Repair page showing active jobs, failed ranges, retry state, and recent completions.
- Storage page showing oplog size, snapshot age, compaction backlog, and tombstone pressure.
- Security page showing join requests, revoked peers, auth failures, and key age.
- Grafana shortcut links with prefilled node/peer filters.

Admin API endpoints:
- `GET /admin/status`
- `GET /admin/peers`
- `GET /admin/replication`
- `GET /admin/repair`
- `GET /admin/storage`
- `GET /admin/security`
- `POST /admin/repair/run`
- `POST /admin/snapshot`
- `POST /admin/peer/revoke`

Decision needed:
- Should admin mutations require local-only access, mTLS, signed admin tokens, or all three?

### 4. Containerized Deployment

Create a boring first-run stack that works locally and resembles production.

Build next:
- `docker-compose.yml` with three database nodes.
- Grafana with provisioned dashboards.
- Prometheus or compatible metrics backend.
- Optional OpenTelemetry collector.
- Admin dashboard container.
- Named volumes for node data.
- Example bootstrap invitation file or seed peer config.

Suggested services:
- `db-node-a`
- `db-node-b`
- `db-node-c`
- `admin-ui`
- `prometheus`
- `grafana`
- `otel-collector`

Decision needed:
- Is the first production target Docker Compose, Kubernetes, or both? Recommendation: Compose first, Kubernetes manifests after the telemetry and health contracts stabilize.

### 5. Security Baseline

Do not defer trust and identity; P2P systems become unsafe quickly without it.

Build next:
- Persistent node identity keypair.
- Signed operation envelope.
- Encrypted transport through libp2p.
- Invitation-based cluster join.
- Peer authorization list.
- Peer revocation list.
- Admin role separation.
- Audit log for admin actions.
- At-rest encryption design for local data and snapshots.

Threat model to write now:
- Sybil joins.
- Stolen node keys.
- Malicious peer sends invalid operations.
- Malicious peer floods replication.
- Replay of old operations.
- Unauthorized admin action.
- Snapshot exfiltration.

Decision needed:
- Who is the root of trust: single cluster owner key, quorum of admins, or external identity provider?

### 6. Replication and Repair Readiness

Replication must be inspectable, bounded, and resumable.

Build next:
- Operation envelope with `operation_id`, `author_peer_id`, `logical_time`, `causal_dependencies`, `signature`, `partition_key`, `payload_hash`.
- Per-peer replication checkpoint.
- Pull-based catch-up API.
- Push-based recent-change notification.
- Bounded replication queues with backpressure.
- Hash-range repair protocol.
- Repair job state machine: pending, running, paused, failed, complete.
- Conflict journal for unmerged documents.

Decision needed:
- Use vector clocks, dotted version vectors, hybrid logical clocks, or another causal metadata scheme? Recommendation: start with hybrid logical clocks plus per-document causal history, then only add heavier vectors where conflicts require them.

### 7. Operational Runbooks

Production-ready means operators know what to do when it breaks.

Build next:
- Node down runbook.
- Replication lag runbook.
- Conflict spike runbook.
- Disk pressure runbook.
- Repair failure runbook.
- Suspected compromised peer runbook.
- Backup restore runbook.
- Rolling upgrade runbook.

Each runbook should include:
- Symptoms.
- Dashboard panels to check.
- CLI/admin commands.
- Safe remediation steps.
- Escalation criteria.

Decision needed:
- What is the supported recovery point objective and recovery time objective for the first real deployment?

## Immediate Work Plan

### Hour 1: Lock the Contracts

Deliverables:
- Metrics names and labels.
- Health endpoint response schema.
- Structured log schema.
- Admin API route list.
- Operation envelope schema.

### Hour 2: Build the Demo Deployment Skeleton

Deliverables:
- Compose stack with three nodes, Prometheus, Grafana, and admin UI.
- Prometheus scrape config.
- Grafana provisioning config.
- Empty dashboard shells wired to expected metric names.

### Hour 3: Make Replication Observable

Deliverables:
- Replication queue metrics.
- Per-peer lag metrics.
- Conflict counter.
- Basic replication status admin endpoint.
- Grafana replication panel.

### Hour 4: Make Security and Ops Visible

Deliverables:
- Auth failure metric.
- Admin audit log.
- Peer inventory endpoint.
- Revocation list design.
- Security dashboard shell.
- First two runbooks: node down and replication lag.

## Team Decisions To Make

- Runtime language and storage engine.
- libp2p implementation target.
- Trust model and admin authority model.
- Causal metadata strategy.
- Whether indexes are replicated state, rebuilt local state, or both.
- Whether deletes use fixed retention or repair-aware tombstone garbage collection.
- Whether Grafana is required in all deployments or optional but bundled.
- Whether the admin dashboard is local-only or remotely accessible.
- Backup format and encryption scheme.
- First supported deployment target: Compose, Kubernetes, or bare metal.

## Production Readiness Gate

The system is not world-ready until a new operator can:
- Start a three-node cluster with one command.
- See all nodes in the admin dashboard.
- Open Grafana and inspect cluster health.
- Kill one node and observe lag/repair behavior.
- Restart the node and watch catch-up complete.
- Revoke a peer and see replication stop.
- Take a snapshot and restore onto a fresh node.
- Follow a runbook without asking the authors.
