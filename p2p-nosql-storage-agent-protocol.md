# P2P NoSQL Storage Agent Protocol Design Notes

## Slice

This pass refines the storage agent protocol only. It builds on the metadata-plane pass: metadata owns placement, reservations, leases, repair jobs, and capacity accounting; storage agents sit behind outbound connections and store only ciphertext.

## Protocol Job

The storage agent protocol is the control and data path between public head nodes and untrusted storage nodes.

It must provide:
- authenticated agent registration
- outbound-only long-lived control sessions
- typed store, fetch, delete, verify, and repair commands
- durable ACK semantics
- fencing-token enforcement for every mutation
- capacity and health telemetry
- resumable reconnect behavior
- orphan detection and cleanup

It must not provide:
- plaintext access
- raw client key exchange
- independent placement decisions by agents
- unauthenticated peer discovery
- agent-initiated object visibility changes

## Transport Choice

Use one outbound connection from each storage agent to the head tier.

Recommended v1:
- `tonic` bidirectional streaming gRPC over `rustls`
- signed protocol envelopes on top of mTLS or noise-style peer identity
- one logical `AgentControlStream` per connected head node
- separate bounded data streams for large object transfer if needed

Rationale:
- storage nodes do not need inbound firewall holes
- bidirectional streaming handles commands and ACKs naturally
- typed protobuf schemas are easier to evolve than ad hoc JSON
- Rust server/client support is mature

Keep libp2p as a later option for decentralized peer-to-peer repair. The first production slice is safer if agents connect to known head nodes and the metadata plane remains authoritative.

## Identity and Registration

Every storage agent has a persistent identity:
- `node_id`: metadata-plane assigned stable id
- `agent_instance_id`: per-install generated UUID
- `identity_pubkey`: long-lived Ed25519 public key
- `identity_key_id`: key version
- `peer_id`: derived transport identity if libp2p is later enabled

Registration flow:
1. Operator creates an invitation through admin API.
2. Invitation contains `cluster_id`, `join_token_id`, expiry, allowed trust domain, and head bootstrap URLs.
3. Agent generates identity keypair locally.
4. Agent opens outbound `RegisterAgent` request with invitation proof and public key.
5. Head node validates invitation with metadata plane.
6. Metadata plane creates `StorageNode` in `joining` state.
7. Agent receives `node_id`, accepted protocol versions, telemetry interval, and capacity limits.
8. Agent persists registration before accepting store commands.

Registration request:
- `cluster_id`
- `join_token_id`
- `agent_instance_id`
- `identity_pubkey`
- `identity_key_id`
- `agent_version`
- `supported_protocol_versions`
- `supported_schema_versions`
- `os`
- `arch`
- `trust_domain_claim`
- `reserved_bytes_requested`
- `nonce`
- `signature`

Registration response:
- `node_id`
- `cluster_id`
- `accepted_protocol_version`
- `accepted_schema_version`
- `trust_domain`
- `heartbeat_interval_ms`
- `capacity_report_interval_ms`
- `max_inflight_store_commands`
- `max_inflight_repair_commands`
- `head_endpoints`
- `metadata_revision`

Decision made: invitations authorize registration, but the agent identity key becomes the durable node identity after registration.

## Envelope

All protocol messages use a signed envelope.

Fields:
- `message_id`
- `correlation_id`
- `cluster_id`
- `node_id`
- `source`: `head | agent`
- `destination`
- `protocol_version`
- `metadata_revision_seen`
- `sent_at_hlc`
- `expires_at`
- `payload_type`
- `payload_hash`
- `signature_key_id`
- `signature`

Rules:
- `message_id` is globally unique and idempotent.
- `correlation_id` links commands, progress events, ACKs, and final outcomes.
- `expires_at` bounds delayed control messages.
- `payload_hash` covers the encoded payload, not the full envelope.
- agents reject unsigned, expired, wrong-cluster, wrong-node, or downgrade-version messages.

## Control Stream

After registration, the agent opens `AgentControlStream`.

Agent-to-head messages:
- `Hello`
- `Heartbeat`
- `CapacityReport`
- `CommandAck`
- `CommandProgress`
- `CommandFailed`
- `FetchDataChunk`
- `RepairDataChunk`
- `LocalAnomalyReport`

Head-to-agent messages:
- `HelloAck`
- `StoreObject`
- `FetchObject`
- `DeleteObject`
- `VerifyObject`
- `CopyReplica`
- `AbortCommand`
- `UpdateConfig`
- `DrainNode`
- `RevokeNode`

Control stream invariants:
- head commands are idempotent by `command_id`
- agent persists command state before mutating disk
- all mutations carry metadata lease id and fencing token
- the agent reports final state exactly once per command attempt, then may repeat the final result on retry

## Heartbeat and Capacity Report

Heartbeat fields:
- `node_id`
- `agent_version`
- `protocol_version`
- `schema_version`
- `started_at`
- `last_command_id_applied`
- `active_command_count`
- `disk_state`: `ok | read_only | degraded | full | unavailable`
- `connection_state`
- `observed_clock_offset_ms`

Capacity report fields:
- `reserved_bytes_configured`
- `bytes_used_on_disk`
- `bytes_committed_known`
- `bytes_reserved_local`
- `bytes_orphaned`
- `bytes_tombstoned`
- `bytes_temp`
- `free_bytes_physical`
- `watermark_state`: `normal | warning | soft_backpressure | hard_reject`
- `object_count`
- `orphan_count`
- `last_full_scan_at`

Capacity reports are advisory. Metadata-plane committed and reserved accounting remains authoritative for placement.

## Store Command

`StoreObject` fields:
- `command_id`
- `lease_id`
- `fencing_token`
- `object_id`
- `version_id`
- `dataset_id`
- `account_id`
- `content_hash`
- `ciphertext_len`
- `encryption_metadata_ref`
- `placement_epoch`
- `expected_replica_state`: `reserved | pending_store`
- `write_mode`: `new_replica | repair_copy | hinted_replay`
- `chunk_plan`

Store rules:
- agent rejects the command if the lease is missing, expired, stale, or below local recorded fencing token
- agent checks local reservation before reading the payload
- agent writes to a temp file first
- agent verifies byte count and hash before commit
- agent fsyncs object file and manifest before ACK
- final ACK means bytes are durable locally, not globally visible

ACK fields:
- `command_id`
- `lease_id`
- `fencing_token`
- `object_id`
- `version_id`
- `bytes_written`
- `content_hash`
- `local_manifest_id`
- `durability`: `fsynced | best_effort`
- `completed_at`

For v1, only `fsynced` ACKs count toward metadata commit. `best_effort` is visible as an error-like diagnostic, not a successful replica.

## Fetch Command

`FetchObject` fields:
- `command_id`
- `object_id`
- `version_id`
- `range`
- `content_hash`
- `max_chunk_bytes`
- `requester_context`

Fetch rules:
- fetch is read-only and does not require a write lease
- agent only serves committed or repair-approved local replicas
- agent verifies the local manifest before streaming unless metadata explicitly allows opportunistic repair reads
- hash mismatch fails closed and emits `LocalAnomalyReport`

Fetch failure reasons:
- `not_found`
- `not_committed`
- `hash_mismatch`
- `disk_unavailable`
- `rate_limited`
- `protocol_error`

## Delete Command

`DeleteObject` fields:
- `command_id`
- `lease_id`
- `fencing_token`
- `object_id`
- `version_id`
- `delete_epoch`
- `delete_mode`: `metadata_tombstone | orphan_cleanup | forced_revoke_cleanup`

Delete rules:
- logical deletion is committed in metadata before storage cleanup starts
- agent records a local tombstone before unlinking bytes
- stale delete commands are rejected by fencing token
- physical deletion may be retried indefinitely
- delete ACK only means the agent has made the object unreadable locally

V1 keeps local tombstones until metadata repair confirms the delete epoch is past the configured retention window.

## Verify and Repair Commands

`VerifyObject` checks local bytes and manifest:
- validates manifest checksum
- validates ciphertext length
- validates content hash
- reports `verified | missing | corrupt | unreadable | tombstoned`

`CopyReplica` asks a target agent to receive bytes from a source selected by the head node:
- metadata creates repair lease
- head node coordinates source fetch and target store
- target agent enforces fencing token as a normal store
- source agent treats the copy as a read-only fetch

Decision made: v1 repair data transfer is head-mediated. Direct agent-to-agent repair can be added later after identity, authorization, and observability are proven.

## Local Disk Layout

Recommended layout under the configured data root:

- `identity/agent-key`
- `identity/registration.json`
- `objects/{dataset_id}/{object_id}/{version_id}.blob`
- `objects/{dataset_id}/{object_id}/{version_id}.manifest`
- `tmp/{command_id}.part`
- `tombstones/{dataset_id}/{object_id}/{version_id}.json`
- `commands/{command_id}.json`
- `orphans/{scan_id}.jsonl`
- `logs/agent.log`

Manifest fields:
- `object_id`
- `version_id`
- `dataset_id`
- `content_hash`
- `ciphertext_len`
- `placement_epoch`
- `highest_fencing_token_seen`
- `replica_state_local`
- `created_at`
- `committed_at_local`
- `last_verified_at`

The manifest is the local source of truth for fencing-token rejection after restarts.

## Reconnect Behavior

On reconnect, the agent sends:
- registration identity proof
- last applied command ids
- active commands
- final results not yet acknowledged by a head node
- latest capacity report
- local anomalies since last connection

The head node reconciles with metadata and may resend commands. Agents must return the already-computed final result for duplicate `command_id` instead of re-running side effects.

If metadata says a replica is not valid but the agent has bytes:
- agent marks it orphaned
- agent makes it unreadable for normal fetches
- cleanup waits for an explicit `DeleteObject` with `orphan_cleanup`

If metadata says a replica exists but the agent is missing bytes:
- agent reports `LocalAnomalyReport`
- metadata marks replica `suspect`
- repair scheduler creates a repair job

## Error Semantics

Error responses include:
- `command_id`
- `error_code`
- `retry_class`: `retryable | retry_after | permanent | security`
- `retry_after_ms`
- `local_state_summary`
- `message`

Canonical error codes:
- `stale_fencing_token`
- `lease_expired`
- `insufficient_local_capacity`
- `disk_full`
- `disk_read_only`
- `hash_mismatch`
- `object_not_found`
- `object_not_committed`
- `identity_rejected`
- `protocol_version_unsupported`
- `command_expired`
- `rate_limited`
- `internal_error`

Security-class errors should trigger admin-visible audit events and may move the node to `suspect`.

## Observability Hooks

Metrics emitted by every agent:
- `p2p_agent_control_stream_connected`
- `p2p_agent_command_duration_seconds`
- `p2p_agent_command_failures_total`
- `p2p_agent_store_bytes_total`
- `p2p_agent_fetch_bytes_total`
- `p2p_agent_local_capacity_bytes`
- `p2p_agent_orphan_bytes`
- `p2p_agent_fencing_rejections_total`
- `p2p_agent_hash_mismatch_total`

Structured log fields:
- `node_id`
- `command_id`
- `lease_id`
- `fencing_token`
- `object_id`
- `version_id`
- `operation`
- `result`
- `error_code`

## Rust Crate Implications

The storage protocol should be separated from agent implementation:

- `p2pnosql-agent-proto`: protobuf definitions and generated Rust types
- `p2pnosql-agent-client`: head-side command sender and stream manager
- `p2pnosql-agent-core`: command state machine, validation, fencing, and idempotency
- `p2pnosql-agent-store`: local disk layout, manifests, temp files, and tombstones
- `p2pnosql-agent-service`: runnable storage-agent binary

Keep fencing and idempotency in `agent-core`, not inside tonic handlers, so protocol behavior can be tested without network services.

## Decisions Made In This Pass

- Use outbound bidirectional gRPC streams for v1 storage-agent control.
- Use invitation-based registration, then persistent agent identity keys.
- Require signed envelopes for all protocol messages.
- Require metadata lease id and fencing token for every storage mutation.
- Count only fsynced store ACKs as successful replicas.
- Make repair head-mediated in v1 rather than direct agent-to-agent.
- Treat metadata as authoritative; unexpected local bytes become unreadable orphans.

## Next Unresolved Portion

The next design slice should define replication and repair in concrete state-machine terms:
- object and replica state transitions from write through repair
- repair scheduler inputs and priority order
- hash-range or manifest-based anti-entropy format
- retry, pause, and backoff rules
- source selection for repair copies
- tombstone retention and garbage collection
- admin-visible repair progress schema
