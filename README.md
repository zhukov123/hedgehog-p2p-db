# Hedgehog P2P Object Store

Hedgehog is a head-mediated, client-encrypted peer-to-peer object store.

The accepted v1-alpha architecture is:
- SQLite metadata authority
- public head nodes for routing and coordination
- storage agents on participant machines
- client-side whole-object encryption
- opaque object IDs plus HMAC lookup hashes, with no required plaintext filenames in metadata
- whole-object replication
- head-mediated repair
- explicit capacity admission, leases, fencing, revocation, audit, and observability

Start here:
- [Canonical architecture](p2p-nosql-architecture.md)
- [Human guide with diagrams](p2p-object-store-guide.md)
- [.NET design brief](p2p-object-store-dotnet-design-brief.md)
- [Key model](p2p-object-store-key-model.md)
- [Implementation contract](p2p-nosql-implementation-contract.md)
- [Implementation roadmap](p2p-nosql-implementation-roadmap.md)
- [SQLite-first SQL schema plan](p2p-object-store-sqlite-schema-plan.md)
- [Deferred v2/v3 design](p2p-object-store-deferred-design.md)
- [V1 task list](TASKS.md)

Historical NoSQL/database-oriented documents are retained for context only. They are not v1-alpha implementation authority unless a newer contract explicitly adopts a feature.

## First Scaffold

The repository now includes the first .NET solution scaffold:
- `Hedgehog.Types`
- `Hedgehog.Crypto`
- `Hedgehog.Metadata.Core`
- `Hedgehog.Metadata.Sqlite`
- `Hedgehog.Agent.Core`
- `Hedgehog.Agent.Store`
- `Hedgehog.StorageAgent`
- `Hedgehog.Head`
- `Hedgehog.Client`
- `Hedgehog.LocalRuntime`
- `Hedgehog.Xtask`

Expected validation commands once the .NET SDK is installed:

```text
dotnet build Hedgehog.sln
dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract
```

## Local Runtime Smoke

The first local runtime proof starts two in-process head nodes, three file-backed storage agents, and two clients. The clients publish encrypted whole objects through different heads, retrieve each other's data, and verify a delete marker prevents later retrieval.

```text
dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-smoke
```

Expected shape:

```text
local runtime smoke passed
heads=2
storage_nodes=3
published_objects=2
verified_retrievals=2
delete_verified=True
healthy_replica_rows=6
```

## Local Runtime Stress

The stress gate creates three tenant datasets, writes 36 encrypted objects through both head nodes, verifies cross-client reads, deletes every fourth object, confirms delete markers block later reads, and checks that plaintext names did not leak into object IDs.

```text
dotnet run --project tools/Hedgehog.Xtask -- run-local-runtime-stress
```

Larger local runs can tune `--tenant-count`, `--objects-per-tenant`, and `--payload-bytes`.

Expected shape:

```text
local runtime stress passed
tenants=3
heads=8
storage_nodes=3
objects_written=36
reads_verified=63
deletes_verified=9
healthy_replica_rows=108
delete_marker_rows=9
```

## Local Restore Drill

The restore drill writes and deletes data in one local runtime, stops it cleanly, copies the metadata and storage directory to a fresh runtime root, restarts with the same tenant keys, verifies restored reads and delete markers, then proves new writes still commit after restore.

```text
dotnet run --project tools/Hedgehog.Xtask -- run-local-restore-drill
```

Expected shape:

```text
local restore drill passed
objects_written_before_backup=2
reads_verified_after_restore=2
delete_verified_after_restore=True
objects_written_after_restore=1
metadata_object_rows=3
metadata_version_rows=4
healthy_replica_rows=9
delete_marker_rows=1
```

## Curlable Local Runtime API

Start a fresh local runtime API:

```text
HEDGEHOG_RUNTIME_ROOT="$(pwd)/.hedgehog/curl-runtime" HEDGEHOG_RUNTIME_RESET=true \
  dotnet run --project src/Hedgehog.LocalRuntime.Api --urls http://localhost:5090
```

Create tenants and write/read/delete objects:

```text
curl -fsS -X POST http://localhost:5090/runtime/tenants \
  -H 'content-type: application/json' \
  -d '{"tenantId":"tenant-alpha","datasetId":"dataset-docs"}'

curl -fsS -X POST http://localhost:5090/runtime/tenants/tenant-alpha/datasets/dataset-docs/objects \
  -H 'content-type: application/json' \
  -d '{"clientId":"alpha-writer","name":"alpha-report.txt","text":"hello alpha","preferLastHead":false}'

curl -fsS 'http://localhost:5090/runtime/tenants/tenant-alpha/datasets/dataset-docs/objects?clientId=alpha-reader&name=alpha-report.txt&preferLastHead=true'

curl -fsS -X DELETE 'http://localhost:5090/runtime/tenants/tenant-alpha/datasets/dataset-docs/objects?clientId=alpha-deleter&name=alpha-report.txt'
```

Prometheus metrics are exposed by the same API:

```text
curl -fsS http://localhost:5090/metrics
```

## Grafana Dashboard

Start the local runtime API, then start Prometheus and Grafana:

```text
HEDGEHOG_RUNTIME_ROOT="$(pwd)/.hedgehog/observability-runtime" HEDGEHOG_RUNTIME_RESET=true \
  dotnet run --project src/Hedgehog.LocalRuntime.Api --urls http://localhost:5090
```

```text
docker compose -f observability/docker-compose.yml up
```

Open Grafana at `http://localhost:3000` and use the provisioned `Hedgehog / Hedgehog Local Runtime` dashboard. Prometheus is available at `http://localhost:9090` and scrapes `http://host.docker.internal:5090/metrics`.
