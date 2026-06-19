# Hedgehog Admin Interface v1

This skeleton defines the first operator-facing admin surface without making the admin UI an authority. The metadata plane remains the source of truth; these endpoints are DTO and workflow contracts that can later be backed by real metadata, outbox, audit, repair, and capacity services.

## Projects

- `src/Hedgehog.Admin.Api`: ASP.NET Core minimal API for `/admin/v1`.
- `src/Hedgehog.Admin.Ui`: static ASP.NET-hosted operator console that consumes the API.

Run locally:

```bash
dotnet run --project src/Hedgehog.Admin.Api/Hedgehog.Admin.Api.csproj --urls http://localhost:5081
dotnet run --project src/Hedgehog.Admin.Ui/Hedgehog.Admin.Ui.csproj --urls http://localhost:5082
```

## API Surfaces

- `GET /admin/v1/cluster/status`
- `POST /admin/v1/cluster/actions/{action}`
- `GET /admin/v1/nodes`
- `GET /admin/v1/nodes/{nodeId}`
- `POST /admin/v1/nodes/{nodeId}/actions/{action}`
- `GET /admin/v1/capacity`
- `POST /admin/v1/capacity/scopes/{scopeId}/actions/{action}`
- `GET /admin/v1/objects`
- `GET /admin/v1/objects/{versionId}`
- `POST /admin/v1/objects/{versionId}/actions/{action}`
- `GET /admin/v1/repair/queue`
- `POST /admin/v1/repair/jobs/{jobId}/actions/{action}`
- `POST /admin/v1/repair/classes/{repairClass}/actions/{action}`
- `GET /admin/v1/audit/events`
- `GET /admin/v1/recovery/gates`
- `POST /admin/v1/recovery/gates/{gateId}/actions/{action}`

## Operator Workflows

- Cluster status: inspect head health, metadata health, outbox lag, write mode, repair backlog, and capacity pressure; pause writes, resume writes, enter read-only mode, or trigger a health sweep.
- Nodes: review heartbeat age, drain state, write admission, capacity, and replica counts; drain, cancel drain, quarantine, or force verification.
- Capacity: compare physical, usable, committed, reserved, effective free, and emergency reserve bytes by global, tenant, and node scope; freeze or resume writes and trigger cleanup.
- Objects: filter by tenant, dataset, state, opaque object id, version, or lookup hash prefix; force repair, mark suspect, block GC, or unblock GC.
- Repair queue: sort and filter active work by state and priority; boost priority, retry failed jobs, or cancel safe duplicate jobs.
- Recovery gates: see open operational blocks and allowed recovery actions; approve, close, or export evidence.
- Audit: query actor, action, target type, result, request id, reason, and redacted metadata for operator action trails.
