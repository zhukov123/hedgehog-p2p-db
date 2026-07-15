# Agent Activity Log

## 2026-07-15 - live demo endpoint and traffic runner

- Branch: `agent/demo-traffic-endpoint-2026-07-15`
- Task: add the issue #11 hosted-demo foundation inside `Hedgehog.LocalRuntime.Api`: live `/runtime/demo` status, manual `/runtime/demo/traffic/run-once`, background generated traffic, Prometheus demo counters, tests, and README runbook.
- Severus: direct configured-agent routing failed (`agent not found: severus`); no visible Severus session was listed. Discord handoff posted in `#agentchat` as message `1526868375664463884`; auto-thread `1526868375664463884` included Severus' recommendation to make the next bounded PR an `AcknowledgeOutboxAsync` workflow with stale-claim rejection and two-connection SQLite tests.
- Tests:
  - `dotnet test tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj --no-restore`
  - `dotnet build Hedgehog.sln`
  - `dotnet run --project tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `dotnet format Hedgehog.sln --verify-no-changes`
- Container coverage: local runtime API was started with generated traffic and `/runtime/demo` plus `/metrics` were verified against two heads and three storage nodes. `docker --version` succeeded, but `docker compose -f observability/docker-compose.yml up -d` was blocked because Docker Desktop's Linux daemon pipe was not reachable.
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/17
- Next candidate task: implement Severus' recommended outbox delivery ACK workflow, or deploy this API endpoint continuously once hosting credentials or a Boromir/OpenClaw VM target are available.

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
