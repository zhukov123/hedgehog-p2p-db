# P2P NoSQL Admin Observability and Operations

## Slice

This pass defines v1 observability, admin dashboard, alerts, audit queries, and runbooks against the canonical model:
- PostgreSQL metadata is the source of truth.
- storage-agent reports are evidence.
- outbox and audit logs are the operational timeline.
- dashboards are views, not authority.

This slice is Milestone 6 in [p2p-nosql-implementation-roadmap.md](p2p-nosql-implementation-roadmap.md). It should not block metadata-core and PostgreSQL foundations, but it must be complete before beta.

## Metrics Taxonomy

Use labels carefully:
- allowed: `tenant_id`, `dataset_id`, `node_id`, `region`, `storage_class`, `state`, `reason`, `priority`
- avoid: `object_id`, `version_id`, raw object names, invite tokens, and high-cardinality request bodies

Core metrics:

```text
hedgehog_object_versions_total{state}
hedgehog_object_version_transitions_total{from,to,reason}
hedgehog_replicas_total{state,node_id,region}
hedgehog_replica_transitions_total{from,to,reason}
hedgehog_replica_under_replicated_total{tenant_id,dataset_id}
hedgehog_replica_over_replicated_total{tenant_id,dataset_id}

hedgehog_leases_active{lease_type,node_id}
hedgehog_leases_expired_total{lease_type,node_id}
hedgehog_fencing_token_rejects_total{operation,reason}

hedgehog_repair_jobs_total{state,priority,reason}
hedgehog_repair_job_age_seconds{state,priority}
hedgehog_repair_attempts_total{result,reason}
hedgehog_repair_bytes_pending
hedgehog_repair_bytes_completed_total

hedgehog_capacity_physical_bytes{node_id}
hedgehog_capacity_usable_bytes{node_id}
hedgehog_capacity_reserved_bytes{bucket}
hedgehog_capacity_effective_free_bytes{scope}
hedgehog_capacity_admission_rejects_total{scope,reason}
hedgehog_capacity_pressure_level{scope}

hedgehog_outbox_events_total{state,type}
hedgehog_outbox_event_age_seconds{state,type}
hedgehog_outbox_delivery_attempts_total{type,result}

hedgehog_security_auth_failures_total{reason}
hedgehog_security_signature_failures_total{reason}
hedgehog_security_invitation_events_total{event}
hedgehog_security_revocation_lag_seconds{principal_type}
hedgehog_audit_events_total{event_type,result}
```

Storage-agent local metrics:

```text
hedgehog_agent_disk_free_bytes
hedgehog_agent_temp_bytes
hedgehog_agent_orphan_bytes
hedgehog_agent_gc_queue_bytes
hedgehog_agent_upload_sessions_active
hedgehog_agent_local_admission_rejects_total{reason}
hedgehog_agent_heartbeat_age_seconds
```

## Admin Dashboard Pages

### Cluster Overview

Show:
- head health
- PostgreSQL health
- outbox lag
- repair backlog
- capacity pressure
- unavailable nodes

Actions:
- pause writes globally
- resume writes
- enter read-only mode
- trigger health sweep

### Objects and Versions

Show:
- tenant, dataset, object key, version id, and state search
- current version
- tombstones
- replica count
- placement epoch
- delete epoch

Actions:
- inspect version
- force repair
- mark suspect
- block GC

### Replicas

Show:
- replica state by node/region
- pending, healthy, suspect, deleting, deleted, and lost counts

Actions:
- quarantine replica
- force verify
- enqueue repair
- drain node

### Repair

Show:
- queue by priority, age, reason, tenant, and dataset
- failed repair reasons
- repair throughput

Actions:
- pause repair class
- boost priority
- retry failed job
- cancel safe duplicate jobs

### Capacity

Show:
- global, tenant, dataset, and node buckets
- physical bytes
- usable bytes
- committed bytes
- reserved bytes
- effective free bytes
- emergency reserve

Actions:
- freeze tenant writes
- raise/lower tenant quota
- drain node
- trigger cleanup

### Security and Authority

Show:
- admin keys
- invitations
- agents
- revoked principals
- revocation propagation lag

Actions:
- revoke agent/admin/head
- rotate signing roots
- expire invitations
- force config reload

### Audit

Show:
- filterable audit events by actor, action, target, result, reason, and request id

Actions:
- export evidence bundle for incident review

## Grafana Dashboards

Minimum dashboards:

- **Cluster SLO**
  - API latency, write success rate, read success rate, PostgreSQL latency, outbox lag, repair backlog age.
- **Replication Health**
  - Under-replicated versions, replica state counts, repair throughput, failed repair reasons.
- **Capacity**
  - Effective free by scope, reserve consumption, admission rejects, node fullness, projected days to full.
- **Storage Agents**
  - Heartbeat age, disk free, temp/orphan bytes, local rejects, upload failures.
- **Security**
  - Auth failures, signature failures, revoked-principal attempts, invitation events, revocation lag.
- **PostgreSQL**
  - Connection pool, locks, deadlocks, transaction latency, WAL/PITR status, replication lag, bloat.
- **Outbox**
  - Oldest pending event, attempts, dead-letter count, delivery latency.

## Alerts

Critical:
- any current committed object version has healthy replicas below required minimum for more than 5 minutes
- PostgreSQL primary unavailable for more than 60 seconds
- PITR/WAL archiving failing for more than 5 minutes
- revoked node/head/admin accepted after revocation grace window
- oldest pending outbox event for security/capacity/repair exceeds 10 minutes
- effective free capacity below emergency reserve
- restore drill failure

Warning:
- repair backlog oldest age over 30 minutes
- repair job failure rate above 5% over 15 minutes
- node heartbeat age over 2x expected interval
- tenant/dataset capacity above 85%
- global effective free below repair + temp + tombstone reserves
- signature/auth failure spike above baseline
- PostgreSQL transaction p95 above target for 15 minutes

Use burn-rate SLO alerts later. V1 starts with hard state and age thresholds.

## Audit Query Surfaces

Audit must support these questions before beta:

```text
Who changed tenant/dataset quota?
Who issued, used, revoked, or expired an invitation?
Which head accepted this write/delete/admin request?
Which admin revoked this node/key?
What object versions were affected by node X?
What repair jobs touched object/version Y?
Which requests used stale fencing tokens?
Which GC/delete operations removed bytes for tenant/dataset?
Which security decisions failed and why?
```

Every audit event needs:
- `event_id`
- `occurred_at`
- `actor_type`
- `actor_id`
- `authority_key_id`
- `request_id`
- `idempotency_key`
- `action`
- `target_type`
- `target_id`
- `result`
- `reason`
- `head_node_id`
- redacted metadata

## Incident Runbooks

### Repair Backlog

Steps:
- freeze low-priority repair creation if queue explosion is caused by duplicates
- identify dominant reason: node loss, capacity, verification failures, or outbox lag
- boost versions below replication minimum
- add/drain nodes only after capacity dashboard confirms reserve headroom
- verify completion from PostgreSQL state, not worker logs

### Capacity Pressure

Steps:
- move system through staged pressure levels: normal, constrained, critical, emergency
- at constrained, reject new large writes and low-priority tenants
- at critical, pause nonessential writes and prioritize deletes/GC/repair that frees unsafe placement
- at emergency, allow read-only plus deletes and admin recovery
- never consume emergency cleanup reserve for ordinary write admission

### Node Revocation

Steps:
- mark node revoked in PostgreSQL
- stop assigning new leases
- invalidate active leases through fencing tokens
- enqueue repair for all healthy replicas on that node
- monitor `revocation_lag_seconds` and rejected attempts
- GC node records only after tombstone and forensic retention

### Head Compromise

Steps:
- revoke head authority key
- rotate admin/session tokens
- force all heads to reload authority set
- query audit by compromised head id and time window
- revalidate accepted writes/deletes against PostgreSQL invariants
- treat logs from compromised head as untrusted; PostgreSQL audit/outbox are primary

### Failed Restore

Steps:
- declare restore environment read-only
- verify WAL continuity, latest recoverable timestamp, and schema migration version
- run metadata invariant checker for object current version, replica counts, tombstone/delete epochs, and expired leases
- compare sampled object manifests against storage-agent inventory
- do not promote until PITR and invariant checks pass

### Stale Outbox Events

Steps:
- check oldest event type and target aggregate
- confirm dispatcher lease/fencing status
- retry idempotently
- if poison event, move to dead-letter only with admin audit reason
- page sooner for security and deletion events than telemetry events

## Before Beta

Must build:
- PostgreSQL-backed metrics exporters for canonical state
- storage-agent local metrics
- audit table and query API
- cluster, repair, capacity, security, and outbox dashboards
- critical alerts listed above
- runbooks for repair backlog, capacity pressure, node revocation, head compromise, failed restore, and stale outbox
- restore drill with documented pass/fail criteria
- invariant checker CLI/admin action

Can wait until v2:
- full anomaly detection
- per-object trace visualization
- customer-facing observability portal
- automated capacity forecasting beyond simple trend panels
- SLO burn-rate sophistication
- cross-region chaos automation
- rich forensic timeline UI

## Strong Warning

Prometheus, Grafana, and logs are views, not operational authority.

PostgreSQL state, audit rows, and idempotent outbox records are the authority. If the admin console can take actions that bypass the same `metadata-core` transactions as normal protocol traffic, the architecture becomes unsafe.

## Research Incorporated

Severus reviewed the observability and admin-operations model.

Accepted findings:
- metrics must align to object/version/replica/lease/repair/capacity/security states
- admin actions must go through metadata-core transactions
- dashboards should expose PostgreSQL state, not infer core truth from logs
- critical alerts must include replica deficit, PostgreSQL availability, WAL/PITR failure, stale outbox, revocation failure, and emergency capacity
- beta requires runbooks and invariant checks, not just charts

## Next Unresolved Portion

The next design slice should define implementation roadmap and Rust workspace sequencing:
- crate-by-crate build order
- first migrations
- test harnesses
- minimal local cluster
- CLI workflows
- beta exit criteria
- issue backlog structure
