# P2P NoSQL Scaffold Contract Part 04

This file preserves ordered scaffold-contract content split from `p2p-nosql-scaffold-contract.md` so GitHub API publishing can avoid large single-file payload limits.


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
