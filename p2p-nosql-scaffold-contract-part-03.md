# P2P NoSQL Scaffold Contract Part 03

This file preserves ordered scaffold-contract content split from `p2p-nosql-scaffold-contract.md` so GitHub API publishing can avoid large single-file payload limits.

tests/Hedgehog.Types.Tests/StateLabelsTests.cs
```

Required assertions:
- every canonical label in this document appears exactly once per domain
- no uppercase conceptual labels appear in any emitted string field
- every fixture slug is URL/path safe: `^[a-z0-9][a-z0-9_]*$`
- duplicate wire labels across domains are accepted only when lookups include `LabelDomain`
- every label has a non-empty metric label and admin filter value

### `Hedgehog.Xtask` Seed Data

Before `Hedgehog.Types` exists, `Hedgehog.Xtask` should keep seed data in one module:

```text
tools/Hedgehog.Xtask/ScaffoldContract/Seed.cs
```

That module should contain:
- the canonical label registry in the same shape as `LabelSpec`
- the uppercase quarantine denylist
- the project ownership map
- allowed `Microsoft.Data.Sqlite` dependency owners
- named metadata workflows
- recovery gate names
- required first fixture IDs

The seed module is temporary. The validator should fail if both `Hedgehog.Types` metadata and the seed module exist but disagree, then later delete the seed module once the project API is stable.

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

This denylist is intentionally larger than the current canonical table. It blocks accidental imports from older conceptual state-machine prose while allowing .NET enum variants such as `UnderReplicated` only through parser-aware checks in `Hedgehog.Types`.

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
owner_project = "Hedgehog.Metadata.Sqlite"
owner_test = "tests/Hedgehog.Metadata.Sqlite.Tests/WorkflowsTests.cs"
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
- `owner_project` must be one project from the ownership map.
- `owner_test` must be a path that either exists or is expected to exist in the empty scaffold.
- `beta_blocker = true` is required for every first-wave fixture.
- `workflows` entries must come from the metadata workflow matrix when present.
- `recovery_gates` entries must come from the readiness gate table when present.
- `capacity_pressure` and `degraded_modes` must use labels from `Hedgehog.Types` or the seed registry.
- `labels` use `domain.wire_label` so duplicate words remain unambiguous.
- `validator_checks` lists the checks that would fail if the scenario were removed.

### Required First Manifest Entries

The initial `fixtures/scaffold/manifest.toml` must contain these scenario IDs:

| Scenario ID | Owner project | Category | Required coverage |
| --- | --- | --- | --- |
| `head_crash_after_one_fsynced_replica` | `Hedgehog.Metadata.Sqlite` | `partial_write` | `create_write_intent`, `complete_replica`, `reservation.expired` |
| `late_ack_after_reservation_expiry` | `Hedgehog.Metadata.Sqlite` | `partial_write` | `complete_replica`, `expire_reservation`, `replica.stale` |
| `late_ack_after_delete_epoch_bump` | `Hedgehog.Metadata.Sqlite` | `partial_write` | `complete_replica`, `delete_marker`, `reservation.failed_cleanup_required` |
| `revoked_node_final_result` | `Hedgehog.Metadata.Sqlite` | `security` | `revoke_actor_or_node`, `complete_replica`, `replica.suspect` |
| `interrupted_repair_conversion` | `Hedgehog.Repair` | `partial_write` | `lease_repair`, `cleanup_conversion`, `repair_job.retry_wait` |
| `metadata_pause_and_recover` | `Hedgehog.LocalCluster` | `recovery` | all recovery gates |
| `restore_with_outbox_lag` | `Hedgehog.LocalCluster` | `recovery` | `claim_outbox`, `evaluate_recovery_gate`, `outbox` |
| `temp_disk_full_during_upload` | `Hedgehog.Agent.Store` | `capacity` | `capacity_report`, `capacity_pressure.critical` |
| `repair_reserve_exhausted` | `Hedgehog.Repair` | `capacity` | `lease_repair`, `capacity_pressure.emergency` |
| `orphan_cleanup_under_critical_capacity` | `Hedgehog.Metadata.Sqlite` | `capacity` | `cleanup_conversion`, `capacity_pressure.critical` |
| `agent_sqlite_manifest_replay_after_crash` | `Hedgehog.Agent.Store` | `agent_store` | manifest reconciliation |
| `cancel_after_fsync_before_final_result` | `Hedgehog.StorageAgent` | `runtime` | `replica.streaming`, `replica.verifying` |
| `lock_held_across_await_check` | `Hedgehog.Head` | `runtime` | `runtime.guardrails` |
| `bounded_queue_overflow_under_repair_pressure` | `Hedgehog.Head` | `runtime` | `capacity_pressure.pressure`, `repair_job.running` |
| `clock_skew_for_leases_and_envelope_expiry` | `Hedgehog.Crypto` | `security` | `lease.expired`, envelope expiry |

### Minimal Parser Strategy

First validator implementation:
- parse TOML with `toml_edit` or `toml`
- parse MSBuild project files with an XML parser, not string matching
- parse .NET files with Roslyn only where language semantics matter, especially enum members and public metadata-sqlite functions
- parse SQL migration files initially as text plus bounded token rules, then add `sqlparser` if SQL checks become noisy
- parse dashboard JSON with `serde_json`
- scan Markdown only for the temporary seed-source comparison and uppercase quarantine, not as a long-term authority

The first version may source-scan metric labels and admin filters as strings. It should not source-scan `*.csproj`, fixture manifests, or JSON dashboards with ad hoc regex.

### Required Negative Tests
