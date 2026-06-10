# Peer-to-Peer NoSQL MVP

## Status

Historical context only.

The accepted v1-alpha product is a head-mediated, client-encrypted P2P object store with SQLite metadata authority. Local-first database behavior, JSON document storage, CRDT fields, and secondary indexes are deferred and tracked in [p2p-object-store-deferred-design.md](p2p-object-store-deferred-design.md).

## MVP Goal

Ship a small but real peer-to-peer database that can be used in classrooms, clubs, labs, and community projects without pretending to be DynamoDB on day one.

The MVP should prove:
- peers can discover and connect securely
- data can be written locally while offline
- changes replicate automatically
- conflicts converge predictably
- operators can inspect and repair the system

## Must-Have Features

### Data

- primary-key document store
- optional sort key
- JSON payloads
- durable local writes
- tombstones for deletes

### Replication

- peer-to-peer sync
- incremental change propagation
- reconnect after disconnect
- background catch-up

### Conflict Handling

- deterministic last-writer or causal merge for simple fields
- CRDT support for counters, sets, and append-only logs
- explicit conflict exposure for unmergeable documents

### Query

- get by primary key
- range query by sort key
- one secondary index type

### Ops

- node health endpoint
- replication status
- repair command
- snapshot and restore command
- log files and metrics
- local admin dashboard
- node inventory view
- Grafana link-outs
- containerized Grafana deployment

### Security

- node identity keys
- encrypted transport
- authenticated peer joining
- basic authorization rules

## What to Defer

Do not include these in v1:
- arbitrary ad hoc queries
- full SQL
- distributed joins
- multi-document serializable transactions
- rich full-text search
- graph traversals
- cross-region billing and tenancy controls

## Minimal Product Surface

### Client API

Keep the first API small:
- `put`
- `get`
- `delete`
- `queryByPartition`
- `watch`

### Admin API

Add a tiny admin surface:
- `status`
- `peers`
- `repair`
- `snapshot`
- `restore`
- `metrics`

### SDK Surface

Support at least:
- TypeScript/JavaScript
- Rust
- Python

## MVP Milestones

### Milestone 1

- embedded local database
- append-only change log
- basic schema
- persistence tests

### Milestone 2

- peer discovery
- secure connection setup
- one-way replication
- reconnect and resume

### Milestone 3

- bidirectional sync
- deterministic conflict resolution
- basic CRDT types
- repair tooling

### Milestone 4

- secondary index
- snapshots
- restore
- observability
- CLI
- admin dashboard
- Grafana container stack
- standard dashboard templates

### Milestone 5

- documentation
- demo app
- classroom deployment guide
- community deployment guide
- observability guide
- dashboard customization guide
- operations runbook

## Evaluation Criteria

The MVP is good enough if it can:
- survive a node going offline for hours
- synchronize cleanly after reconnect
- handle concurrent edits without corrupting data
- let a new peer join and catch up
- be deployed by someone who is not the author

## Teaching Value

For university use, the system should visibly demonstrate:
- CAP tradeoffs
- eventual consistency
- CRDT convergence
- anti-entropy repair
- secure peer discovery
- operational debugging

## References

- [Dynamo paper](https://web.stanford.edu/class/cs244/papers/amazon-dynamo-sosp2007.pdf)
- [Apache Cassandra hints](https://cassandra.apache.org/doc/stable/cassandra/managing/operating/hints.html)
- [Apache Cassandra repair](https://cassandra.apache.org/doc/stable/cassandra/managing/operating/repair.html)
- [CRDTs paper](https://arxiv.org/abs/0907.0929)
- [libp2p docs](https://docs.libp2p.io/)
