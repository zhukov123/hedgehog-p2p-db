# Hedgehog V1 Project Layout Contract

V1 projects are allowed only under `src/`, `tests/`, and `tools/`.

This contract keeps the architecture understandable while the implementation is still small. A new `.csproj` must be added here before it is added to the solution, and the scaffold validator rejects project files outside this list.

## Production Projects

| Project | Path | V1 state | Boundary |
| --- | --- | --- | --- |
| `Hedgehog.Types` | `src/Hedgehog.Types/Hedgehog.Types.csproj` | required now | Canonical labels and shared wire constants. |
| `Hedgehog.Crypto` | `src/Hedgehog.Crypto/Hedgehog.Crypto.csproj` | required now | Signed envelope and encryption contract code. |
| `Hedgehog.Metadata.Core` | `src/Hedgehog.Metadata.Core/Hedgehog.Metadata.Core.csproj` | required now | Metadata commands, decisions, validation, and state transitions without SQLite. |
| `Hedgehog.Metadata.Sqlite` | `src/Hedgehog.Metadata.Sqlite/Hedgehog.Metadata.Sqlite.csproj` | required now | SQLite migrations, authority runner, workflow store, invariant queries, and repair-readiness reads. |
| `Hedgehog.Admin.Api` | `src/Hedgehog.Admin.Api/Hedgehog.Admin.Api.csproj` | required now | Operator read and guarded mutation API. |
| `Hedgehog.Admin.Ui` | `src/Hedgehog.Admin.Ui/Hedgehog.Admin.Ui.csproj` | required now | Dense operator console for status, objects, capacity, repair, audit, and gates. |
| `Hedgehog.Head` | `src/Hedgehog.Head/Hedgehog.Head.csproj` | required now | Client-facing service that verifies envelopes and coordinates metadata plus storage-agent work. |
| `Hedgehog.Agent.Core` | `src/Hedgehog.Agent.Core/Hedgehog.Agent.Core.csproj` | required now | Storage-agent command model, admission rules, and restart reconciliation logic. |
| `Hedgehog.Agent.Store` | `src/Hedgehog.Agent.Store/Hedgehog.Agent.Store.csproj` | required now | Agent-local file store and SQLite manifest implementation. |
| `Hedgehog.StorageAgent` | `src/Hedgehog.StorageAgent/Hedgehog.StorageAgent.csproj` | required now | Runnable storage-agent process. |
| `Hedgehog.Repair` | `src/Hedgehog.Repair/Hedgehog.Repair.csproj` | planned | Repair scanner, lease worker, and repair job executor. |
| `Hedgehog.Client` | `src/Hedgehog.Client/Hedgehog.Client.csproj` | required now | First client commands for put, get, delete, list-by-friendly-name, and inspect metadata. |
| `Hedgehog.LocalRuntime` | `src/Hedgehog.LocalRuntime/Hedgehog.LocalRuntime.csproj` | required now | Local multi-head, multi-agent cluster generator and smoke scenario runner. |
| `Hedgehog.LocalRuntime.Api` | `src/Hedgehog.LocalRuntime.Api/Hedgehog.LocalRuntime.Api.csproj` | required now | Curlable local runtime API for tenant registration, object writes, reads, deletes, and status. |

## Test Projects

| Project | Path | V1 state | Boundary |
| --- | --- | --- | --- |
| `Hedgehog.Metadata.Core.Tests` | `tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj` | required now | Metadata validation and transition tests. |
| `Hedgehog.Metadata.Sqlite.Tests` | `tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj` | required now | Migration and SQLite workflow integration tests. |
| `Hedgehog.Admin.Api.Tests` | `tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj` | required now | Admin repository and endpoint contract tests. |
| `Hedgehog.Head.Tests` | `tests/Hedgehog.Head.Tests/Hedgehog.Head.Tests.csproj` | planned | Envelope verification and request routing tests. |
| `Hedgehog.StorageAgent.Tests` | `tests/Hedgehog.StorageAgent.Tests/Hedgehog.StorageAgent.Tests.csproj` | planned | Agent crash, duplicate command, stale fencing, and restart tests. |
| `Hedgehog.Repair.Tests` | `tests/Hedgehog.Repair.Tests/Hedgehog.Repair.Tests.csproj` | planned | Repair scan, lease, capacity pressure, and completion tests. |
| `Hedgehog.Client.Tests` | `tests/Hedgehog.Client.Tests/Hedgehog.Client.Tests.csproj` | planned | Lookup hash, encryption metadata, and client command tests. |
| `Hedgehog.LocalRuntime.Tests` | `tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj` | required now | End-to-end local cluster smoke and restore drill tests. |
| `Hedgehog.LocalRuntime.Api.Tests` | `tests/Hedgehog.LocalRuntime.Api.Tests/Hedgehog.LocalRuntime.Api.Tests.csproj` | required now | Local runtime API health and operator contract tests. |

## Tool Projects

| Project | Path | V1 state | Boundary |
| --- | --- | --- | --- |
| `Hedgehog.Xtask` | `tools/Hedgehog.Xtask/Hedgehog.Xtask.csproj` | required now | Repo validation, scaffold checks, and local runtime task automation. |
