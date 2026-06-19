# Hedgehog P2P Object Store

Hedgehog is a head-mediated, client-encrypted peer-to-peer object store.

The accepted v1-alpha architecture is:
- SQLite metadata authority
- public head nodes for routing and coordination
- storage agents on participant machines
- client-side whole-object encryption
- opaque object IDs plus HMAC lookup hashes, with no required plaintext filenames in metadata
- whole-object replication
- head-mediated repair
- explicit capacity admission, leases, fencing, revocation, audit, and observability

Start here:
- [Canonical architecture](p2p-nosql-architecture.md)
- [Human guide with diagrams](p2p-object-store-guide.md)
- [Key model](p2p-object-store-key-model.md)
- [Implementation contract](p2p-nosql-implementation-contract.md)
- [Implementation roadmap](p2p-nosql-implementation-roadmap.md)
- [SQLite-first SQL schema plan](p2p-object-store-sqlite-schema-plan.md)
- [Deferred v2/v3 design](p2p-object-store-deferred-design.md)

Historical NoSQL/database-oriented documents are retained for context only. They are not v1-alpha implementation authority unless a newer contract explicitly adopts a feature.

## First Scaffold

The repository now includes the first Rust workspace scaffold:
- `hedgehog-types`
- `hedgehog-crypto`
- `hedgehog-metadata-core`
- `hedgehog-metadata-sql`
- `xtask`

Expected validation commands once Rust/Cargo is installed:

```text
cargo test
cargo run -p xtask -- validate-scaffold-contract
```
