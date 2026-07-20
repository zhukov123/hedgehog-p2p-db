# Agent Activity Log

## 2026-07-20 - live demo traffic runner

- Branch: `agent/demo-traffic-runner-2026-07-20`
- Task: add the first live local-runtime demo surface for issue #11: `/demo`, `/runtime/demo/status`, a manual `/runtime/demo/tick`, and an opt-in generated traffic runner that writes, reads through the opposite head order, deletes, and records counters/recent failures.
- Tests:
  - `dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj` passed after rerun with a longer timeout; first attempt hit the 120s command timeout without output.
  - `dotnet build Hedgehog.sln -c Release`
  - `dotnet run -c Release --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
  - `dotnet run -c Release --project tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj`
  - `dotnet run -c Release --project tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj`
  - `dotnet run -c Release --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill`
  - `dotnet format Hedgehog.sln --verify-no-changes`
- Debug build note: `dotnet build Hedgehog.sln` in Debug was blocked by existing `.NET Host` processes locking Admin API/UI DLLs, so Release output was used for the full build and tests.
- Container coverage: blocked because Docker Desktop's Linux engine pipe was unavailable (`dockerDesktopLinuxEngine` missing); local multi-head/multi-storage-node smoke, stress, API, and restore drills were run instead.
- Severus: direct `sessions_send agentId: severus` failed (`agent not found`); no visible Severus session. Fallback handoff posted to Discord `#agentchat` message/thread `1528750854428164126`. Severus replied in the shared channel/thread, recommending #23 as the current revocation candidate, #22 as the narrower recovery-gate candidate, and flagging duplicate PR selection, revocation authorization-boundary tests, recovery-gate outbox dispatch, and fresh-root restore coverage as next risks.
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/24
- Next candidate task: add a fresh-root restore drill that proves backup artifacts can be restored independent of the original runtime root, or resolve the duplicate revocation/recovery-gate PR stack before merging.

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
