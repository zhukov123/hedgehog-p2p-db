# Comparison: DynamoDB vs Cassandra vs CouchDB vs a New P2P NoSQL System

## Status

Historical context only.

The accepted v1-alpha product is not a DynamoDB-like NoSQL system. It is a head-mediated, client-encrypted P2P object store with SQLite metadata authority. Database API ideas from this comparison are deferred and tracked in [p2p-object-store-deferred-design.md](p2p-object-store-deferred-design.md).

## Short Verdict

If you want a service people can use immediately, DynamoDB is the benchmark for managed simplicity.
If you want a proven decentralized-ish distributed database architecture, Cassandra is the closest operational model.
If you want offline-friendly bidirectional replication with document semantics, CouchDB is the clearest reference.
If you want a true peer-to-peer community database, you will need to combine pieces of all three and add a real P2P networking layer.

## DynamoDB

Strengths:
- extremely polished API
- managed operations
- strong ecosystem
- predictable primary-key access
- global tables and backup/restore features

Weaknesses for your goal:
- not peer-to-peer
- cloud-managed, not community-operated
- the user does not own the replication topology
- less suitable as a teaching reference for distributed systems internals

Best lesson to borrow:
- the data model simplicity
- the narrow request path
- the operational polish

## Cassandra

Strengths:
- decentralized architecture
- gossip-based membership
- hinted handoff
- anti-entropy repair
- scalable on commodity nodes

Weaknesses for your goal:
- still usually run as a cluster, not a real open peer mesh
- operational complexity is high
- query model is more rigid than many users expect

Best lesson to borrow:
- the replica repair story
- the ring and token distribution model
- the operational model for always-available writes

## CouchDB

Strengths:
- true peer-based replication
- offline-friendly
- incremental replication
- deterministic conflict handling
- document-centric mental model

Weaknesses for your goal:
- not a DynamoDB clone
- secondary indexing and query patterns are different
- not ideal if you want a primary-key-first API with range queries as the main primitive

Best lesson to borrow:
- the replication model
- the offline workflow
- the non-destructive conflict semantics

## What Your System Should Borrow

From DynamoDB:
- simple API
- clear key model
- backup and restore expectations

From Cassandra:
- gossip membership
- hinted handoff
- repair
- token-based distribution

From CouchDB:
- bidirectional sync
- conflict visibility
- offline-first behavior
- incremental replication checkpoints

From CRDT systems:
- convergence without lockstep coordination
- mergeable data types
- eventual consistency that is mathematically defensible

From libp2p/IPFS:
- peer discovery
- encrypted connectivity
- NAT traversal
- content-addressed or hash-verifiable transfer primitives

From Grafana-style operations:
- first-class metrics and dashboards
- standard visualization stack
- containerized observability
- linkable admin surfaces for operators

## Biggest Architectural Difference

The biggest difference between your target system and DynamoDB is this:

- DynamoDB is a managed service with one operator.
- Your system must survive with many operators, many peers, and no central authority.

That changes everything:
- identity
- trust
- upgrades
- repair
- abuse handling
- membership
- governance

## Risks

- sybil attacks if anyone can join freely
- data divergence if merge semantics are weak
- index lag if indexes are too ambitious
- operator confusion if the tooling is poor
- storage blow-up if repair and retention are not carefully designed

## Recommendation

If the goal is a real buildable system, aim for:
- a DynamoDB-like API on the outside
- a CouchDB/CRDT-like sync core inside
- Cassandra-like repair and membership concepts
- libp2p as the transport layer

That combination is the most plausible path to something that can be used by communities and universities without becoming a research-only toy.

## References

- [Amazon DynamoDB core components](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.CoreComponents.html)
- [DynamoDB global tables design](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/bp-global-table-design.html)
- [Apache Cassandra overview](https://cassandra.apache.org/doc/latest/cassandra/architecture/overview.html)
- [Cassandra Dynamo-style architecture](https://cassandra.apache.org/doc/latest/cassandra/architecture/dynamo.html)
- [Apache CouchDB docs](https://docs.couchdb.org/_/downloads/en/stable/pdf/)
- [CRDTs paper](https://arxiv.org/abs/0907.0929)
- [Byzantine Eventual Consistency paper](https://arxiv.org/abs/2012.00472)
- [libp2p docs](https://docs.libp2p.io/)
- [IPFS how it works](https://docs.ipfs.tech/concepts/how-ipfs-works/)
