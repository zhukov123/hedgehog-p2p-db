# P2P NoSQL V1 Threat Model

## Slice

This pass turns the security authority model into implementation-facing threats.

Assumptions:
- PostgreSQL is the v1 authority for identity, authorization, placement, revocation, reservations, leases, repair, audit, and outbox state.
- Head nodes are public coordinators, not trust roots.
- Storage agents are untrusted ciphertext holders and proof reporters.
- Clients keep plaintext and raw data keys outside the server-side system.
- Signed envelopes use deterministic CBOR with golden vectors before service code.

## Threat Matrix

| Actor | Capability | Target | Trust boundary | Prevention control | Detection signal | Recovery/runbook |
| --- | --- | --- | --- | --- | --- | --- |
| Compromised head node | Forwards forged mutations, suppresses errors, replays old commands, lies to clients | Metadata workflows, storage-agent commands, client reads | Public head to PostgreSQL authority | PostgreSQL revalidates every mutation, signed envelopes, idempotency keys, lease fencing, short revocation-cache TTLs, no head-local placement authority | Head request/audit mismatch, impossible command sequence, stale metadata revision, outbox delivery gap, abnormal denial/allow rate | Quarantine head, revoke head credentials, rebuild caches, replay outbox/audit from PostgreSQL, rotate affected sessions |
| Malicious storage agent | Drops bytes, serves corrupt bytes, withholds ACKs, reports false durability | Replica durability and repair correctness | Storage-agent evidence to metadata authority | Hash verification, fsynced ACK requirement, signed agent identity, fencing tokens, verify jobs, repair from recently verified replicas | Hash mismatch, missing replica on verify, final ACK mismatch, anomaly reports, repeated command failure, repair deficit | Mark node suspect/quarantined, block placement, repair replicas away, revoke node if repeated, audit affected object versions |
| Stolen admin key | Issues privileged invites, revokes nodes, changes quotas, weakens policy | Security roots, tenant policy, node lifecycle | Human/admin signing boundary to PostgreSQL | Offline root separation, scoped roles, short-lived operational admin tokens, break-glass marker, idempotent signed envelopes, policy version checks | Privileged action from unusual actor/scope, break-glass use, role grant spikes, audit hash-chain checkpoint mismatch | Revoke admin identity, rotate tenant/admin CA, invalidate outstanding invites, PITR review, export audit window |
| Leaked invitation token | Registers unauthorized storage node or user identity | Membership, capacity, tenant access | Bearer invite to identity creation | One-time scoped invites, short expiry, secret hashes only, transactional accept, policy hash binding, raw-token log redaction | Multiple accept attempts, expired/revoked invite use, trust-domain mismatch, unusual join rate | Revoke invite, invalidate tenant outstanding invites if needed, quarantine joined node/user, audit invite lifecycle |
| Stale cached authority | Head accepts revoked tenant, node, admin, or invite during PostgreSQL outage | Reads, writes, repair, admin actions | Head cache to PostgreSQL source of truth | Per-record cache policy, fail-closed for revocation/admin/invite/write admission, cache max age, metadata revision checks | Request allowed with stale revision, revocation epoch mismatch, cache age breach, PostgreSQL unavailable with mutation attempts | Enter degraded mode, reject unsafe operations, rebuild caches from PostgreSQL, audit stale-window decisions |
| Metadata privacy leakage | Exposes object names, sizes, timing, placement, tenant graph, access patterns | Logs, metrics, traces, admin UI, APIs | Internal observability/admin surfaces to operators/users | Opaque IDs, encrypted client-side names, redaction defaults, role-scoped admin views, no object IDs in metrics labels, payload omission | High-cardinality labels, raw invite/object names in logs, trace body capture, broad admin query access | Purge/rotate affected logs where possible, tighten redaction, notify tenant policy owner, add regression tests |
| Capacity-report manipulation | Under-reports usage, over-reports free space, flaps watermarks to attract placement or trigger repair churn | Admission and repair scheduling | Agent telemetry to metadata placement | Treat reports as advisory, reconcile with committed/reserved bytes, freshness bounds, local hard admission, anomaly thresholds | Report/accounting divergence, impossible free-byte jumps, flapping pressure state, repeated local admission rejects | Quarantine node for placement, force verify scan, repair away if suspect, tune anomaly threshold after audit |
| Replayed or downgraded envelope | Reuses valid signature for another action, old protocol, stale payload, or expired lease | Admin, client, head-agent protocol | Signed protocol boundary | Deterministic CBOR canonical bytes, domain separation, action/resource binding, nonce/idempotency key, expiry/skew checks, downgrade rejection | Duplicate nonce with different payload, protocol downgrade attempt, payload hash mismatch, unknown critical field | Reject and audit, revoke key on repeated replay, add vector if parser accepted ambiguous bytes |
| Storage-agent manifest corruption | Loses fencing state, final ACK replay state, tombstone marker, or local admission accounting | Local durability and idempotency | Local disk state to agent command execution | `redb` manifest/journal crash tests, fsync/atomic rename, checksums, duplicate final-result records, startup invariant scan | Manifest checksum error, object file without manifest, manifest without bytes, duplicate result mismatch, startup scan anomaly | Mark local replicas unreadable, emit anomaly report, reconcile with metadata, repair/GC affected replicas, rebuild manifest only from verified bytes |
| Abusive tenant/client | Floods write intents, large objects, retries, reads, or repair-triggering deletes | Capacity, head bandwidth, outbox, PostgreSQL | Tenant API boundary to shared infrastructure | Tenant quotas, rate limits, 64 MiB object cap, transfer classes, reservation expiry, idempotency limits, outbox backpressure | Reservation leak alerts, quota pressure, per-tenant error spikes, large-transfer saturation, outbox lag | Throttle or suspend tenant, expire reservations, preserve deletes/GC, isolate repair priority, audit abuse window |
| PostgreSQL operator or migration error | Applies bad migration, loses WAL, restores stale authority state, blocks transactions | Metadata authority | Operator action to authoritative database | Migration fixtures, forward-only policy, PITR, restore drills, invariant checker, outbox replay test, transactional migrations where possible | Migration lock timeouts, invariant check failure, WAL archive alert, restore drill failure, outbox replay divergence | Stop writes, restore/PITR or forward-fix, run invariant checker, replay outbox, audit authority state before reopening |
| Rust async cancellation bug | Drops future during upload, fsync, outbox publish, repair completion, or journal write | State-machine integrity | Async task boundary to durable state | Named workflow states, durable journals, cancellation tests, `spawn_blocking` for fsync, no locks across `.await`, idempotent retry | Stuck `streaming` reservations, missing final ACK, outbox event without row state, leaked temp bytes, task panic spans | Retry idempotent workflow, expire/convert reservation, cleanup temp/orphan bytes, add cancellation regression test |

## Implementation Requirements

Before networked service work, add tests or fixtures for:
- compromised-head attempts that bypass PostgreSQL checks
- stale revocation cache behavior during PostgreSQL outage
- deterministic CBOR replay, downgrade, unknown critical field, and payload rebinding vectors
- false capacity reports reconciled against PostgreSQL accounting
- storage-agent manifest corruption and duplicate final-result replay
- tenant flood of idempotency keys, reservations, and large-object transfers
- PostgreSQL restore followed by invariant checks and outbox replay
- cancellation at every durable boundary in metadata and agent workflows

## Risk Priorities

Highest implementation risks:
- PostgreSQL authority bypass through accidental head-local decisions.
- Stale cached revocation or tenant state during metadata outages.
- Envelope canonicalization ambiguity before Rust service crates share test vectors.
- Storage-agent manifest corruption breaking fencing and idempotency.
- Capacity-report manipulation causing unsafe placement or repair starvation.
- Operational failure to restore PostgreSQL authority state with audit/outbox consistency.

## Next Unresolved Portion

The next design slice should define the degraded-mode authority and cache policy table:
- record type
- cache max age
- allowed during PostgreSQL outage
- fail-open or fail-closed behavior
- revocation interaction
- audit/outbox behavior after recovery
- admin visibility
