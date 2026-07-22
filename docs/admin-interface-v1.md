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

`GET /admin/v1/recovery/gates` returns the shared `recovery-readiness.v1` evaluator payload used by the local runtime `/health/ready`, `/health/cluster`, and `/metrics` surfaces. The response includes `ready`, `operationalSummary`, and canonical gate outcomes for:

- `schema_migrations`
- `metadata_invariants`
- `outbox_reconciliation`
- `audit_continuity`
- `cache_rebuild`
- `manifest_reconciliation`
- `reservation_reconciliation`
- `repair_deficit`
- `fresh_capacity_reports`

Admin recovery readiness is fail-closed: any `failed` or `unknown` canonical gate makes `ready` false, and evaluator failures return bounded `unknown` reasons instead of exception details or runtime filesystem paths.

## Operator Workflows

- Cluster status: inspect head health, metadata health, outbox lag, write mode, repair backlog, and capacity pressure; pause writes, resume writes, enter read-only mode, or trigger a health sweep.
- Nodes: review heartbeat age, drain state, write admission, capacity, and replica counts; drain, cancel drain, quarantine, or force verification.
- Capacity: compare physical, usable, committed, reserved, effective free, and emergency reserve bytes by global, tenant, and node scope; freeze or resume writes and trigger cleanup.
- Objects: filter by tenant, dataset, state, opaque object id, version, or lookup hash prefix; force repair, mark suspect, block GC, or unblock GC.
- Repair queue: sort and filter active work by state and priority; boost priority, retry failed jobs, or cancel safe duplicate jobs.
- Recovery gates: see canonical recovery readiness outcomes from the same evaluator contract as runtime health and metrics; approve, close, or export evidence for the backing admin gate records while unresolved failed or unknown gates keep recovery not ready.
- Audit: query actor, action, target type, result, request id, reason, and redacted metadata for operator action trails.
