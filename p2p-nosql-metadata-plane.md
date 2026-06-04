# P2P NoSQL Metadata Plane Design Notes

## Slice

This pass refines the metadata plane only. It assumes the Rust-first architecture already chosen elsewhere: public head nodes route requests, storage agents sit behind outbound connections, clients encrypt payloads before upload, and storage nodes only keep ciphertext.

## Metadata Plane Job

The metadata plane is the source of truth for coordination decisions that cannot safely live only in head-node memory.

It owns:
- accounts and tenant boundaries
- dataset definitions and replication policy
- object version records
- object placement and replica health
- storage-node registration and identity state
- capacity reservations and watermarks
- leases for write, repair, and delete workflows
- repair job state
- audit records for administrative mutations

It does not own:
- plaintext user data
- raw user encryption keys
- object ciphertext bytes
- per-node local compaction internals
- Grafana time-series storage

## Recommended First Shape

Use a single strongly consistent metadata cluster for the first production slice.

Recommendation:
- `openraft` or `raft-engine`-backed Rust service for replicated state
- `redb` or RocksDB as the local durable state store
- `axum` for admin and internal APIs
- `tonic` or typed HTTP for head-node to metadata-plane calls
- every mutating metadata command is idempotent by `command_id`

The first version should not shard metadata. Sharding before the workflows are stable would multiply every design problem: repair, capacity accounting, leases, schema migration, audit, and backup.

## Topology

The metadata plane is a separate service behind the head tier.

Components:
- `head-node`: public ingress, auth, request validation, storage-agent routing
- `metadata-node`: member of the strongly consistent metadata cluster
- `storage-agent`: holds ciphertext and reports state through outbound control channels
- `client`: encrypts, uploads, fetches, decrypts

Head nodes may cache read-only metadata with short TTLs, but all placement decisions and all state transitions must be committed through the metadata cluster.

## State Model

### Account

Fields:
- `account_id`
- `created_at`
- `status`: `active | suspended | deleted`
- `quota_bytes`
- `default_replication_factor`
- `key_policy_id`

### Dataset

Fields:
- `dataset_id`
- `account_id`
- `name`
- `replication_factor`
- `placement_policy_id`
- `retention_policy_id`
- `created_at`
- `status`: `active | frozen | deleting | deleted`

### Storage Node

Fields:
- `node_id`
- `peer_id`
- `identity_pubkey`
- `trust_domain`
- `agent_version`
- `reserved_bytes`
- `used_bytes_reported`
- `used_bytes_committed`
- `free_bytes_effective`
- `health_state`: `joining | healthy | suspect | draining | revoked | lost`
- `last_heartbeat_at`
- `last_capacity_report_at`
- `connection_state`: `connected | disconnected`

`used_bytes_reported` comes from the agent. `used_bytes_committed` is the metadata plane's accounting view. Placement must use the conservative lower-capacity result after reconciling both.

### Object Record

Fields:
- `object_id`
- `dataset_id`
- `account_id`
- `version_id`
- `content_hash`
- `ciphertext_len`
- `encryption_metadata_ref`
- `created_at`
- `updated_at`
- `delete_marker`: boolean
- `replication_factor`
- `placement_epoch`

The metadata plane stores encryption metadata references and wrapped-key metadata if needed, but never raw plaintext keys.

### Replica Record

Fields:
- `object_id`
- `version_id`
- `node_id`
- `replica_state`: `reserved | pending_store | committed | suspect | repairing | deleting | deleted | failed`
- `lease_id`
- `reserved_bytes`
- `committed_at`
- `last_verified_at`
- `last_error`

The write path should only report success after the required number of replicas reach `committed`.

### Lease

Fields:
- `lease_id`
- `lease_type`: `write | repair | delete | drain`
- `resource_id`
- `holder_id`
- `expires_at`
- `fencing_token`
- `status`: `active | released | expired | revoked`

Every storage-side mutation carries the current fencing token. Storage agents reject stale tokens to prevent delayed control messages from reviving old writes or deletes.

### Repair Job

Fields:
- `repair_job_id`
- `scope`: `object | dataset | node | placement_range`
- `reason`: `node_lost | under_replicated | corrupt_replica | policy_change | audit`
- `state`: `pending | leasing | copying | verifying | complete | failed | paused`
- `source_node_id`
- `target_node_id`
- `object_id`
- `attempt`
- `next_retry_at`
- `last_error`

## Write Path

1. Client encrypts payload and computes `content_hash`.
2. Client asks a head node to create an object version.
3. Head node asks metadata plane for placement.
4. Metadata plane checks account quota, dataset policy, node health, and capacity watermarks.
5. Metadata plane creates object, replica, reservation, and write-lease records in one committed command.
6. Head node streams ciphertext to selected storage agents over existing outbound agent channels.
7. Each storage agent fsyncs or equivalent durable-writes the object, then ACKs with `content_hash`, `bytes_written`, and fencing token.
8. Head node submits replica ACKs to the metadata plane.
9. Metadata plane transitions ACKed replicas to `committed`.
10. Head node returns success only when the dataset's write quorum is satisfied.

For v1, use full replication factor as the write success threshold. A later version can add `min_write_replicas` separately from durability target if product requirements need lower-latency writes.

## Capacity Accounting

Metadata capacity must be pessimistic.

On placement reserve:
- increment account used/reserved bytes by `ciphertext_len * replication_factor`
- increment each selected node's reserved bytes by `ciphertext_len`
- reject if any target node would cross its hard watermark
- reject if the cluster repair reserve would fall below its minimum

On replica commit:
- move node bytes from reserved to committed
- keep account committed usage aligned with object live versions

On failed write:
- release uncommitted reservations after lease expiry or explicit abort
- mark partial stored replicas as `deleting`
- schedule cleanup commands, but do not rely on cleanup before freeing metadata reservation

## Consistency Rules

Required invariants:
- no object version is visible until its required replicas are committed
- no storage node receives a mutation without a metadata lease
- no stale lease can overwrite a newer replica state
- no write can be admitted past the hard capacity watermark
- no repair can reduce committed replica count below policy target
- deletes become metadata tombstones before storage cleanup begins

Read-only caches in head nodes are allowed only for:
- account status
- dataset config
- node routing hints
- committed placement records

Caches must not be used for:
- capacity admission
- lease creation
- write visibility
- repair ownership
- revocation checks after a token's max age

## Failure Handling

If a head node dies during upload:
- metadata write leases expire
- uncommitted reservations are released
- partial replicas are marked for cleanup if agents later report them

If a storage node ACKs after lease expiry:
- metadata plane rejects the ACK
- storage agent is instructed to delete the orphaned object version

If the metadata cluster loses quorum:
- reject new writes and admin mutations
- allow reads through known committed placement caches only if cache freshness is within policy
- keep storage agents connected and buffering telemetry

If a storage node is lost:
- mark node `suspect`, then `lost` after policy timeout
- mark its committed replicas `suspect`
- create repair jobs for objects below replication target

## Audit and Migration

Every mutating command records:
- `command_id`
- actor type and actor id
- source head node
- request timestamp
- committed log index or equivalent revision
- before/after summary for admin-visible objects

Schema migrations must be explicit metadata commands with a cluster-wide schema version. Storage agents should include their supported protocol and schema versions in every heartbeat so the metadata plane can block incompatible placement.

## Rust Crate Implications

The metadata slice should become its own crate boundary early:

- `p2pnosql-metadata-core`: state types, commands, events, invariants
- `p2pnosql-metadata-store`: redb/RocksDB adapter and snapshots
- `p2pnosql-metadata-raft`: consensus integration
- `p2pnosql-metadata-api`: typed internal/admin API handlers
- `p2pnosql-proto`: shared wire types for head, agent, and metadata services

Keep command validation in `metadata-core`, not in the HTTP layer, so tests can exercise metadata transitions without running services.

## Decisions Made In This Pass

