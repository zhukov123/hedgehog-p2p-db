# Agent Activity Log

## 2026-07-22 - runtime status path redaction

- Branch: `agent/redact-runtime-paths-2026-07-22`
- Task: stop `/runtime/status` from exposing runtime filesystem roots or SQLite database paths while preserving operator-visible tenants, heads, storage-node capacity, and sanitized persistence status.
- Tests:
  - `dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `dotnet build Hedgehog.sln`
  - `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `dotnet format Hedgehog.sln --verify-no-changes`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill`
  - `dotnet run --project tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
- Container coverage: Docker is installed, but this API contract change is covered by the in-process API tests and local multi-head/three-storage-node smoke, stress, and restore drills; no compose-specific behavior was changed.
- Severus: direct `sessions_send agentId: severus` failed with `agent not found`; no visible Severus session was listed. Shared Discord handoff sent in `#agentchat`, message/thread `1529657340096417813`; no actionable reply was available before commit.
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/39
- Next candidate task: wire admin recovery gates to the canonical readiness evaluator or implement the remaining recovery-gate SQLite workflow evidence.

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
