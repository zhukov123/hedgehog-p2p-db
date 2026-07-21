# Agent Activity Log

## 2026-07-21 - SQLite recovery gate evaluation

- Branch: `agent/sqlite-recovery-gate-2026-07-21`
- Task: implement the SQLite metadata `evaluate_recovery_gate` workflow with durable gate outcomes, fail-closed ready state, idempotent replay, audit evidence, and recovery evidence counts.
- Tests:
  - `dotnet run --project tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj`
  - `dotnet build Hedgehog.sln`
  - `dotnet run --project tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
  - `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill`
  - `dotnet format Hedgehog.sln --verify-no-changes`
  - `docker compose -f observability/docker-compose.yml config`
- Container coverage: Docker was present; compose syntax/config was validated. The product runtime distributed coverage came from the local multi-head/multi-storage-node smoke, stress, and restore drill harnesses because the checked-in compose stack currently covers observability services rather than the full Hedgehog runtime.
- Severus: direct configured-agent routing failed with `agent not found`, no visible Severus sessions were listed, and Boromir posted the sidecar request in Discord `#agentchat` message `1528992461299388530`. Severus replied in auto-thread `1528992461299388530`, with concrete review messages `1528992924690550816` and `1528997817660735508`; those replies focused on concurrent manifest-reconciliation side work rather than this SQLite recovery-gate branch, so no Severus code edits or SQLite-specific review were incorporated here.
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/31
- Next candidate task: wire persisted recovery-gate outcomes into the admin/recovery API surface or implement `revoke_actor_or_node` in the SQLite workflow store.

## 2026-07-13 - claim outbox workflow

- Branch: `agent/claim-outbox-2026-07-13`
- Task: implement the SQLite metadata `claim_outbox` workflow so delivery workers can lease available outbox rows without double-claiming active leases.
- Tests:
  - `dotnet run --project tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj`
  - `dotnet build Hedgehog.sln`
  - `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `dotnet run --project tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill`
  - `dotnet format Hedgehog.sln --verify-no-changes`
- Container coverage: blocked because `docker` is not installed in the VM; local multi-head/multi-storage-node smoke, stress, and restore drills were run instead.
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/12
- Next candidate task: implement another remaining SQLite workflow, likely `evaluate_recovery_gate` or `revoke_actor_or_node`.
