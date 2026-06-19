# P2P NoSQL Scaffold Contract Part 01

This file preserves ordered scaffold-contract content split from `p2p-nosql-scaffold-contract.md` so GitHub API publishing can avoid large single-file payload limits.

# P2P NoSQL .NET Scaffold Contract

## Purpose

This is the implementation-facing contract for the first .NET scaffold.

Use it as the source of truth for project ownership, dependency direction, state labels, SQL labels, metrics, admin filters, workflow fixtures, and recovery gates. Broader architecture notes remain background context; the first projects should validate against this file and `p2p-nosql-implementation-contract.md`.

## Source Priority

1. `p2p-nosql-scaffold-contract.md`
2. `p2p-nosql-implementation-contract.md`
3. `p2p-object-store-sqlite-schema-plan.md`
4. `p2p-nosql-capacity-admission.md`
5. `p2p-nosql-degraded-mode-cache-policy.md`
6. `p2p-nosql-security-authority.md`
7. Older architecture and MVP documents

If an older document uses conflicting implementation labels, project names, or workflow names, this contract wins.

## Project Ownership Map

| Concept | Owner project | Allowed dependents | Forbidden dependents | Golden source | First fixtures |
| --- | --- | --- | --- | --- | --- |
| Stable IDs, state labels, reason codes, transfer classes | `Hedgehog.Types` | all projects | none | .NET label registry plus SQL label tests | `tests/Hedgehog.Types.Tests/StateLabelsTests.cs` |
| Signed envelopes and canonical bytes | `Hedgehog.Crypto` | head, metadata-sqlite, admin, CLI | storage-agent business logic deciding authority | deterministic CBOR vectors | `tests/Hedgehog.Crypto.Tests/EnvelopeVectorsTests.cs` |
| Semantic state transitions | `Hedgehog.Metadata.Core` | metadata-sqlite, repair tests, local-cluster tests | head raw SQL, agent raw SQL, admin raw SQL | pure .NET transition tables | `tests/Hedgehog.Metadata.Core.Tests/TransitionsTests.cs` |
| SQLite-first durable workflows | `Hedgehog.Metadata.Sqlite` | head, repair, admin, CLI, local-cluster | direct SQL mutation from service projects | migrations plus workflow matrix below | `tests/Hedgehog.Metadata.Sqlite.Tests/WorkflowsTests.cs` |
| Storage-agent local manifest and journal | `Hedgehog.Agent.Store` | storage-agent, local-cluster tests | metadata-sqlite, head | embedded manifest schema plus crash fixtures | `tests/Hedgehog.Agent.Store.Tests/CrashReconcileTests.cs` |
| Storage-agent service protocol | `Hedgehog.StorageAgent` | local-cluster | metadata-sqlite direct authority | protobuf or RPC schema and command journal | `tests/Hedgehog.StorageAgent.Tests/ProtocolIdempotencyTests.cs` |
| Public head service | `Hedgehog.Head` | local-cluster | storage-agent deciding metadata authority | service workflow wrappers | `tests/Hedgehog.Head.Tests/DegradedCacheTests.cs` |
| Repair scheduler | `Hedgehog.Repair` | local-cluster | direct replica mutation without metadata-sqlite workflow | repair lease workflows | `tests/Hedgehog.Repair.Tests/PressureOrderingTests.cs` |
| Admin and observability labels | `Hedgehog.Admin`, `Hedgehog.Observability` | local-cluster | defining new state names | `Hedgehog.Types` display mapping | `tests/Hedgehog.Admin.Tests/FilterLabelsTests.cs` |
| Generated local cluster | `Hedgehog.LocalCluster` | CI | production-only hidden behavior | Compose and fixture manifests | `tests/Hedgehog.LocalCluster.Tests/ChaosTests.cs` |

## Canonical Labels

All .NET enums or label registries, SQL accepted values, metric labels, admin filters, and test fixture names must use lowercase labels from `p2p-nosql-implementation-contract.md`.

Do not import uppercase labels from `p2p-nosql-replication-repair-state-machine.md` into code, migrations, metrics, or fixtures. That document is useful for transition concepts only until it is rewritten to the canonical labels.

Required first labels:

| Domain | Canonical labels |
| --- | --- |
| Object version | `writing`, `committed`, `under_replicated`, `quarantined`, `delete_marker`, `gc_eligible`, `garbage_collected` |
| Replica | `planned`, `streaming`, `verifying`, `healthy`, `suspect`, `corrupt`, `stale`, `delete_pending`, `deleted` |
| Repair job | `pending`, `leased`, `running`, `verifying`, `completed`, `retry_wait`, `failed_final`, `canceled_superseded` |
| Reservation | `pending`, `reserved`, `streaming`, `finalizing`, `committed`, `expired`, `aborted`, `failed_cleanup_required` |
| Capacity pressure | `normal`, `pressure`, `critical`, `emergency` |
| Degraded mode | `normal`, `degraded_read_only`, `authority_stale`, `recovering` |

Validation command for the scaffold:

```text
dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract
```

The command should fail if any SQL migration, metric, admin filter, or fixture uses non-canonical implementation labels.

## SQL Workflow Matrix

Every metadata mutation must be a named `Hedgehog.Metadata.Sqlite` workflow. Service projects must not call generic update APIs.

| Workflow | Lock order | Isolation target | Idempotency scope | Audit timing | Outbox timing | Invariant checks |
| --- | --- | --- | --- | --- | --- | --- |
| `create_write_intent` | tenant, dataset, object id or lookup hash, capacity rows, selected nodes | SQLite transaction plus guarded updates; backend may strengthen isolation later | `(tenant_id, dataset_id, object_ref, write, key)` | append in transaction after decision | append reservation event in same transaction | quota, pressure, placement, reservation |
| `complete_replica` | version, replica row, reservation, node | guarded `UPDATE ... WHERE fencing_token AND placement_epoch AND delete_epoch` | `(replica_id, final_result_id)` | append accepted or stale completion | append verify or cleanup command | fencing, delete epoch, capacity |
| `commit_version` | object id, version, replicas, reservation | guarded transaction; retry serialization/deadlock | `(tenant_id, dataset_id, object_ref, commit, key)` | append before commit | append visibility event in same transaction | min replicas, digest, placement |
| `delete_marker` | object id, previous head, new delete version, replicas | guarded head-pointer update | `(tenant_id, dataset_id, object_ref, delete, key)` | append delete decision | append delete propagation | delete epoch, retention |
| `lease_repair` | version, replica set, repair job, capacity rows | guarded lease claim | `(repair_job_id, lease_attempt)` | append lease decision | append repair command | pressure, repair reserve, fencing |
| `expire_reservation` | reservation, version, replicas, capacity rows | guarded expiry transition | `(reservation_id, expiry_attempt)` | append expiry decision | append cleanup command | stale bytes classification |
| `cleanup_conversion` | reservation, replica, node, capacity rows | guarded conversion | `(reservation_id, node_id, cleanup_attempt)` | append cleanup classification | append delete/local cleanup | orphan and temp accounting |
| `capacity_report` | node, capacity epoch, pressure state | guarded epoch update | `(node_id, report_id)` | append anomaly when needed | append pressure transition when changed | stale/impossible report detection |
| `accept_invite` | invite, tenant, identity, role | single guarded transaction | `(invite_id, invite_accept)` | append accept or reject | no privileged outbox before commit | expiry, revocation, max uses |
| `revoke_actor_or_node` | actor or node, keys/sessions, replicas/jobs | guarded epoch bump | `(scope_id, revoke, key)` | append revocation in transaction | append cache invalidation and repair work | revocation epoch, placement |
| `claim_outbox` | outbox row only | guarded claim expiry | `(outbox_id, worker_id, claim)` | no new authority audit | update claim in transaction | claim expiry, worker identity |
| `append_audit_checkpoint` | audit sequence/checkpoint | serial sequence | `(checkpoint_period, key)` | checkpoint is the audit event | optional notification | hash continuity |
| `evaluate_recovery_gate` | migration marker, invariants, outbox, caches, agents, capacity | read-only snapshot plus explicit gate write | `(cluster_id, recovery_eval)` | append readiness decision | append readiness state change | all gates below |

Deadlocks, serialization failures, stale fencing, duplicate idempotency keys, and invariant failures must be separate error classes. Retrying a stale fencing failure is a bug unless a new lease was issued.

## Degraded Cache API Contract

Authority-sensitive cache lookups return only:

```csharp
public abstract record AuthorityCacheDecision<T>;
public sealed record Fresh<T>(T Value) : AuthorityCacheDecision<T>;
public sealed record Deny<T>(DenyReason Reason) : AuthorityCacheDecision<T>;
public sealed record Unavailable<T>(UnavailableReason Reason) : AuthorityCacheDecision<T>;
```

Mutation workflows must call `Hedgehog.Metadata.Sqlite` directly and cannot accept `Fresh<T>` as authority. Raw cached tenant, dataset, revocation, placement, invitation, capacity, or visibility records are allowed only in read-only status rendering modules.

## Recovery Readiness Gate

The cluster is not `normal` until one operator-visible gate says all checks passed:

| Gate | Must prove |
| --- | --- |
| migrations | expected migration version is installed |
| metadata invariants | core invariant suite passes against the SQLite metadata store |
| audit continuity | hash chain or checkpoint continuity is valid |
| outbox | lag below threshold and expired claims reconciled |
| cache rebuild | authority caches rebuilt from metadata revisions |
| manifest reconciliation | agents have classified local bytes and journals |
| reservation reconciliation | committed, reserved, temp, orphan, and cleanup bytes agree |
| repair deficit | below-minimum durability objects are known and queued |
| capacity reports | fresh reports exist for nodes eligible for admission |
| readiness publication | head/admin status exposes the exact failed gate |

## Pressure Ordering Policy

Capacity pressure is executable policy, not scheduler preference.

In `critical` or `emergency`, work is admitted in this order:

1. Delete markers and metadata needed to prevent stale resurrection.
2. Expired temp cleanup and orphan cleanup.
3. Tombstone-eligible GC that does not erase stale-completion rejection state.
4. Repair for objects below minimum survivability.
5. Repair for placement policy violations.
6. Desired replica top-up.
7. New writes.

Local storage-agent hard rejection always wins over stale metadata admission. Emergency reserve is available only to cleanup and minimum-survivability repair. Large-object uploads and repairs must be throttled before they can starve control traffic or consume the repair reserve.

## Head Control Capacity

Head nodes must have separate bounded queues and concurrency budgets for:

- client uploads
- repair streams
- control RPCs
- final ACK handling
- lease expiry and renewal
- revocation checks
- outbox publishing
