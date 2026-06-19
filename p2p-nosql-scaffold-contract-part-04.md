# P2P NoSQL Scaffold Contract Part 04

This file preserves ordered scaffold-contract content split from `p2p-nosql-scaffold-contract.md` so GitHub API publishing can avoid large single-file payload limits.


`Hedgehog.Xtask` must include test fixtures that intentionally fail each first-scope check:

| Check | Negative fixture |
| --- | --- |
| `labels.canonical` | fixture manifest label `replica.REPAIRING` or `replica.repairing` |
| `labels.uppercase_quarantine` | dashboard variable containing `UNDER_REPLICATED` |
| `deps.direction` | `Hedgehog.Head` depending directly on `Hedgehog.Agent.Store` |
| `metadata.workflows` | public metadata-sqlite mutation function without a matrix workflow name |
| `metadata.sql_scope` | `Hedgehog.Head` depending on `Microsoft.Data.Sqlite` outside tests |
| `fixtures.present` | missing `late_ack_after_delete_epoch_bump` entry |
| `pressure.policy` | pressure tests missing `emergency` |
| `recovery.gates` | readiness schema missing `audit continuity` |
| `runtime.guardrails` | service code starting unsupervised background work outside the supervised task wrapper |

Passing the empty scaffold without these failure tests is not enough. The validator must prove it can catch the drift it claims to prevent.

## Next Decision

Create the first implementation scaffold in this order:

```text
tools/Hedgehog.Xtask/ScaffoldContract/Seed.cs
fixtures/scaffold/manifest.toml
src/Hedgehog.Types/Labels.cs
tests/Hedgehog.Types.Tests/StateLabelsTests.cs
dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract
```

The next unresolved implementation decision is whether `Hedgehog.Types` starts as a pure .NET static registry or reads generated metadata from JSON/TOML at build time. Prefer pure .NET first: it keeps labels type-checked, makes docs and manifests consumers, and avoids a build-step authority before the project boundary is stable.

After the validator and fixture manifest exist, the next design document needed is a short `p2p-nosql-project-layout.md` with the actual solution projects, MSBuild properties, owner project public APIs, and first CI commands.
