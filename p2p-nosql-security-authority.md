Exit code: 0
Wall time: 0.5 seconds
Output:
# P2P NoSQL Security Authority Model

## Slice

This pass defines the v1 security root and protocol authority model.

Assumptions:
- PostgreSQL is the source of truth for invitations, identities, revocation, epochs, permissions, and audit sequence.
- Head nodes are public infrastructure and must not be trusted as independent authority.
- Storage agents are untrusted blob holders and proof reporters.
- Payload plaintext and raw client keys never leave the client.

## Security Root

Recommended v1 authority:

- offline/root project key signs tenant/admin CA keys and cluster policy roots
- PostgreSQL stores operational admin identities, roles, scopes, revocation state, and policy versions
- privileged actions require signed admin envelopes and idempotency keys
- head nodes authenticate and forward, but PostgreSQL makes final authority decisions

Minimum roles:

- `root_admin`: rotates cluster trust roots and grants/revokes admin identities
- `tenant_admin`: manages tenant users, datasets, invitations, and quotas
- `ops_admin`: manages nodes, repair, capacity, drains, and observability access
- `auditor`: read-only audit and admin views
- `break_glass`: time-limited emergency role requiring explicit audit marker

All privileged changes should record:
- verified signer
- role
- scope
- request hash
- idempotency key
- result
- policy version

## Invitations

Invitation records live in PostgreSQL and are treated as bearer secrets plus signed policy.

Fields:
- `invite_id`
- `tenant_id`
- `dataset_scope` nullable
- `role_scope`
- `max_uses`, default `1`
- `uses_count`
- `expires_at`
- `revoked_at`
- `created_by_admin_id`
- `invite_secret_hash`
- `policy_hash`
- `created_at`
- `accepted_at`

Rules:
- default one-time use
- default short expiry, such as 24-72 hours
- scoped to tenant, role, and optionally dataset
- revocation is immediate in PostgreSQL
- raw invite tokens are never logged
- accept flow is one transaction: verify secret hash, check expiry/revocation/use count, create identity, increment `uses_count`, and write audit event

## Signed Envelope Canonicalization

Lock this before implementation.

Requirements:
- versioned envelope format with `envelope_version = 1`
- sign canonical bytes, not ad hoc JSON strings
- prefer deterministic CBOR or protobuf with strict field ordering rules
- reject unknown critical fields
- enforce clock skew limits
- store signature algorithm and key id
- bind signatures to operation type so a signed invite acceptance cannot be replayed as another command

Envelope fields:
- protocol version
- tenant id
- actor id
- key id
- nonce or request id
- idempotency key
- issued-at
- expires-at
- method or action
- resource scope
- payload hash

Avoid plain JSON canonicalization unless the project adopts a specific standard and tests it heavily.

## Head Node Authority

Head nodes may:
- terminate client connections
- verify envelope signatures
- reject malformed, expired, oversized, or unauthenticated requests
- rate-limit
- assign request IDs
- forward metadata mutations to PostgreSQL transactions
- issue short-lived leases only after PostgreSQL grants them
- emit outbox events after committed metadata changes

Head nodes must not independently decide:
- tenant membership
- dataset authorization
- node revocation status
- replica placement authority
- delete authority
- invite validity
- quota bypass
- repair eligibility
- lease fencing validity

Those checks must be made transactionally against PostgreSQL.

## Storage-Agent Key Rotation and Revocation

Each storage agent has a durable node identity and rotating operational keys.

Model:
- `storage_nodes(node_id, tenant_scope, status, current_key_id, revocation_epoch, placement_epoch, last_seen_at)`
- `node_keys(key_id, node_id, public_key, valid_from, valid_until, revoked_at, created_at)`
- `node_sessions(session_id, node_id, key_id, issued_at, expires_at)`

Rotation:
1. Agent generates a new keypair.
2. Agent signs rotation request with current valid key.
3. PostgreSQL records new key as pending/current.
4. Head nodes accept both old and new keys during overlap.
5. Old key expires automatically after grace window.

Revocation:
1. Set `storage_nodes.status = revoked`.
2. Increment `revocation_epoch`.
3. Revoke active keys and sessions.
4. Stop assigning new replicas to the node immediately.
5. Mark existing replicas suspect or untrusted until verified, repaired, or garbage-collected.
6. Propagate via PostgreSQL polling/watch plus outbox event.
7. Head nodes cache revocation state only with a very short TTL.

## Metadata Privacy

Assume metadata is sensitive even when object bytes are encrypted.

Controls:
- logs must omit object names, invite tokens, raw tenant secrets, envelope payloads, and full public keys unless specifically required
- client IP logging requires explicit policy
- metrics should aggregate by tenant, dataset, and node using opaque IDs or hashed labels
- metrics must avoid high-cardinality object IDs
- admin views are role-gated and tenant-scoped
- APIs expose only the placement/repair information the actor needs
- audit stores actor, action, scope, result, request hash, and object/version IDs while avoiding plaintext object labels
- tracing redacts request bodies and signed payloads by default

If object names are user meaningful, v1 should support encrypted client-side names or opaque object IDs with optional encrypted metadata blobs.

## Audit Events

Minimum audit table fields:
- `audit_id`
- `occurred_at`
- `actor_type`
- `actor_id`
- `tenant_id`
- `action`
- `resource_type`
- `resource_id`
- `request_id`
- `idempotency_key`
- `source_head_node_id`
- `decision`
- `reason_code`
- `policy_version`
- `signature_key_id`
- `payload_hash`
- `prev_audit_hash`
- `audit_hash`

Audit these before beta:
- admin login/session issuance
- admin role grant/revoke
- invitation create/accept/revoke/expire
- tenant/user/dataset permission changes
- storage node join/rotate/revoke/drain
- quota and capacity policy changes
- object write intent, commit, delete marker, and undelete if supported
- lease issuance and fencing failure
- repair job creation/lease/complete/fail
- GC deletion eligibility and execution
- break-glass access
- failed authorization attempts at meaningful thresholds

Use hash chaining or periodic signed checkpoints for tamper evidence.

## Incident Response Before Beta

Required drills:
- revoke compromised storage agent and force repair away from it
- revoke compromised admin key
- rotate tenant admin keys
- invalidate all outstanding invitations for a tenant
- disable a tenant without deleting data
- quarantine a head node
- rebuild revocation cache from PostgreSQL
- produce an audit export for a tenant/time window
- restore PostgreSQL to PITR and verify authority state consistency
- confirm no raw invite tokens or object plaintext metadata appear in logs

## Strong Warning

The naive version is trusting head nodes because they are public infrastructure.

That is the wrong boundary. Head nodes are protocol routers and cacheable policy enforcers. PostgreSQL plus signed envelopes is the authority. If a compromised head node can mint invites, authorize deletes, bypass revocation, or assign replicas without a PostgreSQL transaction, the security model collapses.

## Research Incorporated

Severus reviewed the v1 security authority model.

Accepted findings:
- v1 should use PostgreSQL-backed authority with offline/root admin signing keys and short-lived operational admin tokens.
- role-based admin identities are safer than a shared admin password.
- invitations are bearer secrets and signed policy records, never loggable tokens.
- signed envelopes need a canonical byte format before implementation.
- head-node authority must be narrow and bounded by PostgreSQL decisions.
- revocation state must propagate quickly and cannot be cached loosely.
- audit and incident drills are beta blockers.

## Next Unresolved Portion

The observability and admin-operations slice is captured in `p2p-nosql-admin-observability-ops.md`.

The next design slice should define implementation roadmap and Rust workspace sequencing:
- crate-by-crate build order
- first migrations
- test harnesses
- minimal local cluster
- CLI workflows
- beta exit criteria
- issue backlog structure

