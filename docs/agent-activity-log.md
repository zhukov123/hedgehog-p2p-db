# Agent Activity Log

## 2026-07-20 - revoke actor/node workflow with security outbox

- Branch: `agent/revoke-actor-node-2026-07-20`
- Task: implement the SQLite `revoke_actor_or_node` workflow from latest `main`, including actor revocation, node revocation, node-key revocation, revoked-node replica demotion, active reservation aborts, audit records, and durable security outbox events claimable by workers.
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
- Container coverage: `docker compose -f observability/docker-compose.yml config` passed. `docker compose -f observability/docker-compose.yml up -d` was blocked because Docker Desktop's Linux engine was not running (`dockerDesktopLinuxEngine` pipe missing).
- Severus: direct configured-agent route failed with `agent not found: severus`; no visible Severus session was available. Handoff sent in Discord `#agentchat`, message/thread `1528630188584341586`; no reply arrived in that thread during the run. Prior Severus guidance from 2026-07-19 recommended adding a durable outbox event to revocation, which this branch includes.
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/23
- Next candidate task: implement `accept_invite` binding checks so invitation encrypted payload/target kind constrains the actor or node that can be created, or consolidate/supersede older duplicate revocation and recovery-gate PRs.

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
