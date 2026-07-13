# Agent Activity

## 2026-07-12 - agent/local-runtime-health-2026-07-12

- Task: added local runtime API health endpoints for liveness, readiness, and cluster diagnostics.
- Tests:
  - `DOTNET_ROLL_FORWARD=Major dotnet build Hedgehog.sln`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Hedgehog.LocalRuntime.Api.Tests/Hedgehog.LocalRuntime.Api.Tests.csproj`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress`
  - `DOTNET_ROLL_FORWARD=Major dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill`
- Container coverage: `docker compose -f observability/docker-compose.yml config` blocked because Docker is not installed in this VM.
- Push/PR: blocked. `git push -u origin agent/local-runtime-health-2026-07-12` timed out once waiting for credentials; retry with `GIT_TERMINAL_PROMPT=0` failed with `could not read Username for 'https://github.com': terminal prompts disabled`. `gh pr create` is also blocked because GitHub CLI is not authenticated in this VM.
- Severus: direct configured-agent routing failed (`agent not found: severus`); Discord handoff sent in `#agentchat` message `1526032919918022708`; no actionable reply found in thread `1526032919918022708` during this run.
- Next candidate: continue restore hardening with SQLite backup/checkpoint manifest and negative blob corruption tests if no branch already covers it.
