# P2P NoSQL V1 Degraded-Mode Authority And Cache Policy

## Slice

This pass defines what head nodes may do when metadata store is unavailable, failing over, restoring, or too stale to answer safely.

Assumptions:
- metadata store is the v1 durable authority for identity, tenant and dataset policy, placement, object visibility, leases, revocation, invitations, reservations, repair, audit, and outbox.
- Head nodes are public coordinators and stream routers, not authority holders.
- Storage agents hold ciphertext and local evidence, but they do not decide visibility or placement.
- Any cache that can affect authorization, revocation, write visibility, placement, repair, capacity, or audit must have a hard maximum age and an explicit outage rule.

Design stance:
- Degraded mode is read-mostly and fail-closed.
- No operation may create new authority while metadata store is unavailable.
- Cached data may only serve client-visible reads for object versions that metadata store already marked committed before the outage.
- Heads should prefer a clear `metadata_unavailable` error over a locally improvised decision.

## Degraded-Mode States

Head nodes track a local metadata-connectivity state:

| State | Meaning | Head behavior |
| --- | --- | --- |
| `normal` | metadata store reachable, migrations current, authority cache within age limits | All authorized workflows may run through metadata transactions. |
| `degraded_read_only` | metadata store unavailable or failover in progress, but selected committed read cache entries are fresh | Serve only allowed cached reads; reject writes, deletes, repair ownership changes, admin mutations, invites, registration, and capacity-affecting workflows. |
| `authority_stale` | metadata store unavailable and required authority cache is too old or missing | Reject client and admin operations except health/status endpoints. Keep agent sessions alive for telemetry buffering only. |
| `recovering` | metadata store reachable again, but cache, outbox, audit, and invariant checks have not caught up | Reject unsafe operations until recovery gates pass. Reads may resume only after committed visibility cache is refreshed. |

Transition rules:
- `normal -> degraded_read_only` when metadata health fails but read cache age remains inside the policy table below.
- `degraded_read_only -> authority_stale` when any record required for a requested read exceeds its maximum age.
- `degraded_read_only -> recovering` when metadata store returns but cache reload and replay have not completed.
- `recovering -> normal` only after migrations are current, invariant checks pass, outbox lag is within threshold, audit append works, and all authority caches are rebuilt from metadata store.

SQLite authority implementation note:
- The `evaluate_recovery_gate` workflow records each gate in `recovery_gates` and keeps the associated node in `recovering` until migrations, invariant checks, outbox lag, audit append, and cache rebuild are all passing.
- A passing evaluation closes the gate and returns the node's `degraded_mode` to `normal`; failed evaluations stay open with a reason listing the missing checks.
- Each evaluation is idempotent and audited against the node so the admin surface can distinguish blocked recovery from successfully cleared recovery.

## Cache Policy Table

| Record | Max cache age | Allowed during metadata outage | Fail behavior | Revocation rule | Recovery/audit requirement |
| --- | --- | --- | --- | --- | --- |
| Tenant status | 30 seconds | Read authorization only for already committed object versions | Fail closed for writes, deletes, admin, quota, and policy changes | Any cached `suspended`, `deleted`, or revocation epoch blocks all tenant operations; missing or stale status blocks reads | Reload tenant status before leaving `recovering`; audit count of read attempts served or denied during outage |
| Dataset config | 60 seconds | Read authorization and replica target display only | Fail closed for writes, deletes, replication-policy changes, retention changes, and repair scheduling | Cached `frozen`, `deleting`, or `deleted` blocks writes and deletes; stale config blocks reads | Refresh dataset config and compare policy revision before serving reads normally |
| Admin identity/session | 0 seconds | No | Fail closed for every admin mutation and privileged read | Revocation is non-cacheable for outage approval; stale admin session is invalid | Audit denied admin attempts locally and append denial events after recovery with `degraded_buffered=true` |
| Revocation epochs | 15 seconds | Only as a deny source, never as an allow source | Fail closed if missing, stale, or lower than request envelope epoch | Highest known revocation wins; cached revocation blocks immediately; absence cannot prove validity | Rebuild revocation cache from metadata store before accepting any signed envelope that depends on it |
| Invitation records | 0 seconds | No | Fail closed for invite accept, create, revoke, and resend | Invitation validity is never outage-cacheable | No buffered accepts; recovery should audit denied attempts by token hash prefix only |
| Node status | 30 seconds | Use only to route cached reads to nodes last known active and connected | Fail closed for placement, repair target/source choice, node registration, drain, revoke, or capacity action | Cached `quarantined`, `revoked`, `retired`, or draining-for-read-disabled blocks node use; stale status blocks node use | Reload node table and reconcile agent heartbeats before writes or repair resume |
| Capacity reports | 0 seconds for admission, 60 seconds for display | Display only; no placement, reservation, repair expansion, or quota admission | Fail closed for capacity-affecting actions | Revoked/quarantined node capacity is ignored even for display totals | First post-recovery capacity reports must be marked fresh before write admission resumes |
| Placement records | 60 seconds | Serve reads only for committed object versions with enough eligible cached replicas | Fail closed for write placement, replica movement, stale cleanup, and repair | Any revoked/quarantined cached node is removed from candidate read set; if replica count falls below read quorum, deny read | Refresh placement by metadata revision and audit stale read decisions by object/version opaque IDs |
| Object visibility/head pointer | 30 seconds | Serve reads only when cached version state is `committed` and delete epoch matches cached pointer | Fail closed for writes, deletes, latest-pointer changes, and any ambiguous object state | Cached tenant/dataset/object delete marker or higher delete epoch blocks reads; stale pointer blocks latest reads | Refresh object head pointers before normal reads; run invariant check for versions read during outage |
| Routing hints | 30 seconds | Use only after tenant, dataset, object visibility, placement, and node status checks also pass | Fail closed to `metadata_unavailable`, not best-effort routing | Hints pointing at revoked/quarantined heads or nodes are discarded | Rebuild hints from metadata store/outbox after recovery; do not audit as authority |
| Read tokens | Token expiry, capped at 60 seconds | Existing read token may be honored only for a specific committed version, not `latest` | Fail closed if token references latest pointer, stale authority epoch, or missing replica set | Token invalid if any actor, tenant, dataset, object, or node revocation epoch is newer than token authority epoch | Post-recovery audit sampled token use and reject counters; require fresh token for subsequent reads |
| Repair leases | 0 seconds | No new lease, renewal, completion, cancellation, or source/target decision | Fail closed; in-flight worker may finish local copy but cannot mark metadata complete | Revoked node/head cancels local command dispatch; stale completions are rejected after recovery by fencing token | Recovery reconciles worker journals, lease expiry, final ACKs, and repair job state transactionally |
| Write reservations and leases | 0 seconds | No | Fail closed for create, stream-start, replica completion, commit, expiry conversion, or cleanup release | Any revocation blocks command dispatch immediately; stale completions remain local evidence only | Reconcile partial writes through metadata workflow; never make object visible from cache |
| Audit append | 0 seconds for durable authority audit, local tamper-evident buffer up to 10 minutes for denied attempts | Buffer denial/status events locally only; no privileged action may rely on buffered audit | Fail closed for operations requiring durable audit in the same transaction or if local denial buffer is unavailable | Revocation-sensitive attempts are buffered as denied, not allowed | Flush buffer after recovery with monotonic sequence, head id, local timestamp, hash-chain link, and `degraded_buffered=true`; gaps alert |
| Outbox delivery | Existing claimed-until only | Workers may finish already claimed idempotent deliveries if command was durably committed before outage; no new claims | Fail closed for new claim or mutation-derived delivery | Do not deliver commands to revoked/quarantined actors if revocation cache says deny or is stale | Reclaim expired rows after recovery; compare delivered ids, claimed ids, and worker journals |
| Health/readiness status | 5 seconds | Yes | Report degraded reason explicitly | Include cache age and revocation-cache status | Emit metrics for state transitions, rejection counts, and cache age |

## Operation Matrix

Allowed in `degraded_read_only`:
- health and readiness endpoints
- admin/status pages that clearly show stale source time and disable mutations
- storage-agent session keepalive
- telemetry buffering from agents, marked non-authoritative
- specific-version reads when tenant, dataset, object visibility, placement, node status, routing hint, read token, and revocation records are all inside their max age and deny-free

Rejected in every metadata outage state:
- create, update, delete, and commit object versions
- latest-pointer reads when the latest pointer cache is stale or token was not version-specific
- write reservations, replica completions, and visibility commits
- repair lease acquisition, renewal, completion, or placement change
- storage node join, revoke, drain, quarantine, or key rotation
- invitation create, accept, revoke, or resend
- admin mutations and privileged authority reads
- capacity admission, quota changes, reserve release, and pressure-state changes
- any operation requiring audit and outbox rows in the same metadata transaction

## Head Cache Implementation Rules

1. Cache entries must include `metadata_revision`, `loaded_at`, `source_txid` or equivalent metadata store revision marker, `policy_version`, and relevant revocation/authority epochs.
2. Cache lookup returns `Fresh<T>`, `Deny(reason)`, or `Unavailable(reason)`, never raw optional data.
3. Caches are typed by record kind; there is no generic metadata cache API for service workflows.
4. Positive authorization cannot be inferred from missing cache records.
5. Deny records outlive allow records until metadata refresh proves otherwise.
6. Any stale record required by a read makes the whole read fail closed.
7. Mutating workflows call `Hedgehog.Metadata.Sqlite` directly and do not accept cache-backed decisions.
8. Local degraded audit buffers are for denied attempts and status transitions only; allowed privileged actions are not buffered because they are not allowed.
9. Agent telemetry received during outage is evidence for later reconciliation, not authority for placement or capacity.
10. Recovery gates are explicit and observable before the head returns to `normal`.
11. Restrictive states are sticky during outage: a cached suspended tenant, frozen dataset, revoked actor, quarantined node, delete marker, or expired invitation can deny work, but cannot be cleared without metadata store.

## Required Test Fixtures

Add these before implementing head-node metadata caches:
- metadata outage rejects write intent, delete, admin mutation, invite accept, repair lease, replica completion, and capacity admission.
- Fresh committed specific-version read succeeds during outage only when every required cache entry is fresh.
- Latest read fails when object head pointer cache exceeds 30 seconds or read token is not version-specific.
- Cached revocation blocks a read immediately even if placement cache is fresh.
- Missing or stale revocation cache blocks operations that depend on signed envelopes.
- A node revoked just before outage is excluded from cached read routing.
- In-flight repair copy during outage cannot mark a replica healthy until metadata store fencing workflow accepts it after recovery.
- Denied admin attempts during outage are locally buffered and later flushed as denied audit events.
- Outbox worker cannot claim new rows during outage and reconciles already claimed rows after recovery.
- Recovery remains in `recovering` until migrations, invariant checks, audit append, outbox lag, and cache rebuild all pass.

## Risk Review

Accepted constraints:
- The system would rather reject safe-looking operations than create split authority.
- Read-only degraded mode is a product feature only for already committed, version-specific reads.
- Latest reads are dangerous during metadata outages because the cached pointer may hide deletes, policy changes, or newer committed versions.
- Revocation is asymmetric: cached deny is authoritative enough to block; cached absence is not authoritative enough to allow after max age.
- Admin and invitation workflows are never outage-cacheable.
- Repair and write completions can preserve local evidence during outage, but metadata store must later decide their semantic result.

Main remaining risk:
- Short cache ages protect authority but reduce outage read availability. That is acceptable for v1 because correctness, revocation, and auditability matter more than stale-read convenience.

## Research Incorporated

External review agreed with the fail-closed degraded-mode stance and recommended:
- cache entries carry metadata revision, policy version, source timestamp, and relevant revocation epochs
- restrictive states behave as sticky denies during outage
- invitations, admin mutations, repair leases, capacity admission, and write visibility changes require metadata store
- audit buffering should be tamper-evident and reconciled after recovery
- outbox workers may only finish already claimed idempotent events and must claim no new rows during outage

Accepted with stricter v1 limits:
- routing hints remain short-lived advisory data in this document instead of a longer cache, because routing can accidentally become authority when combined with stale placement
- local audit buffering is capped at 10 minutes for denied/status events, not a long outage window, because durable audit is a beta blocker and privileged actions are unavailable anyway

## Next Unresolved Portion

The next design slice should define the .NET project layout in enough detail to start scaffolding:
- solution members
- project ownership boundaries
- MSBuild properties
- shared error and ID types
- migration embedding
- deterministic CBOR test-vector location
- storage-agent manifest crash-test project boundary
- local-cluster harness ownership
