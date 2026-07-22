# Agent Activity Log

## 2026-07-22 - admin canonical recovery readiness

- Branch: `agent/admin-recovery-readiness-2026-07-22`
- Task: wire `Hedgehog.Admin.Api` recovery gates to the shared canonical recovery readiness evaluator used by local runtime health and metrics for issue #29.
- Changes:
  - Moved recovery readiness contracts and fail-closed evaluator into `Hedgehog.Metadata.Core`.
  - Kept `Hedgehog.LocalRuntime.Api` as a runtime-specific readiness probe.
  - Added an admin readiness probe and changed `GET /admin/v1/recovery/gates` to return the `recovery-readiness.v1` payload.
  - Updated the Admin UI recovery table for the canonical `{ ready, gates }` payload and documented the contract in `docs/admin-interface-v1.md`.
- Tests:
  - `dotnet run --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `dotnet build Hedgehog.sln`
  - `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `dotnet format Hedgehog.sln --verify-no-changes`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
- Container coverage: blocked because Docker Desktop's Linux engine was not running (`open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified`); local multi-head, three-storage-node smoke coverage passed instead.
- Severus: direct `sessions_send agentId: severus` failed with `agent not found`; visible session search returned no Severus session; fallback Discord handoff was attempted in `#agentchat`, with visible auto-thread `1529354905964249128` and message `1529354905964249128`. No current-run concrete Severus review was available before PR prep.
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/35
- Next candidate task: make one currently `unknown` recovery gate actionable, starting with `manifest_reconciliation` or `cache_rebuild`, so recovery readiness can eventually reach `ready`.

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
