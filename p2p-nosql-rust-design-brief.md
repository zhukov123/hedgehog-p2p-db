Exit code: 0
Wall time: 0.6 seconds
Output:
# Rust-First P2P NoSQL Design Brief

## Goal

Design a production-ready peer-to-peer NoSQL storage system in Rust.

The system should support:
- public head nodes on IP addresses and ports
- storage nodes behind firewalls
- client-side encryption with keys known only to the user
- multiple users sharing a distributed pool of volunteer storage PCs
- fixed disk reservations on participant machines
- configurable replication factor per user or per dataset
- automatic re-replication when nodes fail
- built-in observability with Grafana and admin dashboards
- capacity-based admission control that rejects writes before storage is exhausted

## Core Product Shape

This is not a monolithic database.

It is:
- a public coordination and routing tier
- a strongly consistent metadata plane
- an encrypted object storage network
- a client application for encrypting, uploading, downloading, and decrypting data
- a storage agent installed on participant PCs

## Key Architectural Decisions

### 1. Public Head Nodes

Head nodes are publicly reachable and act as:
- account provisioning and auth entrypoint
- replica placement coordinator
- metadata router
- repair scheduler
- admin API surface
- load-balanced ingress points

They should be horizontally scalable and stateless enough that any head node can take traffic.

### 2. Storage Nodes

Storage nodes run on participant machines behind firewalls.

They should:
- reserve a fixed amount of disk space
- keep outbound connections open to the head tier
- store only ciphertext
- expose health and capacity telemetry
- accept store, fetch, delete, and repair instructions from head nodes
- never need inbound firewall holes

### 3. Client-Side Encryption

All user data must be encrypted on the client before upload.

Properties:
- user owns the data key
- servers never see plaintext
- storage nodes are untrusted
- head nodes should not be able to decrypt user payloads

Recommended model:
- per-user master key
- per-object data keys
- key wrapping for multi-device support
- optional recovery path as a separate, explicit product choice

### 4. Whole-Object Replication

Payloads are small enough that we do not need chunking or erasure coding in the first version.

Use:
- whole-object ciphertext replication
- replication factor `N`
- fetch-from-any-replica reads
- repair by copying whole objects to replacement nodes

This is much easier to reason about than sliced storage.

### 5. Metadata Plane

The metadata plane must be strongly consistent.

It should store:
- accounts
- user keys metadata, never raw keys
- storage node registrations
- reservations and quotas
- object placement records
- replica sets
- version state
- leases
- repair jobs
- capacity state

This plane is the brain of the system.

V1 backend decision:
- PostgreSQL is the default authoritative metadata store for the first production slice.
- Use transactions, constraints, indexes, migrations, WAL durability, PITR, and rehearsed failover rather than building a custom consensus database immediately.
- FoundationDB and Rust-native Raft remain later options if metadata scale, distribution, or product goals justify the operational cost.

## Rust Implementation Direction

Rust should be the main programming language for:
- head nodes
- storage node agent
- client library
- admin API
- observability plumbing
- on-disk data handling

Suggested Rust ecosystem building blocks:
- async runtime: `tokio`
- HTTP APIs: `axum` or `actix-web`
- gRPC or internal RPC if needed: `tonic`
- serialization: `serde`, `serde_json`, `bincode` or `postcard`
- crypto: `ring`, `rustls`, `age`-style primitives if appropriate
- metrics: `metrics`, `prometheus-client`, or OpenTelemetry Rust SDK
- logging: `tracing`, `tracing-subscriber`
- config: `figment` or plain typed config
- metadata database access: `sqlx` with explicit migrations against PostgreSQL
- storage-agent local data: file-per-object plus an embedded manifest/index store such as `redb`

## Required System Properties

### Availability

- reads should stay available during node failure where possible
- writes should be accepted when there is enough healthy replica capacity
- if capacity is insufficient, reject new writes early

### Durability

- a write is successful only after enough replicas confirm persistence
- hinting and repair should cover temporary outages
- permanent failures should trigger re-replication

### Confidentiality

- payloads are encrypted on the client
- storage nodes cannot inspect plaintext
- head nodes should only see metadata necessary for routing and accounting
- metadata remains sensitive: object sizes, timing, access patterns, placement, namespace shape, node capacity, and retention state can leak information even when payloads are encrypted

### Integrity

- all stored objects should be verifiable by hash or MAC
- replication should detect corruption
- version metadata must prevent silent overwrites

### Operability

- admin dashboard is first-class
- Grafana is part of the standard container stack
- metrics and logs exist before feature expansion
- failure states must be observable and actionable

## Capacity Policy

If the system is approaching storage exhaustion, it must stop accepting writes before it is actually full.

Use at least three thresholds:
- warning watermark
- soft backpressure watermark
- hard reject watermark

At the hard watermark:
- reject new object writes
- reject new reservations
- reject replica expansion
- allow reads, deletes, repair, and compaction

This reserve is required for:
- re-replication
- repair
- compaction
- snapshots
- emergency recovery

## Replication and Repair

The system should support:
- user-selected replication factor
- per-object or per-dataset replica placement
- automatic re-replication when a node fails
- hinted handoff or equivalent temporary holding of writes
- anti-entropy repair
- background convergence checks

Repair should be built around:
- node health
- object version state
- placement metadata
- capacity availability

## DNS and Load Balancing

Use DNS as a bootstrap and balancing layer for head nodes.

Do not rely on DNS as the source of truth for:
- replica placement
- leases
- object ownership
- repair decisions

DNS should help distribute ingress, but the metadata plane must control the actual system state.

## Security and Trust Model

The network contains untrusted storage hosts on random PCs.

Threat model should include:
- malicious storage nodes
- malicious users trying to exhaust capacity
- replay of old data
- corrupted payloads
- compromised head node
- node churn
- sybil-style participation abuse

Minimum defenses:
- TLS on all links
- node identity keys
- signed control messages
- admission control
- audit logs
- revocation
- rate limiting

## Observability

Observability must be built in from the start.

Requirements:
- metrics endpoint
- health endpoints
- structured logs
- tracing
- admin dashboard
- Grafana dashboards
- containerized observability services

Admin dashboard should show:
- nodes
- peer status
- storage utilization
- replication lag
- repair jobs
- conflicts
- warnings
- alerts
- links to Grafana

## User Experience

The product flow should be:
1. user creates an account
2. user obtains a client key or recovery material
3. user connects storage or installs a storage agent
4. user reserves capacity
5. user stores encrypted objects
6. system replicates and repairs in the background

The system should be understandable enough for community and university deployment.

## Suggested First Design Deliverables

The next design pass should produce:
- component diagram
- Rust crate layout
- metadata schema (started in `p2p-nosql-metadata-plane.md` and `p2p-nosql-postgresql-schema-plan.md`)
- replication state machine (started in `p2p-nosql-replication-repair-state-machine.md`)
- capacity-control policy (started in `p2p-nosql-capacity-admission.md`)
- security model (started in `p2p-nosql-security-authority.md`)
- admin API surface (started in `p2p-nosql-admin-observability-ops.md`)
- telemetry schema (started in `p2p-nosql-admin-observability-ops.md`)
- Grafana dashboard list (started in `p2p-nosql-admin-observability-ops.md`)
- container stack layout
- failure-mode matrix

## Non-Goals For Version 1

Do not add these yet:
- erasure coding
- chunked distributed downloads
- full-text search over encrypted content
- SQL
- distributed joins
- arbitrary cross-object transactions
- Byzantine consensus everywhere

## Open Decisions

- What is the canonical key recovery story?
- What exact PostgreSQL HA and backup topology is required for beta?
- Should head nodes cache metadata or remain thin routers?
- Should storage nodes use push, pull, or hybrid repair sync?
- What exact on-disk store should Rust use initially?
- How strict should quota enforcement be at the user and tenant level?
- How do we prevent abuse from volunteer storage nodes?
- What is the concrete PostgreSQL schema, index, migration, and outbox plan for the metadata state machine?
- What is the exact capacity admission and repair-reserve formula?
- What is the v1 security authority model for invitations, revocation, signed envelopes, and head-node limits?
- What should the admin dashboard, metrics taxonomy, alerts, and incident runbooks look like against the canonical v1 model?
- What is the crate-by-crate implementation roadmap and beta exit plan?

## Working Thesis

The safest architecture is:
- public head nodes for routing and coordination
- PostgreSQL as the strongly consistent v1 metadata service behind them
- client-side encryption with user-owned keys
- untrusted storage nodes that only hold ciphertext
- whole-object replication
- background re-replication and repair
- hard admission control before storage exhaustion
- Rust for the entire system stack

