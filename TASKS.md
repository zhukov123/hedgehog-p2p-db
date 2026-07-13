# Hedgehog V1 Task List

## Priority Rule

Admin is not a later dashboard. For v1, admin status, repair visibility, audit review, and recovery gates are part of the core system. A feature is not done until an operator can see its state and failure mode.

## Milestone 0: Scaffold Integrity

- [x] .NET solution scaffold exists.
- [x] Canonical label registry exists in `Hedgehog.Types`.
- [x] Scaffold validator runs through `Hedgehog.Xtask`.
- [x] Add CI for `dotnet build` and scaffold validation.
- [x] Add first fixture manifest for crash, recovery, capacity, admin, and repair scenarios.
- [x] Add project layout contract for all v1 projects.

## Milestone 1: Admin-First Control Plane Skeleton

- [x] Create `Hedgehog.Admin.Api`.
- [x] Create `Hedgehog.Admin.Ui`.
- [x] Define admin DTOs for cluster status, nodes, capacity, objects, replicas, repair jobs, audit events, and recovery gates.
- [x] Add admin read endpoints:
  - [x] `GET /admin/status`
  - [x] `GET /admin/nodes`
  - [x] `GET /admin/capacity`
  - [x] `GET /admin/objects`
  - [x] `GET /admin/objects/{objectId}`
  - [x] `GET /admin/repair/jobs`
  - [x] `GET /admin/audit`
  - [x] `GET /admin/recovery/gates`
- [x] Add guarded admin mutation endpoint shapes:
  - [x] quarantine node
  - [x] drain node
  - [x] revoke node
  - [x] retry repair job
  - [x] acknowledge recovery gate
- [x] Build dense operator UI views:
  - [x] overview
  - [x] nodes and capacity
  - [x] objects and replicas
  - [x] repair queue
  - [x] audit log
  - [x] recovery gates
- [ ] Ensure every workflow below has an admin-visible state.

## Milestone 2: Metadata Core

- [ ] Define ID, epoch, timestamp, and result primitives.
- [x] Define metadata command records.
- [x] Define metadata decision records.
- [ ] Implement validation for:
  - [x] create write intent
  - [x] complete replica
  - [x] commit version
  - [x] delete marker
  - [x] lease repair
  - [ ] expire reservation
  - [ ] cleanup conversion
  - [ ] capacity report
- [ ] Implement transition tables for object, version, replica, reservation, lease, repair job, node, invitation, and audit decisions.
- [ ] Add invariant checks for quorum, fencing token, placement epoch, delete epoch, idempotency scope, and capacity reservation.

## Milestone 3: SQLite Metadata Authority

- [x] Add forward-only SQLite migrations:
  - [x] security roots, tenants, datasets
  - [x] nodes, node keys, capacity reports
  - [x] objects, object versions, replicas
  - [x] leases, repair jobs, tombstones
  - [x] idempotency records, outbox events, audit events
  - [x] capacity reservations
- [x] Add migration runner.
- [ ] Add metadata workflow methods:
  - [x] create write intent
  - [x] complete replica
  - [x] commit version
  - [x] delete marker
  - [x] lease repair
  - [x] expire reservation
  - [x] cleanup conversion
  - [x] capacity report
  - [ ] accept invite
  - [ ] revoke actor or node
  - [ ] claim outbox
  - [ ] evaluate recovery gate
- [ ] Add invariant queries and repair-readiness checks.

## Milestone 4: Head Service

- [x] Create `Hedgehog.Head`.
- [ ] Add health, readiness, and metrics endpoints.
- [ ] Verify signed envelopes through `Hedgehog.Crypto`.
- [x] Route client write/read/delete requests through metadata workflows.
- [x] Coordinate outbound storage-agent sessions.
- [ ] Publish outbox work without becoming metadata authority.
- [ ] Expose admin API dependency boundaries without raw SQL.

## Milestone 5: Storage Agent

- [x] Create `Hedgehog.Agent.Core`.
- [x] Create `Hedgehog.Agent.Store`.
- [x] Create `Hedgehog.StorageAgent`.
- [x] Store ciphertext as file-per-object.
- [ ] Use agent-local SQLite manifest/journal.
- [ ] Implement local admission, temp file fsync, atomic rename, manifest update, final result journaling, fetch, delete, and restart reconciliation.
- [ ] Add crash tests for duplicate commands, duplicate final results, stale fencing, delete during write, and restart after partial durable steps.

## Milestone 6: Repair Worker

- [ ] Create `Hedgehog.Repair`.
- [ ] Scan under-replicated, suspect, corrupt, stale, and delete-pending replicas.
- [ ] Lease repair through metadata.
- [ ] Respect capacity pressure order.
- [ ] Produce admin-visible repair job status.

## Milestone 7: Client/Crypto

- [ ] Define deterministic signed envelope encoding.
- [ ] Add golden vectors for replay, downgrade, expiry, critical fields, payload hash mismatch, and actor/action rebinding.
- [x] Implement object data encryption metadata.
- [x] Implement object lookup hash helpers.
- [x] Provide first client library commands for put, get, delete, and list-by-friendly-name lookup.

## Milestone 8: Local Runtime

- [x] Create local cluster generator.
- [x] Generate ignored runtime directories and SQLite files.
- [x] Run multiple heads and three storage agents locally in the smoke runtime.
- [x] Add smoke scenario: create tenant, create dataset, register nodes, upload object, commit replicas, retrieve from another client, and delete.
- [x] Expose curlable local runtime API for tenant registration, object writes, reads, deletes, and status.
- [x] Expose local runtime health endpoints for liveness, readiness, and cluster diagnostics.

## Milestone 9: Tests And Release Gate

- [ ] Add unit tests for labels, transitions, and envelope vectors.
- [x] Add SQLite migration integration tests.
- [x] Add SQLite integration tests for create write intent, complete replica, commit version, delete marker, lease repair, expire reservation, cleanup conversion, and capacity report.
- [ ] Add SQLite integration tests for remaining workflows.
- [ ] Add storage-agent crash tests.
- [x] Add admin repository contract tests.
- [x] Add admin endpoint contract tests.
- [x] Add local runtime smoke test.
- [x] Add local runtime API health endpoint contract test.
- [x] Add restore drill test.
- [ ] Add CI gate for build, validator, tests, and formatting.

## Definition Of Done For V1

- [x] A user can store, fetch, and delete encrypted whole objects in the local runtime smoke.
- [ ] The metadata store never exposes plaintext object contents or required plaintext names.
- [x] At least three local storage agents can hold replicas in the local runtime smoke.
- [ ] Failed or missing replicas produce repair jobs.
- [ ] Repair can restore minimum replica count.
- [ ] Admin can see cluster health, nodes, capacity, objects, replicas, repair, audit, and recovery gates.
- [ ] Dangerous admin actions are signed, audited, and visible.
- [ ] A local restore drill proves metadata, outbox, reservations, and repair state recover coherently.
