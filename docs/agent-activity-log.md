# Agent Activity Log

## 2026-07-19 - recovery gate workflow and readiness evaluator

- Branch: `agent/recovery-gate-workflow-2026-07-19`
- Task: add durable SQLite recovery gate state and wire local runtime readiness to one fail-closed evaluator rendered through health endpoints and Prometheus metrics.
- Changes:
  - Added migration `0007_recovery_gates.sql` plus `EvaluateRecoveryGateAsync` request/model/store coverage with durable recovery gate outbox events.
  - Added local readiness gates for schema parity, metadata integrity, outbox reconciliation, audit appendability, storage consistency, and emergency capacity pressure.
  - Updated `/health/ready`, `/health/cluster`, and `/metrics` to share the evaluator result instead of computing separate readiness answers.
- Tests:
  - `dotnet build Hedgehog.sln`
  - `dotnet run --project tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill`
  - `dotnet format Hedgehog.sln --verify-no-changes`
- Container coverage: `docker compose -f observability/docker-compose.yml config` passed; no full multi-container database stack exists on current `main`, so runtime coverage used local multi-head/multi-storage-node smoke, stress, and restore drills.
- Severus: direct `sessions_send` failed with `agent not found: severus`; Discord handoff sent in `#agentchat` message `1528509292259442869`, auto-thread `1528509292259442869`. Severus emphasized one centrally computed fail-closed readiness answer, no dashboard-derived readiness, no second authority, and no production bypass.
- PR: pending.
- Next candidate task: add durable outbox insertion/assertions to the strongest actor/node revocation workflow before merging a revocation PR.

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
