# Hedgehog Deferred Design Decisions

## Purpose

This document keeps non-v1 ideas visible without letting them distort the v1 architecture.

V1 is a head-mediated, client-encrypted, whole-object P2P object store with SQLite metadata authority. Anything below is intentionally deferred until the v1 object, replica, repair, lease, capacity, audit, and observability model is proven.

## V2 Candidates

### PostgreSQL Metadata Backend

Deferred from v1-alpha.

Reason:
- SQLite keeps the first implementation simple and local.
- PostgreSQL becomes valuable when the system needs multiple heads, multiple repair workers, stronger concurrent workflow behavior, PITR, WAL archiving, standby/failover, and production operational tooling.

Decision to revisit when:
- more than one head or repair worker must mutate metadata concurrently
- SQLite write contention becomes a real limit
- production restore and failover requirements exceed SQLite backup/export workflows

### Direct Agent-To-Agent Repair

Deferred from v1.

V1 repair path:

```text
source agent -> head -> target agent
```

Deferred path:

```text
source agent -> target agent
```

Reason:
- direct repair requires peer discovery, NAT traversal, relay behavior, peer-to-peer authorization, direct transfer leases, revocation enforcement, abuse controls, bandwidth policy, and transfer observability between untrusted agents
- v1 can prove repair correctness faster when heads observe and coordinate the whole transfer

Decision to revisit when:
- head-mediated repair bandwidth is a measured bottleneck
- identity, revocation, leases, fencing, and repair telemetry are stable
- agents have a proven direct encrypted transport and authorization protocol

### Advisory DHT Provider Discovery

Deferred from v1.

V1 location model:

```text
metadata store -> authoritative object location
head -> coordinates reads/writes/repair
agents -> store ciphertext
```

Potential v2 hybrid model:

```text
metadata store -> authority
DHT -> advisory discovery/cache of possible providers
head -> intersects DHT results with metadata-approved replicas
```

Reason:
- a DHT can help discover which peers claim to have a version, content hash, dataset participant, or reachable node address
- a DHT must not decide object visibility, authorization, delete safety, revocation, replica health, repair completion, or capacity admission
- stale DHT provider records can resurrect deleted data if they are treated as authority
- malicious or revoked agents can advertise false provider records

V2 rule if adopted:

```text
usable providers = DHT providers ∩ metadata-approved replicas
```

Never:

```text
usable providers = DHT providers
```

Possible signed provider record:

```text
key: object_version_id
value:
  node_id
  dataset_id
  version_id
  content_hash
  advertised_at
  expires_at
  agent_signature
  metadata_revision_seen
```

Decision to revisit when:
- metadata-authoritative v1 reads, repair, revocation, and deletes are reliable
- direct or semi-direct peer discovery becomes valuable enough to justify the additional attack surface
- the system has clear rules for short-lived signed provider records and stale-record rejection

### Chunked Object Transfer

Deferred from v1.

Reason:
- whole-object transfer keeps manifest, repair, hashing, admission, and cleanup simpler
- chunking multiplies states: partial chunks, chunk manifests, per-chunk repair, range verification, partial GC, and chunk-level capacity reservations

Decision to revisit when:
- the 64 MiB whole-object cap is too small
- retries waste unacceptable bandwidth
- range reads become a product requirement

### Erasure Coding

Deferred from v1.

Reason:
- replication factor is easier to explain, test, and repair
- erasure coding complicates placement, verification, repair scheduling, degraded reads, and capacity accounting

Decision to revisit when:
- storage overhead becomes a dominant cost
- repair and verification are already reliable with full replicas

### Browser Or Mobile Storage Agents

Deferred from v1.

Reason:
- durable storage, background execution, networking, quota management, and key persistence vary heavily across browser and mobile platforms
- desktop/server agents are a simpler first participant model

Decision to revisit when:
- the agent protocol is stable
- resumable local manifests and background sync are proven on desktop

## V3 Candidates

### Local-First Writes

Deferred from v1 and v2 unless the product goal changes.

Reason:
- local-first writes without metadata authority require causal histories, conflict resolution, offline authorization, eventual placement reconciliation, and security rules for stale or revoked actors
- this is closer to a replicated database than the accepted v1 object-store model

Decision to revisit when:
- the project intentionally expands from object storage into collaborative/offline data structures

### JSON Document Querying

Deferred from v1.

Reason:
- servers do not see plaintext payloads
- secondary indexes over JSON require either plaintext access, client-generated index tokens, searchable encryption, or application-specific encrypted metadata
- all options introduce metadata leakage and product complexity

Decision to revisit when:
- v1 object storage is stable
- the project has an explicit leakage model for indexed encrypted data

### CRDT Data Types

Deferred from v1.

Reason:
- CRDTs are useful for local-first databases, counters, sets, maps, and collaborative records
- the accepted v1 stores immutable encrypted object versions, not mergeable plaintext fields

Decision to revisit when:
- the project adds an application-data layer above the object store

### DynamoDB-Like API Compatibility

Deferred from v1.

Reason:
- DynamoDB semantics imply partitions, sort keys, conditional expressions, streams, secondary indexes, and predictable query behavior over item attributes
- v1 exposes object put/get/delete/stat/list-style semantics over encrypted objects

Decision to revisit when:
- the object store is stable and there is a deliberate product decision to build a database API layer

### Federated Clusters

Deferred from v1.

Reason:
- federation requires cross-cluster identity, policy, revocation, audit, quota, and repair semantics
- the first system should prove one cluster well

Decision to revisit when:
- single-cluster authority, backup, restore, and revocation are mature

### Custom Consensus-Backed Metadata

Deferred from v1.

Reason:
- building a correct metadata consensus system adds membership, snapshots, compaction, state-machine determinism, corruption recovery, migration, backup/restore, and rolling-upgrade complexity
- SQLite first and PostgreSQL later cover the early implementation and production paths more directly

Decision to revisit when:
- PostgreSQL no longer fits deployment goals
- running a self-contained metadata consensus system becomes more valuable than using a mature SQL backend

## Explicitly Not V1

These must not appear in v1 implementation contracts, migrations, metrics, dashboards, fixtures, or admin labels as if they are current behavior:
- direct agent-to-agent repair
- DHT provider discovery as authority
- local-first offline writes
- JSON secondary indexes
- CRDT field merges
- erasure coding
- chunk-level repair
- browser/mobile storage-agent support
- DynamoDB-compatible API behavior
- multi-cluster federation
