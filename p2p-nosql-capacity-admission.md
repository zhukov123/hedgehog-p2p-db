# P2P NoSQL Capacity Admission and Repair Reserve

## Slice

This pass defines v1 capacity admission for the head-mediated encrypted object store.

Assumptions:
- PostgreSQL owns logical capacity accounting and reservations.
- Storage agents report physical capacity and enforce local hard admission before accepting bytes.
- Placement and repair must satisfy both metadata accounting and real disk headroom.
- Capacity is multidimensional, not a single global free-byte number.

## Core Rule

Admit writes only against effective durable free capacity, not raw reported free capacity.

The v1 admission path must pass:
- tenant quota check
- dataset quota check, if configured
- global repair reserve check
- eligible-node count check
- placement diversity check
- per-node effective free check
- latest capacity report freshness check
- local storage-agent admission check before bytes are accepted

## Capacity Buckets

Track these globally, per tenant, per dataset, and per node where applicable.

Common buckets:
- `physical_total_bytes`: agent-reported disk/storage pool size
- `physical_free_bytes`: agent-reported current free bytes
- `physical_usable_bytes`: total minus node-local hard reserves
- `logical_committed_bytes`: bytes for live object versions
- `replica_committed_bytes`: physical bytes for completed replicas
- `reserved_write_bytes`: accepted but incomplete write reservations
- `reserved_repair_bytes`: capacity held for repair and re-replication
- `reserved_temp_bytes`: upload staging, checksum, compaction, and multipart temp
- `reserved_gc_lag_bytes`: tombstones, orphan candidates, and delete-lag allowance
- `reserved_emergency_bytes`: cleanup-only buffer, never normal write capacity
- `overhead_bytes`: metadata, index, checksum, and encryption framing estimate
- `unhealthy_bytes`: bytes on suspect, degraded, or offline nodes
- `debt_bytes`: under-replicated bytes needed to restore policy

Tenant and dataset buckets:
- `quota_bytes`
- `hard_used_bytes`
- `soft_reserved_bytes`
- `pending_delete_bytes`
- `under_replicated_bytes`
- `repair_debt_bytes`

Node-specific buckets:
- `node_state`: `healthy | draining | degraded | full | offline`
- `capacity_epoch`
- `last_reported_at`
- `max_admit_bytes`
- `inflight_write_bytes`
- `inflight_repair_bytes`
- `local_temp_bytes`
- `local_orphan_bytes`

## Write Admission Formula

For a write of logical size `S`, replication factor `R`, encryption/framing overhead `O`, and placement safety multiplier `M`:

```text
needed_physical = ceil(S * R * O * M)
```

Recommended v1 values:

```text
O = 1.02 to 1.05
M = 1.05 to 1.15
```

Global effective free:

```text
effective_free =
  sum(healthy node physical_free_bytes)
  - reserved_write_bytes
  - reserved_repair_bytes
  - reserved_temp_bytes
  - reserved_gc_lag_bytes
  - reserved_emergency_bytes
  - placement_unavailable_bytes
```

Admit a write only if:

```text
effective_free >= needed_physical
tenant_available >= S
dataset_available >= S
eligible_nodes_count >= R
each selected node has local_effective_free >= per_replica_needed
```

Tenant available:

```text
tenant_available =
  tenant_quota_bytes
  - logical_committed_bytes
  - soft_reserved_write_bytes
  - repair_charge_bytes
```

Dataset available follows the same model when dataset quota exists. If no dataset quota exists, tenant quota governs.

Do not count free capacity on nodes that cannot satisfy placement constraints. A cluster can show substantial aggregate free space and still be unable to safely admit an RF=3 object if the free bytes are on the wrong nodes.

## Reserve Rules

### Repair Reserve

```text
repair_reserve = max(
  10% of healthy physical usable capacity,
  bytes_needed_to_restore_largest_single_node_loss,
  configured_min_repair_reserve_bytes
)
```

For small clusters, use a higher floor. If RF=3 and only 3-5 nodes exist, 10% is likely too low.

### Temp-File Reserve

```text
temp_reserve = max(
  2 * max_object_size * max_concurrent_uploads_per_node,
  5% of node physical usable capacity
)
```

If multipart upload exists, temp reserve may be based on part size rather than max object size.

### Tombstone and Orphan Reserve

```text
gc_lag_reserve = max(
  expected_delete_rate_bytes_per_day * tombstone_retention_days,
  2% to 5% of physical usable capacity
)
```

This reserve covers tombstoned bytes, orphan cleanup delay, and physical deletion lag.

### Emergency Cleanup Reserve

```text
emergency_reserve = max(
  5% of physical usable capacity,
  2 * max_object_size
)
```

Emergency reserve is not available to normal writes or normal repair. Only delete markers, GC bookkeeping, and cleanup moves may consume it. Repair may consume emergency reserve only when an object is below minimum survivability, for example RF target 3 with only 1 good replica remaining.

## Local Storage-Agent Admission

Before accepting bytes, each selected storage agent computes:

```text
local_effective_free =
  physical_free
  - local_temp_bytes
  - local_reserved_write_bytes
  - local_reserved_repair_bytes
  - local_orphan_bytes
  - emergency_reserve
```

Agent accepts a replica write only if:

```text
local_effective_free >= replica_size_with_overhead
node_capacity_epoch == placement_capacity_epoch
node_state == healthy
reservation_id is valid and unexpired
fencing_token matches current lease
```

Agent must reject if disk free percentage is below the local hard floor, even if PostgreSQL metadata is stale.

## Pressure States

Recommended v1 states:
- `normal`: writes and repair allowed
- `pressure`: new writes throttled, repair continues
- `critical`: reject new writes; only deletes, highest-risk repair, and GC
- `emergency`: reject everything except delete, GC, and emergency cleanup

Capacity pressure work order:

1. Admit delete markers and tombstones.
2. Run orphan cleanup and expired temp cleanup.
3. Run GC for tombstone-eligible objects.
4. Prioritize repair for objects below minimum durability.
5. Throttle or reject new writes.
6. Defer low-priority repair that would worsen hot-node pressure.

Repair priority increases with:

```text
priority =
  durability_deficit
  + node_failure_risk
  + tenant_priority
  + object_age_factor
  + placement_skew_penalty
  - capacity_pressure_penalty_on_target_nodes
```

## PostgreSQL Capacity Tables

Core tables:
- `capacity_nodes`
- `capacity_tenants`
- `capacity_datasets`
- `capacity_reservations`
- `capacity_reports`
- `placement_epochs`

Reservation fields should include:
- `reservation_id`
- `tenant_id`
- `dataset_id`
- `object_id`
- `version_id`
- `node_id`
- `placement_epoch`
- `reservation_kind`: `write | repair | temp | gc_lag | emergency`
- `bytes_reserved`
- `state`: `pending | reserved | streaming | finalizing | committed | expired | aborted | failed_cleanup_required`
- `expires_at`
- `idempotency_key`
- `created_at`
- `updated_at`

These reservation states must stay aligned with `p2p-nosql-scaffold-contract.md`,
`p2p-nosql-implementation-contract.md`, and the eventual `hedgehog-types`
label metadata. Older `released` or `converted_to_repair` terminology is
non-canonical for v1 implementation.

Required constraints/checks:
- reservation bytes must be positive
- reservation creation idempotency key is unique
- reservation references tenant, dataset, object/version or write intent, and placement epoch
- node reservations reference a specific `node_id`
- no write reservation can be created for a node state other than `healthy`
- reservation expiry is required
- committed replica consumes an existing valid reservation or is attached to a repair job
- tenant/dataset quota checks happen in the same transaction that creates write intent and reservations

Use transactional `SELECT ... FOR UPDATE` on tenant, dataset, and node capacity rows during admission.

Tenant invariant:

```text
reserved_write_bytes + logical_committed_bytes <= quota_bytes
```

Node invariant:

```text
reserved_write_bytes
+ reserved_repair_bytes
+ reserved_temp_bytes
+ reserved_gc_lag_bytes
+ reserved_emergency_bytes
<= physical_usable_bytes
```

Because physical free bytes are agent-reported and can change outside PostgreSQL, enforce the node check against the latest report plus a staleness bound:

```text
last_reported_at >= now() - report_staleness_limit
```

If the report is stale, the node is ineligible for new placement.

## Strong Warning

The naive trap is treating capacity as a global scalar.

Capacity is multidimensional:
- tenant quota
- dataset quota
- placement diversity
- node freshness
- repair debt
- temp amplification
- deletion lag
- emergency reserve

V1 should implement pessimistic reservations in PostgreSQL, local hard admission on agents, and simple explicit pressure states. Dynamic capacity markets, clever fair-share models, and optimization-heavy placement should wait.

## Research Incorporated

Severus reviewed the capacity admission model.

Accepted findings:
- PostgreSQL should own logical reservations.
- Storage agents are the physical disk admission gate.
- Raw free bytes are not safe for admission.
- Repair, temp files, tombstones/orphans, and emergency cleanup require separate reserves.
- Capacity pressure should favor deletes, cleanup, GC, and minimum-durability repair before new writes.
- Stale capacity reports make nodes ineligible for placement.

## Next Unresolved Portion

The security authority slice is captured in `p2p-nosql-security-authority.md`.

The next design slice should define observability and admin operations:
- metrics names aligned to object/version/replica states
- admin dashboard pages and actions
- audit query surfaces
- incident runbooks
- Grafana dashboards
- alert thresholds
- operator workflows for repair, revocation, capacity, and restore
