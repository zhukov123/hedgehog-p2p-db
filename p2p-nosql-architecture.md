# Peer-to-Peer NoSQL Database Architecture Spec

## Goal

Build a full-featured peer-to-peer NoSQL database that feels familiar to DynamoDB users but runs without a central control plane.

The design target is:
- local-first writes
- offline operation
- automatic replication and convergence
- community or university deployment
- enough operational tooling to be teachable and supportable

## What This Is

This is not a general SQL database.
It is a distributed document / key-value / wide-column system with:
- primary-key access as the core path
- optional secondary indexes
- multi-peer replication
- conflict handling that is explicit and deterministic
- strong security and observability

## Design Principles

1. Prefer availability and locality over global coordination.
2. Keep the primary API narrow and predictable.
3. Make replication incremental, resumable, and inspectable.
4. Support offline use as a first-class mode.
5. Treat security, governance, and observability as core features.
6. Make the system understandable enough for students and operators.
7. Ship with built-in dashboards and operational visibility from day one.

## Core Architecture

### 1. Network Layer

Use a P2P stack such as libp2p for:
- encrypted peer connections
- peer discovery
- NAT traversal
- relay support
- protocol multiplexing

This layer should support browsers, mobile devices, desktops, and servers.

### 2. Identity and Membership

Each node needs:
- a persistent cryptographic identity
- a peer record with addresses and metadata
- a membership protocol
- peer authorization and revocation

For community use, support:
- invitation-based clusters
- federated cluster peering
- optional trust domains

### 3. Storage Engine

Use an embedded local engine with:
- append-only operation log
- durable snapshots
- compaction
- tombstone handling
- background repair metadata

The local store must work fully disconnected.

### 4. Data Model

Start with:
- primary key
- optional sort key
- JSON-like attributes
- sparse secondary indexes

This mirrors the parts of DynamoDB that are easy to teach and scale.

### 5. Replication Engine

Replication should be:
- incremental
- peer-to-peer
- resumable after interruption
- able to sync from multiple sources

Use a two-path approach:
- operation sync for recent changes
- state reconciliation for repair and catch-up

### 6. Conflict Model

Use conflict-free types where possible:
- counters
- sets
- maps
- registers
- append-only feeds

For non-CRDT data:
- attach causal metadata
- expose deterministic conflict resolution
- allow application-level merge hooks

### 7. Query and Indexing

Support these query tiers:
- point lookups by primary key
- range scans by sort key
- equality lookups by secondary index
- limited prefix / partial materialized views

Indexes should be:
- explicit
- asynchronously maintained
- rebuildable

### 8. Transactions and Integrity

Do not promise arbitrary cross-key serializability everywhere.
Instead provide:
- conditional writes
- compare-and-swap
- atomic batch within a partition when possible
- optional consensus-backed transactions for small critical scopes

### 9. Repair and Reconciliation

Use anti-entropy repair:
- Merkle-tree or hash-range based comparison
- incremental repair windows
- background repair scheduling

Support:
- hinted delivery for temporarily unreachable peers
- read repair for stale replicas

### 10. Backup and Recovery

Provide:
- point-in-time recovery
- on-demand snapshots
- export/import
- cross-peer restore
- audit trail of restores

### 11. Security

Required:
- end-to-end encrypted connections
- authenticated peers
- signed operations
- authorization policies
- encrypted-at-rest local storage
- secure key rotation

Optional but important for community deployments:
- quorum-based trust domains
- admin role separation
- abuse reporting and quarantine

### 12. Observability

You will need:
- metrics
- tracing
- logs
- peer health inspection
- replication lag metrics
- repair status
- index health
- storage utilization
- built-in dashboards
- Grafana integration
- node inventory and topology views
- admin links that jump from the local UI into Grafana

Observability should be a first-class product surface, not an afterthought. The system should expose a local admin dashboard that can show:
- nodes and peer status
- live replication activity
- storage and memory pressure
- read and write rates
- repair progress
- conflict counts
- index freshness
- recent alerts and warnings

Grafana should be deployed alongside the database in containers so that:
- the base install remains reproducible
- dashboards are available in local dev and production-like environments
- operators can use standard Grafana dashboards without custom setup

Prefer a container stack where the database node, admin dashboard, and Grafana can be brought up together with one compose-like command.

### 13. Developer Experience

Include:
- SDKs
- CLI tools
- local dev mode
- deterministic test harnesses
- migration tooling
- schema inspection
- example applications

## Recommended Semantics

### Consistency

Default to eventual consistency with strong local writes.

Offer three modes:
- eventual
- causal
- scoped strong consistency for specific operations

### Availability

Writes should succeed locally whenever the node is healthy.
If a strict write cannot be committed safely, the system should fail clearly rather than silently degrade.

### Read Behavior

Reads should prefer local data when fresh enough, but be able to:
- merge replicas
- surface conflicts
- request repair

## Internal Subsystems

- peer discovery
- auth and identity
- op log
- snapshot manager
- replication scheduler
- conflict resolver
- indexer
- repair worker
- backup manager
- admin API
- telemetry pipeline
- dashboard service
- Grafana container
- metrics exporter
- container orchestration layer

## Suggested First Implementation

Build a narrow vertical slice:
- one database type
- one peer protocol
- one local engine
- one replicated document type
- one secondary index
- one repair workflow
- one metrics pipeline
- one local admin dashboard
- one Grafana deployment in containers

Then expand horizontally.

## References

- [Amazon Dynamo paper](https://web.stanford.edu/class/cs244/papers/amazon-dynamo-sosp2007.pdf)
- [DynamoDB core components](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.CoreComponents.html)
- [Apache Cassandra overview](https://cassandra.apache.org/doc/latest/cassandra/architecture/overview.html)
- [CRDTs: Consistency without concurrency control](https://arxiv.org/abs/0907.0929)
- [Byzantine Eventual Consistency and the Fundamental Limits of Peer-to-Peer Databases](https://arxiv.org/abs/2012.00472)
- [libp2p docs](https://docs.libp2p.io/)
- [IPFS how it works](https://docs.ipfs.tech/concepts/how-ipfs-works/)
- [CouchDB documentation](https://docs.couchdb.org/_/downloads/en/stable/pdf/)
