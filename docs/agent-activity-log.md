# Agent Activity Log

## 2026-07-19 - revoke actor or node workflow

- Branch: `agent/revoke-actor-node-2026-07-19`
- Task: implement the SQLite metadata `revoke_actor_or_node` workflow for actor and node revocation.
- Behavior:
  - Actor revocation moves the actor to `revoked`, timestamps `revoked_at_ms`, and revokes active invitations tied to that actor.
  - Node revocation moves the node to `revoked`, revokes active node keys, marks healthy replicas on that node `suspect`, and marks affected committed versions `under_replicated`.
  - Both paths are idempotent, append audit rows, and create durable `security.actor.revoked` or `security.node.revoked` outbox events that workers can claim.
- Tests:
  - `dotnet run --project tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj`
  - `dotnet run --project tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj`
  - `dotnet build Hedgehog.sln`
  - `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress`
  - `dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill`
  - `dotnet format Hedgehog.sln --verify-no-changes`
  - `docker compose -f observability/docker-compose.yml config --quiet`
- Container coverage: Docker is installed; the bundled observability compose file validates. The product still does not have a full multi-container database-node stack, so distributed runtime coverage remains the in-process multi-head/multi-storage smoke, stress, and restore drills.
- Severus: direct `agentId: severus` session was unavailable and no visible Severus sessions were listed; sidecar request sent in Discord `#agentchat`, message `1528464414318264431`.
- PR: https://github.com/zhukov123/hedgehog-p2p-db/pull/20
- Next candidate task: implement the remaining SQLite `accept_invite` workflow or design the recovery gate schema before `evaluate_recovery_gate`.

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
