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
