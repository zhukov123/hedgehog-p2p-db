# P2P Object Store Deployment Stack

## Slice

This pass defines the v1 deployment contract for the Rust-first, head-mediated encrypted object store.

The deployment stack must be boring enough for local development, close enough to production to catch integration mistakes, and explicit about what is not yet a production topology. It is not the source of correctness. The metadata store, signed authority records, storage-agent durable state, and `metadata-core` transitions remain the authority.

## Deployment Targets

### Target 0: Local Developer Runtime

Purpose:
- exercise the full lifecycle on one workstation
- run integration tests with real metadata transactions
- inspect metrics, logs, audit rows, and dashboards
- reproduce repair, restart, and capacity-pressure bugs

Required processes/services:
- `metadata-db` SQLite file
- `head-1`
- `storage-agent-1`
- `storage-agent-2`
- `storage-agent-3`
- `repair-worker`
- `admin-api`
- `admin-ui`
- `prometheus`
- `grafana`
- optional `otel-collector`

This is the first deployment artifact to build. It should be started by:

```text
hedgehog local-cluster up
```

and backed by generated runtime files rather than hand-maintained operator state. Compose may still be used for Prometheus, Grafana, or other supporting services.

### Target 1: Single-Host Beta Runtime

Purpose:
- run a real small deployment for a trusted group
- validate backup, restore, alerts, dashboards, and runbooks
- keep operational surface small before Kubernetes exists

Differences from local developer runtime:
- metadata store data volume is persistent and backed up
- storage-agent volumes are persistent and size-limited
- admin endpoints require signed admin envelopes and mTLS or a private management network
- Grafana and Prometheus are protected behind admin auth
- backup archiving and restore drills are mandatory
- secrets are mounted from files or an external secret manager, not committed `.env` defaults

This target is acceptable for beta only if the project can rehearse restore, node revocation, storage-agent restart, and repair backlog recovery.

### Target 2: Production Reference

Purpose:
- document how production should be operated even before Kubernetes manifests are shipped

Minimum production posture:
- production SQL metadata backend, likely PostgreSQL, once the deferred backend track is accepted
- at least two head nodes behind a TCP/HTTP load balancer
- repair workers as horizontally scalable stateless processes with metadata store leases
- storage agents on participant machines, usually outside the server network, maintaining outbound control sessions
- Prometheus-compatible metrics scraping and Grafana dashboards
- central log sink or OpenTelemetry collector
- separate admin network or strong admin auth boundary

Kubernetes can wait until the local runtime contract, health endpoints, secret layout, and dashboard provisioning are stable.

## Runtime Service Contract

### `metadata-db`

Owns:
- authoritative metadata
- idempotency records
- leases
- repair jobs
- outbox events
- audit events

Required configuration:
- SQLite database path in the ignored runtime directory for local development
- persistent metadata path for beta deployments
- health check using a real SQL query through `hedgehog-metadata-sql`
- migration runner dependency before heads start serving writes
- backup/export hook in beta target

Must expose metrics through one of:
- metadata store exporter sidecar
- managed service metrics bridge
- direct dashboard data source only for local development

### `migrator`

Runs once per stack start before mutable services accept traffic.

Rules:
- use the same `sqlx` migration set as CI
- fail the stack if migrations fail
- record migration version in metadata store
- never run destructive cleanup implicitly

The migrator should exist even in the local runtime so developers see migration failures before service failures.

### `head-1`

Owns:
- client/admin request authentication
- signed-envelope verification
- write placement coordination
- read routing
- storage-agent session coordination
- outbox dispatch where explicitly configured

Does not own:
- final metadata authority
- payload plaintext
- revocation truth
- durable repair ownership

Required endpoints:

```text
GET /health/live
GET /health/ready
GET /metrics
```

Optional local endpoints:

```text
GET /debug/config
GET /debug/routes
```

Debug endpoints must not be enabled in beta without explicit admin protection.

### `storage-agent-*`

Owns:
- local ciphertext store
- command journal
- object manifest
- temp files
- local physical admission checks
- outbound session to a head node

Required volumes per agent:
- `data`
- `journal`
- `temp`

For local runtime, volume names should include the cluster name and agent id so restart tests do not silently share state across different clusters.

Required local limits:
- max usable bytes
- max temp bytes
- max concurrent uploads
- max repair streams
- v1 max object size

The agent must be able to restart with only its local volumes and metadata store reconciliation. No in-memory command result may be required for correctness.

### `repair-worker`

Owns:
- repair lease acquisition
- repair priority scheduling
- repair execution through head-mediated storage-agent sessions
- stale replica cleanup requests

Required controls:
- max concurrent repair jobs
- max large-object repair jobs
- per-head repair bandwidth budget
- queue starvation threshold
- repair pause switch

The worker is stateless except for metadata store leases and idempotency keys.

### `admin-api` and `admin-ui`

Own:
- operator views
- admin mutation entrypoint
- links into Grafana
- audit export
- invariant checker trigger

Rules:
- admin mutations call the same metadata transactions as normal protocol operations
- no dashboard action may patch metadata store directly
- admin UI should show exact blockers from metadata state: quorum, quota, watermarks, revocation, stale capacity, repair reserve, stale fencing, or migration lock

### `prometheus`

Owns:
- scraping head, repair-worker, admin-api, storage-agent, metadata store exporter, and optional collector metrics
- alert rules for beta gates

Required local scrape targets:

```text
head-1:9100
storage-agent-1:9100
storage-agent-2:9100
storage-agent-3:9100
repair-worker:9100
admin-api:9100
metadata-exporter:9187
```

The exact port can change, but all Rust services should use one conventional metrics port in local runtime unless there is a strong reason not to.

### `grafana`

Owns:
- provisioned dashboards
- provisioned Prometheus data source
- local admin credentials generated by the local-cluster harness

Dashboards are versioned source files, not mutable operator-only state.

Minimum provisioned dashboards:
- Cluster SLO
- Replication Health
- Capacity
- Storage Agents
- Security
- metadata store
- Outbox

### `otel-collector`

V1 stance:
- optional in local development
- recommended in beta if structured traces are enabled
- required only after trace schemas stop changing

Prometheus metrics remain the minimum observability path. The project should not block metadata or storage implementation on collector polish.

## Network Layout

Local runtime networks:
- `hedgehog-public`: client, admin UI, Grafana, and exposed head/admin ports
- `hedgehog-control`: head, repair worker, admin API, metadata store, Prometheus
- `hedgehog-agent`: storage-agent outbound sessions to head

Rules:
- metadata store is never on the public network.
- Storage agents do not need inbound public ports.
- Admin API is not public in beta unless protected by the security-authority model.
- Grafana is operator-facing only.

The local network model should deliberately mirror the production trust boundary: public head nodes, private metadata, outbound-only agents, protected admin surfaces.

## Volumes and Persistence

Local development can recreate volumes by explicit command:

```text
hedgehog local-cluster reset
```

The reset command must be loud and recoverable where practical. It should not be part of normal `up` or `down`.

Persistent volumes/files:
- `metadata-db`
- `storage-agent-1-data`
- `storage-agent-1-journal`
- `storage-agent-1-temp`
- `storage-agent-2-data`
- `storage-agent-2-journal`
- `storage-agent-2-temp`
- `storage-agent-3-data`
- `storage-agent-3-journal`
- `storage-agent-3-temp`
- `prometheus-data`
- `grafana-data`

Beta backup requirements:
- metadata store base backup plus backup archive
- admin-exported audit evidence bundle
- Grafana dashboard source in git
- Prometheus data can be disposable if dashboards and alert rules are reconstructable
- storage-agent payload backup is not required for beta if replication and repair can restore durability after node loss, but node-loss drills must prove this

## Secrets and Bootstrap

Local cluster generated files:
- cluster id
- development admin root key
- head node identity
- storage-agent identities
- invitation tokens
- metadata database path and file permissions
- Grafana local admin password

Rules:
- generated secrets live under an ignored local runtime directory
- repository docs may include examples only
- invitation tokens are one-time and scoped
- beta secrets must be loaded from mounted files or an external secret provider
- root/admin signing material must not be baked into images

The first local cluster may generate a development admin key automatically. Beta must require explicit operator-provided authority material.

## Images and Binaries

Recommended image split:
- `hedgehog-head`
- `hedgehog-storage-agent`
- `hedgehog-repair`
- `hedgehog-admin-api`
- `hedgehog-admin-ui`
- `hedgehog-cli`
- `hedgehog-migrator`

For early implementation, one Rust workspace image with multiple binary entrypoints is acceptable if:
- binary commands are explicit
- image tags include git SHA and schema compatibility
- runtime config chooses role without changing code

Do not hide role-specific behavior behind vague image defaults.

## Health and Readiness

`/health/live` means the process is alive and its runtime is not wedged.

`/health/ready` means the service can safely accept its role's traffic.

Readiness requirements:
- head: metadata store reachable, migrations current, authority cache loaded within max age, outbound storage-agent coordinator active
- storage-agent: data/journal/temp volumes writable, manifest opened, local capacity limits loaded, outbound session established or retrying
- repair-worker: metadata store reachable, migrations current, can acquire or observe leases, repair not administratively paused unless readiness explicitly reports paused
- admin-api: metadata store reachable, authority policy loaded, audit writes available

The local-cluster harness should fail fast if readiness never converges.

## Upgrade Policy

V1 upgrade order:
1. Run migrations.
2. Start heads in compatibility mode.
3. Start repair workers.
4. Restart admin services.
5. Restart storage agents gradually.
6. Run invariant checker.
7. Confirm dashboards and alerts.

Compatibility gates:
- protocol version accepted by head
- envelope canonicalization version
- SQL migration version
- storage-agent manifest version
- admin API version

Beta should support only forward migrations plus restore-based rollback. A rollback story that relies on guessing which rows to delete is not acceptable.

## Failure Drills

The local deployment must make these drills easy:
- kill one storage agent during upload
- kill one storage agent after commit
- restart storage agent with pending journal results
- kill repair worker during repair
- kill head during upload coordination
- pause metadata store and verify write rejection/read degraded behavior
- fill a storage-agent temp volume
- revoke a node and watch repair enqueue
- restore metadata store into a new local cluster

Each drill needs:
- CLI command
- expected admin page state
- expected Grafana panel movement
- expected audit/outbox evidence
- pass/fail invariant

## Implementation Order

1. Add `hedgehog-local-cluster` config generation before polished services exist.
2. Add SQLite metadata store plus migrator runtime skeleton.
3. Add fake health/metrics endpoints for planned Rust binaries.
4. Add Prometheus scrape config and empty Grafana dashboards wired to canonical metric names.
5. Add storage-agent persistent volumes and size limits.
6. Add restart and kill drills once storage-agent journal code exists.
7. Add restore/restore drill once real migrations and audit/outbox tables exist.

This means local deployment work starts earlier than its crate position in the roadmap suggests. The harness can be thin at first, but it should exist while metadata transactions are being built.

## Decisions Locked

- Generated local runtime is the first supported deployment target.
- Kubernetes is deferred until health, metrics, config, and secret contracts are stable.
- metadata store is private to the control network and never directly exposed.
- Grafana and Prometheus are bundled in the standard local stack.
- OpenTelemetry collector is optional for v1 local development.
- Storage agents keep outbound-only connectivity and persistent local volumes.
- The migrator is a first-class service, not a manual side instruction.
- Local-cluster generation belongs in the Rust workspace as a test harness, not only as static YAML.

## Next Unresolved Portion

Before implementation begins, write the v1 implementation contract that ties this deployment stack to the first crates:
- choose `sqlx` explicitly for metadata store access unless a blocker is found
- choose deterministic envelope encoding and golden-vector layout
- publish the canonical state glossary mapping design states to Rust enum names, SQL values, metric labels, and admin labels
- define write reservation lifecycle states and expiry rules
- set v1 max object size and transfer classes
- define the first `hedgehog local-cluster up` generated file layout
- define where generated local secrets live and how they are ignored
