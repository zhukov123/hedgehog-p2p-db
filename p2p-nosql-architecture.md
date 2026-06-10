# Hedgehog P2P Object Store Architecture Spec

## Canonical V1 Framing

Hedgehog is a peer-to-peer object store, not a DynamoDB-compatible NoSQL database in v1.

The v1 system is:
- a head-mediated storage network
- a client-encrypted whole-object store
- a pool of participant storage agents
- a transactional metadata authority
- an operator-visible repair, capacity, audit, and observability surface

The first implementation uses SQLite as the metadata authority to keep the system easy to build, test, and run locally. The metadata layer should remain SQL-oriented so PostgreSQL can become a later production backend without changing object, replica, repair, lease, or audit semantics.

## What V1 Is

V1 stores opaque encrypted objects.

Core properties:
- clients encrypt object payloads before upload
- heads route requests and coordinate transfers
- storage agents store ciphertext and local evidence
- metadata decides placement, visibility, leases, capacity, repair, revocation, and audit
- writes become visible only after the required replicas are durable
- repair is head-mediated in v1
- direct agent-to-agent repair is deferred

## What V1 Is Not

V1 is not:
- a local-first database
- a general document database
- a DynamoDB-compatible API
- a JSON query engine
- a secondary-index service over plaintext attributes
- a system with offline writes that later reconcile without metadata authority
- a central-storage service where heads hold all data

Older database-oriented docs in this repository are historical context unless a newer implementation contract explicitly adopts them.

## Design Principles

1. Keep the authority model simple enough to implement correctly.
2. Store only ciphertext on infrastructure and participant machines.
3. Make metadata transitions transactional, named, and testable.
4. Make replication, repair, capacity, and deletes inspectable.
5. Fail closed when authority is unavailable or stale.
6. Prefer whole-object transfer before chunking, erasure coding, or direct peer repair.
7. Keep v1 deployable on one machine with SQLite and multiple local storage agents.
8. Preserve a migration path to stronger production metadata backends.

## Core Architecture

### 1. Clients

Clients:
- own user keys
- encrypt object payloads before upload
- compute payload hashes
- send signed requests to a head node
- decrypt fetched payloads locally

Servers must never require plaintext payload access.

### 2. Head Nodes

Head nodes are public coordinators.

They:
- authenticate signed envelopes
- rate-limit and validate requests
- call metadata workflows for authority decisions
- coordinate uploads, reads, deletes, and repair copies
- maintain outbound storage-agent sessions
- expose client and admin APIs
- publish already-committed outbox work where configured

They must not independently decide:
- object visibility
- replica placement
- tenant authorization
- node revocation
- write admission
- repair ownership
- durable audit results

### 3. Metadata Authority

The metadata authority owns:
- tenants and datasets
- opaque object IDs, lookup hashes, and version records
- replica placement and health
- write reservations and capacity accounting
- leases and fencing tokens
- repair jobs
- storage-agent identity and revocation
- invitations and admin roles
- outbox and audit rows

V1-alpha backend:
- SQLite
- single metadata writer authority
- explicit migrations
- deterministic integration tests
- local backup/export/restore drills

Deferred production backend:
- PostgreSQL
- multi-head/multi-worker concurrency
- PITR, WAL archiving, failover, and operational backup tooling

### 4. Storage Agents

Storage agents run on participant machines.

They:
- keep outbound sessions to heads
- reserve a configured disk budget
- store object ciphertext
- keep local manifests and command journals
- enforce local hard capacity rejection
- reject stale fencing tokens after restart
- report capacity, health, final command results, and anomalies

Agents do not decide object liveness or placement.

### 5. Data Model

V1 data model:
- tenant
- dataset
- opaque `object_id`
- `object_lookup_hash` for deterministic human-name lookup without plaintext names
- immutable object version
- ciphertext length and content hash
- encryption metadata reference
- replica rows
- tombstone/delete marker rows

Queryable metadata is operational metadata, not plaintext object attributes. V1 does not require plaintext object names, paths, or filenames in metadata. Human-readable names belong in encrypted client metadata.

The lookup hash is computed by the client:

```text
object_lookup_hash = HMAC-SHA256(dataset_lookup_secret, normalized_object_name)
```

The dataset lookup secret is available only to authorized clients. The metadata store can use `object_lookup_hash` for deterministic lookup, but cannot cheaply guess names such as `photo.jpg`, `taxes.pdf`, or `resume.docx` without the secret.

Application-level JSON documents, CRDT fields, secondary indexes, and query views are deferred until the object-store foundation is correct.

### 6. Write Path

1. Client encrypts an object and computes the content hash.
2. Client sends a signed write-intent request to a head.
3. Head asks metadata to create a write reservation and planned replicas.
4. Metadata checks tenant/dataset quota, node eligibility, placement, capacity, delete epoch, and revocation state.
5. Head streams ciphertext to selected storage agents.
6. Agents write temp files, verify bytes and hash, fsync, update manifest/journal, and return final ACKs.
7. Head submits final ACKs to metadata.
8. Metadata accepts only ACKs with matching reservation, version, node, fencing token, placement epoch, and delete epoch.
9. Metadata commits the version once the required healthy replica count is met.

### 7. Read Path

Reads are metadata-authorized.

V1 supports:
- read latest committed version by opaque object id or lookup hash
- read specific committed version
- fetch from any eligible healthy replica
- fail closed if metadata, revocation, placement, or read token authority is stale

### 8. Delete and GC

Deletes create metadata tombstones/delete markers before physical cleanup starts.

Rules:
- stale writes and stale repair completions must not resurrect deleted data
- storage cleanup is retryable and idempotent
- metadata tombstones outlive retry, repair, audit, and clock-skew windows
- GC must keep enough state to reject late completions

### 9. Repair

V1 repair is head-mediated:

```text
source agent -> head -> target agent
```

The head coordinates ciphertext movement but does not decrypt payloads.

Direct agent-to-agent repair is deferred because it requires direct peer discovery, NAT traversal, peer-to-peer authorization, revocation enforcement, abuse controls, transfer observability, and more complex failure recovery.

### 10. Capacity Admission

Capacity admission is pessimistic.

Metadata tracks logical reservations and committed bytes. Storage agents separately enforce local physical admission.

A write requires:
- tenant quota available
- dataset quota available if configured
- enough eligible nodes
- placement diversity satisfied
- per-node effective free capacity
- repair reserve preserved
- fresh enough capacity reports
- local agent admission before bytes are accepted

### 11. Security

Required:
- signed client, admin, and agent envelopes
- authenticated storage-agent identities
- invitation-based joining
- explicit revocation epochs
- signed admin actions
- audit rows for authority-changing operations
- client-side payload encryption
- metadata privacy controls

### 12. Observability

The operator surface must show:
- metadata health
- head health
- storage-agent health
- replica counts and repair deficit
- write reservations and stuck workflows
- capacity pressure
- revocation lag
- outbox lag
- audit event continuity
- local-agent anomalies

Grafana dashboards and the admin API are views over authority state. They are not authority themselves.

## Deferred Design

Deferred v2/v3 choices live in [p2p-object-store-deferred-design.md](p2p-object-store-deferred-design.md).
