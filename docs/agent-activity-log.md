# Agent Activity Log

## 2026-07-22 - fresh-root restore drill

- Branch: `agent/fresh-root-restore-drill-2026-07-22`
- Task: harden the local restore drill so backup artifacts include storage-agent manifests and the drill restores into a fresh runtime root after removing the source runtime root.
- Tests:
  - `dotnet build Hedgehog.sln`
  - `dotnet run --project tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill`
  - `dotnet format Hedgehog.sln --verify-no-changes`
  - `docker compose -f observability/docker-compose.yml config`
- Container coverage: Docker is installed and the observability Compose config rendered successfully; the app runtime coverage is the local two-head / three-storage-node smoke, stress, and fresh-root restore drill.
- Severus: direct configured-agent routing failed with `agent not found: severus`; visible session search returned no Severus session. Discord fallback sent message `1529475761587884152`; Severus replied in `#agentchat` messages `1529478370557952052`, `1529478373091311810`, and `1529478374140018971`, flagging that existing recovery-gate PRs were duplicate/superseded and recommending fresh-root restore proof as the better next task.
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/36
- Next candidate task: wire an admin/readiness surface to the SQLite recovery gate source of truth after the recovery-gate workflow PR lands, or start storage-agent local SQLite manifest/journal work.

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
