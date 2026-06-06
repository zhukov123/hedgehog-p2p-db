# P2P NoSQL Rust Scaffold Contract

## Purpose

This is the implementation-facing contract for the first Rust scaffold.

Use it as the source of truth for crate ownership, dependency direction, state labels, SQL labels, metrics, admin filters, workflow fixtures, and recovery gates. Broader architecture notes remain background context; the first crates should validate against this file and `p2p-nosql-implementation-contract.md`.

## Source Priority

1. `p2p-nosql-scaffold-contract.md`
2. `p2p-nosql-implementation-contract.md`
3. `p2p-nosql-postgresql-schema-plan.md`
4. `p2p-nosql-capacity-admission.md`
5. `p2p-nosql-degraded-mode-cache-policy.md`
6. `p2p-nosql-security-authority.md`
7. Older architecture and MVP documents

If an older document uses conflicting implementation labels, crate names, or workflow names, this contract wins.

## Crate Ownership Map

| Concept | Owner crate | Allowed dependents | Forbidden dependents | Golden source | First fixtures |
| --- | --- | --- | --- | --- | --- |
| Stable IDs, state labels, reason codes, transfer classes | `hedgehog-types` | all crates | none | generated Rust enums plus SQL label tests | `crates/hedgehog-types/tests/state_labels.rs` |
| Signed envelopes and canonical bytes | `hedgehog-crypto` | head, metadata-pg, admin, CLI | storage-agent business logic deciding authority | deterministic CBOR vectors | `crates/hedgehog-crypto/tests/envelope_vectors.rs` |
| Semantic state transitions | `hedgehog-metadata-core` | metadata-pg, repair tests, local-cluster tests | head raw SQL, agent raw SQL, admin raw SQL | pure Rust transition tables | `crates/hedgehog-metadata-core/tests/transitions.rs` |
| PostgreSQL durable workflows | `hedgehog-metadata-pg` | head, repair, admin, CLI, local-cluster | direct SQL mutation from service crates | migrations plus workflow matrix below | `crates/hedgehog-metadata-pg/tests/workflows.rs` |
| Storage-agent local manifest and journal | `hedgehog-agent-store` | storage-agent, local-cluster tests | metadata-pg, head | `redb` schema plus crash fixtures | `crates/hedgehog-agent-store/tests/crash_reconcile.rs` |
| Storage-agent service protocol | `hedgehog-storage-agent` | local-cluster | metadata-pg direct authority | protobuf or RPC schema and command journal | `crates/hedgehog-storage-agent/tests/protocol_idempotency.rs` |
| Public head service | `hedgehog-head` | local-cluster | storage-agent deciding metadata authority | service workflow wrappers | `crates/hedgehog-head/tests/degraded_cache.rs` |
| Repair scheduler | `hedgehog-repair` | local-cluster | direct replica mutation without metadata-pg workflow | repair lease workflows | `crates/hedgehog-repair/tests/pressure_ordering.rs` |
| Admin and observability labels | `hedgehog-admin`, `hedgehog-observability` | local-cluster | defining new state names | `hedgehog-types` display mapping | `crates/hedgehog-admin/tests/filter_labels.rs` |
| Generated local cluster | `hedgehog-local-cluster` | CI | production-only hidden behavior | Compose and fixture manifests | `crates/hedgehog-local-cluster/tests/chaos.rs` |

## Canonical Labels

All Rust enums, SQL accepted values, metric labels, admin filters, and test fixture names must use lowercase labels from `p2p-nosql-implementation-contract.md`.

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
cargo xtask validate-scaffold-contract
```

The command should fail if any SQL migration, metric, admin filter, or fixture uses non-canonical implementation labels.

## PostgreSQL Workflow Matrix

Every metadata mutation must be a named `hedgehog-metadata-pg` workflow. Service crates must not call generic update APIs.

| Workflow | Lock order | Isolation target | Idempotency scope | Audit timing | Outbox timing | Invariant checks |
| --- | --- | --- | --- | --- | --- | --- |
| `create_write_intent` | tenant, dataset, object key, capacity rows, selected nodes | `READ COMMITTED` plus guarded updates; escalate on conflicts | `(tenant_id, object_key, write, key)` | append in transaction after decision | append reservation event in same transaction | quota, pressure, placement, reservation |
| `complete_replica` | version, replica row, reservation, node | guarded `UPDATE ... WHERE fencing_token AND placement_epoch AND delete_epoch` | `(replica_id, final_result_id)` | append accepted or stale completion | append verify or cleanup command | fencing, delete epoch, capacity |
| `commit_version` | object key, version, replicas, reservation | guarded transaction; retry serialization/deadlock | `(tenant_id, object_key, commit, key)` | append before commit | append visibility event in same transaction | min replicas, digest, placement |
| `delete_marker` | object key, previous head, new delete version, replicas | guarded head-pointer update | `(tenant_id, object_key, delete, key)` | append delete decision | append delete propagation | delete epoch, retention |
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

```rust
enum AuthorityCacheDecision<T> {
    Fresh(T),
    Deny(DenyReason),
    Unavailable(UnavailableReason),
}
```

Mutation workflows must call `hedgehog-metadata-pg` directly and cannot accept `Fresh<T>` as authority. Raw cached tenant, dataset, revocation, placement, invitation, capacity, or visibility records are allowed only in read-only status rendering modules.

## Recovery Readiness Gate

The cluster is not `normal` until one operator-visible gate says all checks passed:

| Gate | Must prove |
| --- | --- |
| migrations | expected migration version is installed |
| metadata invariants | core invariant suite passes against PostgreSQL |
| audit continuity | hash chain or checkpoint continuity is valid |
| outbox | lag below threshold and expired claims reconciled |
| cache rebuild | authority caches rebuilt from PostgreSQL revisions |
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
- admin readiness checks

Final ACK, revocation, lease, outbox, and readiness traffic must not share an unbounded queue with upload or repair streams.

## First Crash And Chaos Fixtures

These fixtures are beta blockers for service glue:

- head crash after one fsynced replica
- late ACK after reservation expiry
- late ACK after delete epoch bump
- revoked-node final result
- interrupted repair conversion
- PostgreSQL pause and recover
- restore with outbox lag
- temp disk full during upload
- repair reserve exhausted
- orphan cleanup under critical capacity
- `redb` manifest replay after crash
- task cancellation after fsync and before final result publication
- lock-held-across-await check in service code
- bounded queue overflow under repair pressure
- test-controlled clock skew for leases and envelope expiry

## Scaffold Validation Task

The first implementation task is `cargo xtask validate-scaffold-contract`. It should run before any service crate is accepted and should be cheap enough for every local build, CI job, and pre-PR check.

### Ownership

The validation task belongs to `xtask` but must treat `hedgehog-types` as the source for executable labels once the workspace exists. Until generated Rust enums exist, the labels in this contract are the seed fixture.

The task should fail closed: missing files, unreadable manifests, unknown workflow names, missing fixture names, or parse failures are validation failures. A skipped check is allowed only behind an explicit `--allow-missing-scaffold` flag for the very first empty workspace bootstrap.

### Inputs

The task reads:

- root `Cargo.toml`
- `crates/*/Cargo.toml`
- `migrations/**/*.sql`
- `crates/**/tests/**/*`
- `crates/**/src/**/*`
- `dashboards/**/*`
- `admin/**/*`
- `fixtures/**/*`
- `p2p-nosql-scaffold-contract.md`
- `p2p-nosql-implementation-contract.md`

Implementation should parse TOML, SQL migrations, and Rust code with real parsers where practical. Plain text scanning is acceptable only for metric label strings, fixture names, dashboard JSON, and the temporary pre-scaffold markdown seed.

### Checks

| Check | Failure condition | First implementation approach |
| --- | --- | --- |
| `labels.canonical` | Rust enums, SQL accepted values, metric labels, admin filters, dashboard variables, or fixture names use implementation-state labels outside the canonical lowercase set | Seed a canonical-label table in `xtask`, then replace it with `hedgehog-types` generated metadata |
| `labels.uppercase_quarantine` | Uppercase pre-contract states from `p2p-nosql-replication-repair-state-machine.md` appear in code, migrations, metrics, admin filters, dashboards, or fixtures | Maintain a denylist from the old state-machine document and report the exact file and token |
| `deps.direction` | A crate imports a forbidden owner or service crate bypasses the owner crate named in the ownership map | Parse crate manifests and direct dependencies; later add `cargo metadata` package graph checks |
| `metadata.workflows` | Metadata mutation APIs are exposed without one of the named workflow identifiers | Require public metadata-pg mutation modules or functions to carry a workflow name from the matrix |
| `metadata.sql_scope` | Service crates contain raw SQL mutation strings or depend on `sqlx` without being `hedgehog-metadata-pg`, migrator, or test-only harness | Scan dependencies first, then scan source for `query!`, `query_as!`, `UPDATE`, `INSERT`, and `DELETE` markers outside allowed crates |
| `fixtures.present` | Any first crash or chaos fixture is missing from `fixtures/` or the named crate test path | Require one manifest entry per fixture with owner crate, scenario name, and beta-blocker flag |
| `cache.api` | Authority-sensitive code exposes raw cached authority records to mutation workflows | Require cache decision helpers to return `AuthorityCacheDecision<T>` and forbid raw cache modules in head mutation paths |
| `pressure.policy` | Repair, cleanup, and write admission tests do not include every capacity pressure label | Require test or fixture names for `normal`, `pressure`, `critical`, and `emergency` in the pressure-ordering owner |
| `recovery.gates` | Readiness output lacks one of the named recovery gates | Require admin/status schema or fixture labels for every gate in the readiness table |
| `runtime.guardrails` | Service crates use unbounded channels, blocking disk APIs on async paths, or task spawns without supervision markers | Start as a source scan with allowlisted wrappers; graduate to lints once wrappers exist |

### Output Contract

The validator should print grouped failures with stable check IDs, for example:

```text
labels.uppercase_quarantine: crates/hedgehog-repair/src/state.rs used REPAIRING
metadata.sql_scope: crates/hedgehog-head/src/write.rs contains UPDATE outside hedgehog-metadata-pg
fixtures.present: missing beta fixture "late ACK after delete epoch bump"
```

CI should treat any failure as blocking. The local command should also support `--json` for editor integration and future admin-dashboard display of scaffold readiness.

### Bootstrap Sequence

1. Add `xtask` with hardcoded contract seed data and parser tests.
2. Add empty crate manifests for the owner crates in the ownership map.
3. Add fixture manifest stubs under `fixtures/scaffold/`.
4. Make `cargo xtask validate-scaffold-contract` pass on the empty scaffold.
5. Add `hedgehog-types` canonical label metadata and switch the validator away from markdown-derived labels.
6. Add CI so service crates cannot land without the validator.

The key constraint is ordering: the validator may start with hardcoded seed data, but service crates must not start with hardcoded labels. Once `hedgehog-types` exists, labels flow from it into SQL tests, metrics labels, admin filters, dashboard variables, and fixture names.

## Validator Seed And Fixture Manifest Contract

This slice defines the first machine-readable contract that `cargo xtask validate-scaffold-contract` consumes. It deliberately keeps v1 small enough to implement before service crates exist while still proving pass and fail behavior.

### `hedgehog-types` Label Metadata

`hedgehog-types` owns a static label registry once the crate exists. Until then, `xtask` may carry the same data as seed TOML or Rust constants, but the shape should already match the future API.

Recommended public shape:

```rust
pub enum LabelDomain {
    Object,
    ObjectVersion,
    Replica,
    Lease,
    RepairJob,
    Reservation,
    CapacityPressure,
    DegradedMode,
    Node,
    Invitation,
    AuditDecision,
}

pub struct LabelSpec {
    pub domain: LabelDomain,
    pub wire: &'static str,
    pub rust_variant: &'static str,
    pub sql_value: &'static str,
    pub metric_label: &'static str,
    pub admin_filter: &'static str,
    pub fixture_slug: &'static str,
    pub display: &'static str,
}

pub fn label_specs() -> &'static [LabelSpec];
pub fn labels_for(domain: LabelDomain) -> &'static [LabelSpec];
pub fn lookup_label(domain: LabelDomain, wire: &str) -> Option<&'static LabelSpec>;
```

Rules:
- `wire`, `sql_value`, `metric_label`, `admin_filter`, and `fixture_slug` are lowercase stable strings.
- `rust_variant` is the only PascalCase field and is never emitted to SQL, metrics, dashboards, logs, or fixture names.
- `display` is presentation-only and must not be parsed back into workflow code.
- Domains that reuse words, such as `normal` in capacity pressure and degraded mode, must be validated with domain context.
- Adding, renaming, or removing a label requires updating `hedgehog-types` tests, SQL accepted-value tests, fixture manifest coverage, and admin or dashboard filter tests in the same change.

The first `hedgehog-types` tests:

```text
crates/hedgehog-types/tests/state_labels.rs
```

Required assertions:
- every canonical label in this document appears exactly once per domain
- no uppercase conceptual labels appear in any emitted string field
- every fixture slug is URL/path safe: `^[a-z0-9][a-z0-9_]*$`
- duplicate wire labels across domains are accepted only when lookups include `LabelDomain`
- every label has a non-empty metric label and admin filter value

### `xtask` Seed Data

Before `hedgehog-types` exists, `xtask` should keep seed data in one module:

```text
xtask/src/scaffold_contract/seed.rs
```

That module should contain:
- the canonical label registry in the same shape as `LabelSpec`
- the uppercase quarantine denylist
- the crate ownership map
- allowed `sqlx` dependency owners
- named metadata workflows
- recovery gate names
- required first fixture IDs

The seed module is temporary. The validator should fail if both `hedgehog-types` metadata and the seed module exist but disagree, then later delete the seed module once the crate API is stable.

### Uppercase Quarantine Denylist

The first validator should reject these tokens in code, migrations, fixture names, dashboards, admin filters, metrics, and tests:

```text
WRITING
COMMITTED
UNDER_REPLICATED
QUARANTINED
DELETE_MARKER
GC_ELIGIBLE
GARBAGE_COLLECTED
PLANNED
STREAMING
VERIFYING
HEALTHY
SUSPECT
CORRUPT
STALE
DELETE_PENDING
DELETED
REPAIRING
DONE
FAILED
```

This denylist is intentionally larger than the current canonical table. It blocks accidental imports from older conceptual state-machine prose while allowing Rust enum variants such as `UnderReplicated` only through parser-aware checks in `hedgehog-types`.

### Fixture Manifest Path

The first fixture manifest lives at:

```text
fixtures/scaffold/manifest.toml
```

It is a contract file, not a generated test report. Every beta-blocking crash or chaos scenario in this scaffold contract must have exactly one manifest entry.

Recommended schema:

```toml
version = 1

[[scenario]]
id = "late_ack_after_delete_epoch_bump"
title = "late ACK after delete epoch bump"
category = "partial_write"
owner_crate = "hedgehog-metadata-pg"
owner_test = "crates/hedgehog-metadata-pg/tests/workflows.rs"
beta_blocker = true
workflows = ["complete_replica", "delete_marker", "cleanup_conversion"]
recovery_gates = ["reservation reconciliation", "repair deficit"]
capacity_pressure = ["critical"]
degraded_modes = ["recovering"]
labels = [
  "object_version.delete_marker",
  "replica.stale",
  "reservation.expired",
  "reservation.failed_cleanup_required"
]
validator_checks = ["fixtures.present", "labels.canonical", "metadata.workflows"]
```

Field rules:
- `id` is the stable fixture slug and must use `^[a-z0-9][a-z0-9_]*$`.
- `title` must match one human-readable fixture name from this document.
- `category` is one of `partial_write`, `recovery`, `capacity`, `agent_store`, `runtime`, `security`, or `observability`.
- `owner_crate` must be one crate from the ownership map.
- `owner_test` must be a path that either exists or is expected to exist in the empty scaffold.
- `beta_blocker = true` is required for every first-wave fixture.
- `workflows` entries must come from the PostgreSQL workflow matrix when present.
- `recovery_gates` entries must come from the readiness gate table when present.
- `capacity_pressure` and `degraded_modes` must use labels from `hedgehog-types` or the seed registry.
- `labels` use `domain.wire_label` so duplicate words remain unambiguous.
- `validator_checks` lists the checks that would fail if the scenario were removed.

### Required First Manifest Entries

The initial `fixtures/scaffold/manifest.toml` must contain these scenario IDs:

| Scenario ID | Owner crate | Category | Required coverage |
| --- | --- | --- | --- |
| `head_crash_after_one_fsynced_replica` | `hedgehog-metadata-pg` | `partial_write` | `create_write_intent`, `complete_replica`, `reservation.expired` |
| `late_ack_after_reservation_expiry` | `hedgehog-metadata-pg` | `partial_write` | `complete_replica`, `expire_reservation`, `replica.stale` |
| `late_ack_after_delete_epoch_bump` | `hedgehog-metadata-pg` | `partial_write` | `complete_replica`, `delete_marker`, `reservation.failed_cleanup_required` |
| `revoked_node_final_result` | `hedgehog-metadata-pg` | `security` | `revoke_actor_or_node`, `complete_replica`, `replica.suspect` |
| `interrupted_repair_conversion` | `hedgehog-repair` | `partial_write` | `lease_repair`, `cleanup_conversion`, `repair_job.retry_wait` |
| `postgres_pause_and_recover` | `hedgehog-local-cluster` | `recovery` | all recovery gates |
| `restore_with_outbox_lag` | `hedgehog-local-cluster` | `recovery` | `claim_outbox`, `evaluate_recovery_gate`, `outbox` |
| `temp_disk_full_during_upload` | `hedgehog-agent-store` | `capacity` | `capacity_report`, `capacity_pressure.critical` |
| `repair_reserve_exhausted` | `hedgehog-repair` | `capacity` | `lease_repair`, `capacity_pressure.emergency` |
| `orphan_cleanup_under_critical_capacity` | `hedgehog-metadata-pg` | `capacity` | `cleanup_conversion`, `capacity_pressure.critical` |
| `redb_manifest_replay_after_crash` | `hedgehog-agent-store` | `agent_store` | manifest reconciliation |
| `cancel_after_fsync_before_final_result` | `hedgehog-storage-agent` | `runtime` | `replica.streaming`, `replica.verifying` |
| `lock_held_across_await_check` | `hedgehog-head` | `runtime` | `runtime.guardrails` |
| `bounded_queue_overflow_under_repair_pressure` | `hedgehog-head` | `runtime` | `capacity_pressure.pressure`, `repair_job.running` |
| `clock_skew_for_leases_and_envelope_expiry` | `hedgehog-crypto` | `security` | `lease.expired`, envelope expiry |

### Minimal Parser Strategy

First validator implementation:
- parse TOML with `toml_edit` or `toml`
- parse Cargo manifests with the same TOML parser, not string matching
- parse Rust files with `syn` only where Rust semantics matter, especially enum variants and public metadata-pg functions
- parse SQL migration files initially as text plus bounded token rules, then add `sqlparser` if SQL checks become noisy
- parse dashboard JSON with `serde_json`
- scan Markdown only for the temporary seed-source comparison and uppercase quarantine, not as a long-term authority

The first version may source-scan metric labels and admin filters as strings. It should not source-scan `Cargo.toml`, fixture manifests, or JSON dashboards with ad hoc regex.

### Required Negative Tests

`xtask` must include test fixtures that intentionally fail each first-scope check:

| Check | Negative fixture |
| --- | --- |
| `labels.canonical` | fixture manifest label `replica.REPAIRING` or `replica.repairing` |
| `labels.uppercase_quarantine` | dashboard variable containing `UNDER_REPLICATED` |
| `deps.direction` | `hedgehog-head` depending directly on `hedgehog-agent-store` |
| `metadata.workflows` | public metadata-pg mutation function without a matrix workflow name |
| `metadata.sql_scope` | `hedgehog-head` depending on `sqlx` outside tests |
| `fixtures.present` | missing `late_ack_after_delete_epoch_bump` entry |
| `pressure.policy` | pressure tests missing `emergency` |
| `recovery.gates` | readiness schema missing `audit continuity` |
| `runtime.guardrails` | service code using `tokio::spawn` outside the supervised task wrapper |

Passing the empty scaffold without these failure tests is not enough. The validator must prove it can catch the drift it claims to prevent.

## Next Decision

Create the first implementation scaffold in this order:

```text
xtask/src/scaffold_contract/seed.rs
fixtures/scaffold/manifest.toml
crates/hedgehog-types/src/labels.rs
crates/hedgehog-types/tests/state_labels.rs
cargo xtask validate-scaffold-contract
```

The next unresolved implementation decision is whether `hedgehog-types` starts as a pure Rust static registry or reads generated metadata from TOML at build time. Prefer pure Rust first: it keeps labels type-checked, makes docs and manifests consumers, and avoids a build-script authority before the crate boundary is stable.

After the validator and fixture manifest exist, the next design document needed is a short `p2p-nosql-crate-layout.md` with the actual workspace `Cargo.toml`, feature flags, owner crate public APIs, and first CI commands.
