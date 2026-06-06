# P2P NoSQL Replication and Repair State Machine

## Slice

This pass defines the v1 replication and repair state machine for the Rust-first head-mediated encrypted object store.

Assumptions:
- PostgreSQL is the authoritative metadata plane.
- Storage agents are outbound-only blob holders and proof reporters.
- Clients encrypt whole objects before upload.
- Metadata owns object visibility, placement, repair ownership, fencing, and delete semantics.
- Storage agents never independently decide object liveness or repair ownership.

## Core Boundary

PostgreSQL is the sole authority for:
- object and version intent
- current object head pointer
- replica placement
- replica lifecycle
- repair scheduling
- fencing-token leases
- delete visibility
- tombstone retention and garbage collection eligibility

Storage agents own only:
- local ciphertext bytes
- local manifests
- local tombstones
- durable command journals
- final command results and anomaly reports

The most important invariant:

> A worker completion is valid only when `version_id`, `fencing_token`, `placement_epoch`, and `delete_epoch` all match the metadata snapshot for that work.

That check should be implemented as a single transactional `UPDATE ... WHERE ... RETURNING` or equivalent guarded mutation, not as app-side precheck logic.

## Object Version States

Object versions are immutable. The system mutates state and head pointers, not payload identity.

States:

- `INITIATED`: client reserved an object/version id; not readable.
- `COMMIT_PENDING`: metadata has expected digest, size, encryption envelope, and planned replicas; not readable.
- `AVAILABLE`: readable version; minimum replication policy is satisfied.
- `UNDER_REPLICATED`: readable, but below desired replica count; repair eligible.
- `QUARANTINED`: digest disagreement, suspected corruption, or metadata conflict; not served through normal reads.
- `DELETE_MARKER`: logical current-version delete; hides older versions from normal reads.
- `GC_ELIGIBLE`: retention has passed and no live references or active jobs remain.
- `PURGED`: terminal metadata state after blob delete jobs complete or are waived by policy.

Legal transitions:

- `INITIATED -> COMMIT_PENDING -> AVAILABLE`
- `AVAILABLE -> UNDER_REPLICATED -> AVAILABLE`
- `AVAILABLE -> QUARANTINED`
- `UNDER_REPLICATED -> QUARANTINED`
- `AVAILABLE -> DELETE_MARKER`
- `UNDER_REPLICATED -> DELETE_MARKER`
- `QUARANTINED -> DELETE_MARKER`
- `DELETE_MARKER -> GC_ELIGIBLE -> PURGED`

Rules:
- v1 writes create new versions.
- v1 deletes create delete markers with a new `version_id` and higher `delete_epoch`.
- v1 should avoid in-place overwrites of object contents.
- an object cannot become `AVAILABLE` unless verified healthy replicas meet minimum replica policy and placement constraints.

## Replica States

Replica rows are scoped by `(object_id, version_id, node_id, placement_epoch)`.

States:

- `PLANNED`: placement selected in PostgreSQL; no blob expected yet.
- `TRANSFER_ASSIGNED`: a specific worker or agent lease owns the upload/copy attempt and has a fencing token.
- `UPLOADING`: ciphertext transfer is in progress.
- `VERIFYING`: agent claims bytes exist; verifier checks digest, size, and manifest.
- `HEALTHY`: counts toward replication.
- `STALE`: belongs to an old placement epoch, old node assignment, or superseded policy.
- `SUSPECT`: missed heartbeat, failed audit, or transient read failure.
- `CORRUPT`: digest/proof mismatch; never serve.
- `DELETE_PENDING`: blob should be removed or made unreadable.
- `DELETED`: agent reported deletion or metadata accepted expiry waiver.

Legal transitions:

- `PLANNED -> TRANSFER_ASSIGNED -> UPLOADING -> VERIFYING -> HEALTHY`
- `HEALTHY -> SUSPECT -> HEALTHY`
- `HEALTHY -> SUSPECT -> CORRUPT`
- `HEALTHY -> SUSPECT -> DELETE_PENDING`
- `HEALTHY -> STALE -> DELETE_PENDING -> DELETED`
- `CORRUPT -> DELETE_PENDING -> DELETED`
- `PLANNED -> DELETE_PENDING`
- `TRANSFER_ASSIGNED -> DELETE_PENDING`
- `UPLOADING -> DELETE_PENDING`
- `VERIFYING -> DELETE_PENDING`

Rules:
- do not allow `CORRUPT -> HEALTHY`
- reset corrupt data only through a new replica row or explicit admin repair path
- do not serve from `CORRUPT`, `DELETE_PENDING`, `DELETED`, or a wrong delete epoch
- stale replicas may serve only if the object version is still live and read policy explicitly allows fallback

## Repair Job States

Repair jobs are deduped by `(object_id, version_id, repair_kind, placement_epoch, delete_epoch)`.

States:

- `QUEUED`
- `LEASED`
- `RUNNING`
- `VERIFYING`
- `COMPLETED`
- `RETRY_WAIT`
- `FAILED_FINAL`
- `CANCELED_SUPERSEDED`

Legal transitions:

- `QUEUED -> LEASED -> RUNNING -> VERIFYING -> COMPLETED`
- `LEASED -> RETRY_WAIT -> QUEUED`
- `RUNNING -> RETRY_WAIT -> QUEUED`
- `VERIFYING -> RETRY_WAIT -> QUEUED`
- `LEASED -> FAILED_FINAL`
- `RUNNING -> FAILED_FINAL`
- `VERIFYING -> FAILED_FINAL`
- `QUEUED -> CANCELED_SUPERSEDED`
- `LEASED -> CANCELED_SUPERSEDED`
- `RUNNING -> CANCELED_SUPERSEDED`
- `VERIFYING -> CANCELED_SUPERSEDED`

Priority order:

1. `QUARANTINED` metadata conflicts or digest disagreement.
2. Below minimum durability threshold, for example `healthy_count < min_replicas`.
3. Delete propagation for versions hidden by delete markers, especially under capacity pressure.
4. Placement-policy violations such as wrong region, node class, or capacity pool.
5. Desired-count top-up when readable but below target replication factor.
6. Audit refresh or proof aging.

Repair reads the authoritative version row, locks it, checks epochs, then leases work. It must not guess based on local agent state alone.

## Fencing, Epochs, and Idempotency

These concepts are separate and should not be collapsed.

`fencing_token`:
- monotonic token issued when a worker leases replica or repair work
- every completion callback must include it
- PostgreSQL accepts the callback only if the token still matches the current lease row
- prevents old workers from marking stale work healthy after timeout or reassignment

`placement_epoch`:
- monotonic per object version
- increments whenever placement policy for that version changes
- replica rows include the epoch
- a healthy replica from an old epoch may physically exist but does not satisfy current placement unless explicitly grandfathered

`delete_epoch`:
- monotonic per object key or version lineage
- delete marker gets the newest delete epoch
- any upload or repair with `work_delete_epoch < current_delete_epoch` is rejected or converted to cleanup

`idempotency_key`:
- client-supplied for write/delete requests and internally generated for repair attempts
- unique within operation scope, such as `(tenant_id, object_key, op_type, idempotency_key)`
- replays return the original object version, delete marker, or repair result

Completion validity rule:

```sql
UPDATE replicas
SET state = 'HEALTHY', verified_at = now()
WHERE version_id = $1
  AND node_id = $2
  AND fencing_token = $3
  AND placement_epoch = $4
  AND delete_epoch = $5
  AND state = 'VERIFYING'
RETURNING replica_id;
```

The exact schema can differ, but the mutation must be guarded transactionally.

## Tombstone Retention and Garbage Collection

Use delete markers, not immediate hard deletes.

Rules:
- normal delete creates `DELETE_MARKER` with a new `version_id` and `delete_epoch`
- normal reads return not found when the latest visible version is a delete marker
- versioned reads may access older versions until retention expires, if authorization allows
- tombstone retention must exceed replication lag, repair retry horizon, audit interval, client retry window, and clock-skew allowance
- do not purge a tombstone while any replica or repair job with an older or equal delete epoch can still report completion
- garbage collection proceeds through `GC_ELIGIBLE`, replica delete jobs, delete confirmation or expiry waiver, then `PURGED`
- capacity-pressure GC may delete old non-current blob replicas, but must keep enough metadata tombstone state to reject stale completions

V1 default:
- conservative tombstone retention of 7-30 days
- tenant or dataset override later

## SQL Constraints and Transactional Checks

Hard v1 constraints:
- unique object version: `(tenant_id, object_key, version_id)`
- one latest/head pointer per object key
- idempotency uniqueness: `(tenant_id, op_type, idempotency_key)`
- replica uniqueness: `(version_id, node_id, placement_epoch)`
- at most one active lease per replica or repair job row
- repair job dedupe on active jobs for `(version_id, repair_kind, placement_epoch, delete_epoch)`

Transactional checks:
- valid enum transitions enforced by guarded updates or a transition table checked in code and tests
- healthy replica count updated in the same transaction as replica state changes, or derived instead of cached
- completion callbacks update with predicates on `state`, `fencing_token`, `placement_epoch`, and `delete_epoch`
- row locks on object/version rows during write, delete, placement, and repair lease decisions

Use `SERIALIZABLE` where needed, but do not rely on isolation level alone. Hot state transitions should still use explicit row-level checks.

## Large Object Warning

Whole-object replication is acceptable for v1, but large objects need explicit size classes and transfer boundaries even if repair remains whole-object.

Without those boundaries:
- verification gets expensive
- retries monopolize head-node bandwidth
- temp-space accounting becomes fragile
- repair reserve can be consumed by a small number of large copies
- storage-agent worker pools can starve smaller urgent repairs

V1 should define a maximum object size and transfer class policy before implementation.

## Research Incorporated

Severus reviewed the v1 replication and repair boundaries.

Accepted findings:
- PostgreSQL is the sole authority for object/version intent, placement, lifecycle, repair, fencing, and delete visibility.
- Storage agents are blob holders and proof reporters only.
- Object versions are immutable; writes create versions and deletes create markers.
- Fencing tokens, placement epochs, delete epochs, and idempotency keys serve different roles and all must be present in mutating callbacks.
- Tombstones are correctness state, not just cleanup hints.
- Every mutating callback must be transactionally guarded in PostgreSQL.
- The naive failure mode is accepting stale worker completions and accidentally resurrecting deleted data or counting invalid replicas as durable.

## Next Unresolved Portion

The PostgreSQL schema and migration slice is captured in `p2p-nosql-postgresql-schema-plan.md`.

The capacity admission and repair-reserve slice is captured in `p2p-nosql-capacity-admission.md`.

The next research slice should define security roots and protocol authority:
- admin authority model
- invitation issuance and revocation
- signed envelope canonicalization
- head-node authority limits
- storage-agent key rotation
- metadata privacy controls
- audit trails and incident response
