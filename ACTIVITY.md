# Agent Activity Log

## 2026-07-11

- Branch: `agent/local-restore-drill-2026-07-11`
- Task: Add a local restore drill release gate for implemented durable runtime state.
- Scope: Restore SQLite metadata, committed versions, delete markers, storage-agent manifests, and replica blobs into a fresh local runtime root; expose the drill through `Hedgehog.Xtask`; include it in `Hedgehog.LocalRuntime.Tests`; document current coverage and future outbox/reservation/repair restore gaps.
- Tests:
  - `dotnet build Hedgehog.sln`
  - `dotnet format Hedgehog.sln --verify-no-changes`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Hedgehog.Metadata.Core.Tests --no-build`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Hedgehog.Metadata.Sqlite.Tests --no-build`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Hedgehog.Admin.Api.Tests --no-build`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Hedgehog.LocalRuntime.Tests --no-build`
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/1
- Next candidate task: Extend restore coverage to reservations, outbox replay, and repair jobs after those workflows exist.
