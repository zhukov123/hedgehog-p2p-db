# P2P NoSQL Risk Register

## Purpose

Track implementation risks and unresolved decisions for the Rust-first architecture.

This register focuses on failure modes, operational complexity, security gaps, metadata-plane risk, replication edge cases, capacity exhaustion, and Rust implementation hazards.

## Current Highest Risks

### 0. Architecture Boundary Drift Between Local-First P2P And Head-Mediated Storage

Risk:
- The older architecture/MVP documents describe local-first peer-to-peer database semantics, while the Rust-first design has moved toward public head nodes, a Raft-backed metadata plane, and outbound storage agents storing encrypted objects.
- If this boundary is not made explicit, implementation can accidentally mix two incompatible systems: an offline-first document database and a coordinated encrypted object store.

Failure modes:
- Engineers build local-first writes that bypass metadata reservations, leases, and capacity admission.
- Replication code assumes peer-to-peer object exchange while the v1 storage-agent protocol assumes head-mediated repair.
- Product promises offline writes even though the Rust-first storage design currently requires metadata quorum for new visible writes.
- Observability and runbooks use replication/conflict terms from the document database shape that do not map cleanly to object placement and repair.

Mitigations:
- Add a short canonical architecture note that names the v1 as head-mediated encrypted object storage with strong metadata, not the original local-first database.
- Keep peer-to-peer document database semantics as a separate future track unless explicitly reselected.
- Rename metrics, runbooks, and admin pages where needed to distinguish object repair from document conflict replication.
- Require every write-path design slice to state whether it depends on metadata quorum, local offline acceptance, or both.
- Follow [p2p-nosql-implementation-roadmap.md](p2p-nosql-implementation-roadmap.md): build `hedgehog-metadata-core` and `hedgehog-metadata-pg` first, then storage agents, head nodes, repair, admin, observability, and local-cluster polish.

Next decision:
- Keep the current v1 target as the head-mediated encrypted object store and treat local-first document database semantics as deferred unless explicitly reselected.

### 1. Metadata Plane Becomes The Real Control Plane Bottleneck

Risk:
- The design correctly makes metadata strongly consistent, but every placement, lease, quota, repair, delete, and visibility transition depends on it.
- PostgreSQL is the right v1 control plane choice, but the system still inherits database availability, failover, migration, backup, and restore risk.

Failure modes:
- Primary database loss freezes writes, admin mutations, repair ownership, and capacity changes until failover completes.
- Bad migrations or long-running locks can delay write admissions and repair scheduling.
- Head-node read caches may accidentally outlive revocation or account suspension policy.
- Backup/restore and PITR are assumed rather than rehearsed, leaving the project unable to recover cleanly from operator mistakes.

Mitigations:
- Define explicit read-cache max ages by metadata type.
- Add load tests for write admission, repair-job churn, and heartbeat/capacity-report volume before feature expansion.
- Keep all metadata commands idempotent and replay-testable.
- Define a degraded-mode policy that allows only safe reads and telemetry buffering when metadata quorum is unavailable.
- Require tested failover, PITR, restore drills, migration rollback plans, and durable mutation outbox processing before beta.
- Make the Milestone 1 beta blocker the real PostgreSQL migration set plus metadata-core transition tests before public API or storage protocol expansion.

Next decision:
- Pick the first PostgreSQL deployment posture: managed Postgres, self-hosted primary plus standby, or containerized dev-only Postgres with a clearly separate production plan.

### 2. Replication, Hints, And Repair State Machine Is Still Underdefined

Risk:
- Metadata and storage-agent slices mention repair, leases, hinted replay, and orphan cleanup, but the complete object/replica state machine is not yet locked.

Failure modes:
- A repair copy races with a delete tombstone or a newer object version.
- A source replica becomes corrupt or lost during a repair copy.
- Re-replication consumes the remaining repair reserve and blocks more urgent repairs.
- Tombstones are collected too early and deleted data reappears from an offline node.
- A hinted write is replayed after the object was deleted, overwritten, or its placement epoch changed.
- Read repair serves an opportunistic replica that has durable bytes but no valid metadata visibility.

Mitigations:
- Define a formal replica state transition table with legal transitions and fencing requirements.
- Make repair source selection prefer recently verified committed replicas.
- Add tombstone retention rules tied to max offline duration and repair audit completion.
- Add repair priority classes: durability loss, corruption, node drain, policy change, audit.
- Bind hinted replay to object version, delete epoch, placement epoch, lease fencing token, and max replay age.
- Keep repair reads explicitly separate from normal client-visible reads in protocol and metrics.

Next decision:
- Write the replication and repair state-machine slice before adding more product surface.

### 3. Capacity Exhaustion Can Cascade

Risk:
- The design has warning, soft, and hard watermarks, but capacity pressure interacts badly with repair, tombstones, temporary files, snapshots, and orphan cleanup.

Failure modes:
- The cluster admits writes based on metadata reservations while agents run out of physical temp space.
- Repair cannot proceed because every healthy node is near the hard watermark.
- Deletes free metadata quota before physical cleanup, hiding real disk pressure.
- Compaction and snapshot work need disk headroom that the admission policy did not reserve.

Mitigations:
- Reserve separate capacity budgets for live data, temp files, repair, tombstones, snapshots, and emergency cleanup.
- Treat metadata committed/reserved accounting and agent physical reports as two different constraints; placement must satisfy both.
- Keep delete and compaction progress visible in admin and Grafana views.
- Add failure tests where writes, repair, and cleanup all contend for the last free bytes.
- Define per-agent local admission checks for temp files and manifests so metadata reservations cannot overrun real disk.

Next decision:
- Define exact formulas for effective free capacity and repair reserve.

### 4. Security Model Needs A Root Of Trust

Risk:
- The current design requires invitations, signed envelopes, TLS, node identities, revocation, and audit logs, but the authority model is still open.

Failure modes:
- A compromised head node can issue valid-looking control messages unless metadata and agents can verify authority boundaries.
- A stolen storage-agent key can preserve access until revocation propagates.
- Invitation leakage enables Sybil joins or capacity abuse.
- Metadata leakage reveals user object counts, sizes, access patterns, and placement topology even though payloads are encrypted.
- Agent-reported capacity or health can be falsified to attract placement or trigger denial-of-service repairs.
- Signed envelopes without an explicit canonical encoding can create verification splits across versions.

Mitigations:
- Define the trust root: single owner key, admin quorum, or external IdP.
- Make revocation checks non-cacheable past a short hard max age.
- Bind invitations to expiry, trust domain, capacity ceiling, and one registration.
- Add metadata minimization and audit review for object-size and access-pattern exposure.
- Treat agent telemetry as untrusted input; reconcile it with committed metadata accounting and anomaly thresholds.
- Specify canonical serialization for signed envelopes and include protocol/schema versions in signature tests.

Next decision:
- Choose the admin authority and revocation propagation model.

### 5. Operational Complexity May Outrun The MVP

Risk:
- The product includes head nodes, metadata nodes, storage agents, clients, dashboards, Grafana, Prometheus or OpenTelemetry, repair workers, and backup/restore.

Failure modes:
- Operators cannot tell whether a write failure is due to metadata quorum, capacity admission, storage-agent disconnect, lease expiry, or policy rejection.
- Repair jobs pile up without a clear safe intervention path.
- Rolling upgrades break protocol compatibility between head, metadata, and agent versions.

Mitigations:
- Keep v1 deployment to one Compose stack with three metadata/head nodes and a small number of agents.
- Define error taxonomy across client, head, metadata, and agent APIs.
- Add runbooks before adding sharding, direct agent-to-agent repair, or erasure coding.
- Gate placement on agent protocol/schema compatibility.
- Make the admin dashboard show the exact admission blocker: metadata quorum, quota, node watermarks, repair reserve, lease failure, or revocation.

Next decision:
- Decide whether v1 bundles OpenTelemetry collector or Prometheus-only metrics.

### 6. Rust Async And Storage Hazards

Risk:
- Rust gives strong memory safety, but the design depends on tricky async networking, durable fsync semantics, local manifests, and consensus integration.

Failure modes:
- Holding locks across `.await` causes stalls or deadlocks under repair load.
- Bounded queues drop critical ACKs or anomaly reports without durable retry.
- Blocking disk fsyncs run on async executor threads and starve control streams.
- Protobuf/schema evolution accidentally breaks idempotency or signature verification.
- RocksDB bindings or custom storage layers introduce portability and operational burden on volunteer PCs.

Mitigations:
- Keep metadata state transitions in pure core crates with deterministic tests.
- Use bounded queues with explicit backpressure and durable command journals for critical events.
- Put blocking disk work behind `spawn_blocking` or dedicated worker pools.
- Add property tests for idempotency, fencing, tombstones, and duplicate delivery.
- Prefer the simplest durable local store that can pass crash-recovery tests before optimizing.
- Use one canonical time source abstraction for HLC, lease expiry checks, and test-controlled clock skew.
- Add crash tests around temp-file rename, manifest fsync, duplicate final ACK replay, and late delete/repair commands.

Next decision:
- Pick the initial embedded stores separately for metadata-node state and storage-agent manifests.

## 2026-06-04 23:05 UTC Review Notes

New or sharpened risks:
- The largest unresolved decision is not technical storage choice, but product semantics: the repo still contains both local-first P2P database language and the newer head-mediated encrypted object-store design.
- Hinted replay must be fenced as tightly as repair and delete; otherwise offline work can resurrect stale versions.
- Capacity admission needs both metadata reservations and local agent temp-space checks, because fsync, manifests, tombstones, and snapshots can fail after admission.
- Security needs a canonical signed-envelope encoding and an explicit stance that agent telemetry is adversarial until reconciled.
- Rust implementation risk is concentrated in durable async boundaries: fsync placement, lock scope around `.await`, retry journals, duplicate ACK handling, and clock-skew-dependent lease expiry.

Mitigation ideas:
- Make the next design slice a single canonical replication/repair state machine and include a preface that resolves the v1 architecture boundary.
- Add property/crash tests before service glue: command idempotency, stale fencing rejection, tombstone retention, hinted replay expiry, and capacity-reserve exhaustion.
- Define admin-visible blockers and metrics directly from the state machine so operators can distinguish capacity exhaustion, quorum loss, repair starvation, and security rejection.

Next decision:
- Confirm the v1 target as either head-mediated encrypted object storage or local-first document database. If head-mediated storage is confirmed, immediately write the replication/repair state-machine slice against that model.

## 2026-06-05 00:05 UTC Review Notes

New or sharpened risks:
- Metadata quorum loss is currently described as write rejection plus cache-bounded reads, but the exact cache freshness, revocation, and audit behavior is still not testable. Without hard rules, heads may diverge during outages.
- Lease expiry depends on time behavior across head nodes, metadata nodes, and agents. Clock skew can turn a safe late ACK into an accepted stale mutation or make healthy repair commands fail repeatedly.
- Repair can starve under low capacity because the same reserve must cover re-replication, tombstones, orphan cleanup, snapshots, and emergency compaction.
- Hinted replay remains dangerous unless it is explicitly tied to object version, placement epoch, delete epoch, command idempotency, and max replay age.
- Signed envelopes need a single canonical byte representation and downgrade policy before protobuf evolution begins, or different Rust crates can verify different messages.
- The storage-agent manifest is now a safety-critical database. Manifest corruption, partial fsync, rename failure, or duplicate command replay can silently break fencing and idempotency.
- Capacity telemetry is adversarial input. A malicious or broken agent can under-report usage, over-report free space, or flap watermarks to manipulate placement and repair scheduling.

Mitigation ideas:
- Add a degraded-mode matrix covering metadata quorum loss, revocation freshness, read-cache max age, telemetry buffering, and audit replay.
- Define a monotonic lease model: metadata assigns fencing tokens; agents compare tokens, not wall-clock trust, and tests inject clock skew.
- Split effective free capacity into live, reserved, temp, tombstone, orphan, snapshot, repair reserve, and emergency cleanup buckets.
- Require every hinted replay to pass the same metadata state-machine checks as a fresh repair copy.
- Freeze protocol-envelope canonicalization before service implementation; include cross-version signature vectors in CI.
- Treat agent manifests as a separate crash-tested Rust crate with property tests around fsync, atomic rename, duplicate final ACK, and stale command rejection.
- Reconcile agent capacity reports against metadata accounting and quarantine nodes with impossible or unstable reports.

Next decision:
- Define the replication/repair state machine and capacity-reserve formula together, because repair legality is inseparable from available physical and metadata-accounted space.

## 2026-06-05 01:05 UTC Review Notes

New or sharpened risks:
- The observability and production-readiness notes still use older local-first database terms such as coãm;öÚ$z{-®éÜj×6W2W†—7BæBF—6w&VRà¢ÒF†Rf—'7Bf—‡GW&RÖæ–fW7BF‚—2f—†VB2f—‡GW&W2÷66fföÆBöÖæ–fW7BçFöÖÆà¢Òf—‡GW&RÆ&VÇ2W6RFöÖ–âçv—&UöÆ&VÆ6òGWÆ–6FRv÷&G2Æ–¶Ræ÷&ÖÆÂVæF–ævÂ÷"W‡—&VF&VÖ–âVæÖ&–wV÷W2à¢ÒF†R–æ—F–ÂÖæ–fW7B×W7B6öçF–âöæR&WFÖ&Æö6¶–ærVçG'’f÷"V6‚f—'7B7&6‚æB6†÷266Væ&–òÂ–æ6ÇVF–ærÆFR4·2Â&Wfö¶VBÖæöFR6ö×ÆWF–öâÂ÷7Fw&U5Â&V6÷fW'’Â÷WF&÷‚ÆrÂ&W—"&W6W'fRW††W7F–öâÂ&VF"&WÆ’Â6æ6VÆÆF–öâgFW"g7–æ2Â&÷VæFVBVWVR÷fW&fÆ÷rÂæB6Æö6²6¶Wrà¢ÒF†RfÆ–FF÷"w2f—'7B'6W"7G&FVw’—2–çFVçF–öæÆÇ’&÷VæFVC¢&VÂDôÔÂ'6–ærf÷"6&vòæBf—‡GW&W2Â6W&FUö§6öæf÷"F6†&ö&G2Â7–æöæÇ’v†W&R'W7B6VÖçF–72ÖGFW"Â&÷VæFVB5ÂFö¶Vâ6†V6·2f—'7BÂæBÖ&¶F÷vâ66ææ–æröæÇ’f÷"FV×÷&'’6VVB6ö×&—6öâæBWW&66RV&çF–æRà ¤æWr÷"6†'VæVB&—6·3 ¢ÒF†RÖ–â&—6²†26†–gFVBg&öÒ'v†B6†÷VÆBF†RfÆ–FF÷"6†V6²"Fò&FöW2F†Rf—'7B–×ÆVÖVçFF–öâ7GVÆÇ’&÷fRf–ÇW&R&V†f–÷"â"fÆ–FF÷"F†B76W2âV×G’v÷&·76Rv—F†÷WBæVvF—fRf—‡GW&W26â7F–ÆÂ&V6öÖR÷&æÖVçFÂà¢Ò†VFvV†ör×G—W66â66–FVçFÆÇ’&V6öÖRvVæW&FVBÖf–ÆRw&W"FöòV&Ç’âW&R'W7B7FF–2&Vv—7G'’—26fW"f÷"F†Rf—'7B72&V6W6R—BÖ¶W2F†R7&FRF†RWF†÷&—G’æB¶VW2'V–ÆB67&—G2÷WBöbF†RG'W7BF‚à¢Òf—‡GW&RÖæ–fW7B66÷R6â7&vÂ–bV6‚66Væ&–òG&–W2FòVæ6öFRgVÆÂFW7B7FW2âF†Rf—'7BÖæ–fW7B6†÷VÆBFW67&–&R÷væW'6†—æB6÷fW&vRÂæ÷B&WÆ6R&VÂFW7G2à¢ÒFöÖ–â×VÆ–f–VBÆ&VÇ2&Ræ÷r&WV—&VBâç’gWGW&R'6W"÷"Öæ–fW7B6†÷'F†æBF†BG&÷2F†RFöÖ–â6â6–ÆVçFÇ’6öægW6RÆ&VÇ2&WW6VB7&÷727FFRÖ6†–æW2à¢ÒF†RWW&66RFVç–Æ—7B—2FVÆ–&W&FVÇ’&ÇVçBâ—BÖ’æVVB'6W"Öv&RW†6WF–öç2f÷"'W7BVçVÒf&–çG2Â'WBF†Rf—'7BfW'6–öâ6†÷VÆB&VfW"fÇ6R÷6—F—fW2–â6W'f–6R6öFR÷fW"ÆÆ÷v–æröÆB6öæ6WGVÂÆ&VÇ2–çFò5ÂÂÖWG&–72Âf—‡GW&W2Â÷"F6†&ö&G2à ¤Ö—F–vF–öâ–FV3 ¢Ò–×ÆVÖVçBfÆ–FF÷"æVvF—fRf—‡GW&W2Æöæw6–FRF†Rf—'7B76–ærFƒ¢æöâÖ6æöæ–6ÂÆ&VÂÂWW&66RF6†&ö&B7FFRÂf÷&&–FFVâFWVæFVæ7’ÂÖ—76–ærv÷&¶fÆ÷ræÖRÂ6W'f–6R7Ç†FWVæFVæ7’ÂÖ—76–ærf—‡GW&R66Væ&–òÂÖ—76–ær&W77W&RÆ&VÂÂÖ—76–ær&V6÷fW'’vFRÂæBVç7WW'f—6VBFö¶–ó£§7væà¢Ò¶VWf—‡GW&W2÷66fföÆBöÖæ–fW7BçFöÖÆ26öçG&7BÖæ–fW7Bv—F‚66Væ&–ò–BÂ÷væW"7&FRÂ÷væW"FW7BÂ&WF&Æö6¶W"fÆrÂv÷&¶fÆ÷r6÷fW&vRÂ&V6÷fW'’vFW2Â&W77W&RöFVw&FVBÆ&VÇ2ÂFöÖ–â×VÆ–f–VBÆ&VÇ2ÂæBfÆ–FF÷"6†V6·2à¢ÒÖ¶R†VFvV†ör×G—W6Æ&VÇ2ç'6F†Rf—'7BæöâÖV×G’7&FRÖöGVÆRÂv—F‚FW7G2&÷f–ærVÖ—GFVB7G&–æw2&RÆ÷vW&66RÂf—‡GW&R6ÇVw2&RF‚×6fRÂGWÆ–6FRÆ&VÇ2&WV—&RFöÖ–â6öçFW‡BÂæBWfW'’6æöæ–6ÂÆ&VÂV'2W†7FÇ’öæ6RW"FöÖ–âà¢ÒG&VBFö7226öç7VÖW'2gFW"†VFvV†ör×G—W6W†—7G3¢F†RfÆ–FF÷"6†÷VÆB6ö×&R66fföÆBæB–×ÆVÖVçFF–öâFö72FòF†RÆ&VÂ&Vv—7G'’Âæ÷B'6RÖ&¶F÷vâ2F†R6÷W&6RöbG'WF‚à ¤æW‡BFV6—6–öã ¢Ò7F'B–×ÆVÖVçFF–öâv—F‚‡F6²÷7&2÷66fföÆEö6öçG&7B÷6VVBç'6Âf—‡GW&W2÷66fföÆBöÖæ–fW7BçFöÖÆÂ7&FW2ö†VFvV†ör×G—W2÷7&2öÆ&VÇ2ç'6ÂæB7&FW2ö†VFvV†ör×G—W2÷FW7G2÷7FFUöÆ&VÇ2ç'6â&VfW"W&R'W7B7FF–2Æ&VÂ&Vv—7G'’÷fW"'V–ÆB×F–ÖRvVæW&FVBÖWFFFf÷"F†Rf—'7B66fföÆBâgFW"F†—2ÆæG2Âw&—FR'Öæ÷7ÂÖ7&FRÖÆ–÷WBæÖFv—F‚F†R7GVÂv÷&·76R6&vòçFöÖÆÂ7&FRfVGW&RfÆw2ÂV&Æ–2—2ÂæBf—'7B4’6öÖÖæG2à ¢22##bÓbÓb#£rUD2&—6²&Wf–Wp ¤æWr÷"6†'VæVB&—6·3 ¢ÒF†RæW‡Bf–ÇW&RÖöFR—2–×ÆVÖVçFF–öâF†VFW#¢7&VF–ær‡F6¶Â†VFvV†ör×G—W6ÂæBf—‡GW&Rf–ÆW2v—F†÷WBæVvF—fRFW7G2F†B&÷fRF†RfÆ–FF÷"f–Ç2f÷"F†R–çFVæFVB6öçG&7Bf–öÆF–öç2à¢ÒÖWFFF×ÆæR&—6²&VÖ–ç26VçFW&VBöâ'—72F‡2â–b†VBÂ&W—"ÂFÖ–âÂ÷"vVçB7&FW26â×WFFRWF†÷&—G’F&ÆW2F‡&÷Vv‚vVæW&–2†VÇW'2÷"F—&V7B7Ç†ÂF†R÷7Fw&U5Âv÷&¶fÆ÷rÖG&—‚Æ÷6W2—G2f÷&6Rà¢Ò'F–Â×w&—FR6Æ76–f–6F–öâ—27F–ÆÂF†RÖ–âGW&&–Æ—G’VFvRâÖ–æ÷&—G’g7–æ2ÂÆFRf–æÂ&W7VÇG2gFW"W‡—'’ÂFVÆWFRWö6‚'V×2Â&Wfö¶VBÖæöFR6ö×ÆWF–öç2Â–çFW''WFVB&W—"6öçfW'6–öâÂ&W7F÷&R&WÆ’ÂæB6ÆVçWVæFW"&W77W&RæVVBöæRÖWFFFÖ6÷&RFV6—6–öâF‚&Vf÷&R6W'f–6RvÇVRà¢Ò66—G’W††W7F–öâ6â7F–ÆÂ7Æ—B7&÷72&÷VæF&–W2â÷7Fw&U5Â&W6W'fF–öç2ÂvVçBÆö6Â†&B&V¦V7F–öâÂ&W—"&W6W'fRVÆ–v–&–Æ—G’ÂVÖW&vVæ7’6ÆVçWÂFöÖ'7FöæR&WFVçF–öâÂæB†VBG&ç6fW"F‡&÷GFÆW2×W7Bw&VRVæFW"7&—F–6ÆæBVÖW&vVæ7–à¢ÒFVw&FVBÖöFR6â&V6öÖR6†F÷rWF†÷&—G’ÆæRGW&–ær&V6÷fW&–æv–b66†W2Æöö²g&W6‚&Vf÷&R÷WF&÷‚ÂVF—BÂ&W6W'fF–öç2ÂÖæ–fW7G2ÂæB&W—"FVf–6—B&R&V6öæ6–ÆVBà¢ÒÖWFFF&—f7’&VÖ–ç2V7’FòÆV²F‡&÷Vv‚–×ÆVÖVçFF–öâFVfVÇG3¢ö&¦V7BÆ&VÇ2ÂFVæçB”G2Â–çf—FR7FFRÂ66—G’&W÷'G2ÂæöFRÆ6VÖVçBÂ†–v‚Ö6&F–æÆ—G’ÖWG&–72ÂG&6R&öF–W2ÂæBVF—BW‡÷'G2à¢Ò'W7B†¦&G2&VÖ–âBGW&&ÆR7–æ2&÷VæF&–W3¢6æ6VÆÆF–öâgFW"g7–æ2÷"FöÖ–2&VæÖRÂ&Æö6¶–ærF—6²v÷&²öâFö¶–òv÷&¶W'2ÂÆö6·27&÷72æv—FÂVç7WW'f—6VBFö¶–ó£§7væÂVæ&÷VæFVB6†ææVÇ2Â'6W"ÖÆ–v‡BfÆ–FF–öâÂæöâ×FW7BÖ6öçG&öÆÆVB6Æö6·2ÂæBW&Ö—76—fR&VF&&WÆ’à ¤Ö—F–vF–öâ–FV3 ¢Ò'V–ÆBF†Rf—'7BfÆ–FF÷"6Æ–6Rv—F‚72æBf–Âf—‡GW&W2–âF†R6ÖR6öÖÖ—C¢6æöæ–6ÂÖÆ&VÂG&–gBÂWW&66RV&çF–æVB7FFRÂf÷&&–FFVâFWVæFVæ7’Â6W'f–6R7Ç†ÂÖ—76–ærv÷&¶fÆ÷ræÖRÂÖ—76–ærf—‡GW&R66Væ&–òÂÖ—76–ær&V6÷fW'’vFRÂÖ—76–ær&W77W&RÆ&VÂÂæBVç7WW'f—6VB7vâà¢Ò¶VW†VFvV†ör×G—W62W&R'W7B7FF–2&Vv—7G'’f—'7C²&WV—&RFW7G2f÷"Æ÷vW&66Rv—&RÆ&VÇ2ÂF‚×6fRf—‡GW&R6ÇVw2ÂFöÖ–â×VÆ–f–VBÆöö·WÂæBöæRVçG'’W"6æöæ–6ÂÆ&VÂà¢ÒÖ¶Rf—‡GW&W2÷66fföÆBöÖæ–fW7BçFöÖÆæ'&÷r6÷fW&vR6öçG&7BÂæ÷BgVÆÂ66Væ&–ò'VææW#¢66Væ&–ò–BÂ÷væW"7&FRÂ÷væW"FW7BÂ&WF&Æö6¶W"fÆrÂ6÷fW&VBv÷&¶fÆ÷w2Â&V6÷fW'’vFW2Â&W77W&RöFVw&FVBÆ&VÇ2ÂæBfÆ–FF÷"6†V6·2à¢Òf÷&&–BWF†÷&—G’×F&ÆR5Â÷WG6–FR†VFvV†örÖÖWFFF×vÂÖ–w&F÷"6öFRÂæBW‡Æ–6—BFW7B†&æW76W3²Væf÷&6RF†—2v—F‚6&vòFWVæFVæ7’6†V6·2&Vf÷&R6÷W&6R66ææ–ærw&÷w26÷†—7F–6FVBà¢Ò7F'B†VFvV†örÖÖWFFFÖ6÷&Vv—F‚FV6—6–öâFW7G2f÷"7FÆR6ö×ÆWF–öç2ÂFVÆWFRWö6‡2Â&Wfö6F–öâÂ&W6W'fF–öâW‡—'’Â6ÆVçW6öçfW'6–öâÂæB&W77W&R÷&FW&–ær&Vf÷&R7F÷&vRÖvVçBf–æÂ×&W7VÇB†æFÆ–ærà¢ÒFB'VçF–ÖRw&W'2V&Ç’f÷"7WW'f—6VBF6·2Â&÷VæFVBVWVW2v—F‚W‡Æ–6—B÷fW&fÆ÷r&V†f–÷"ÂF—6²v÷&¶W'2÷"7våö&Æö6¶–ævÂ&WÆ–&ÆR÷WF&÷‚V&Æ–6F–öâÂ6æ6VÆÆF–öâ–æ¦V7F–öâÂæBFW7BÖ6öçG&öÆÆVB6Æö6²à ¤æW‡BFV6—6–öã ¢Ò–×ÆVÖVçBF†RfÆ–FF÷"6VVBæBf—‡GW&RÖæ–fW7B2F†Rf—'7B'W7B66fföÆB6Æ–6RÂæB&WV—&RFVÖöç7G&FVBæVvF—fRFW7Bf–ÇW&W2&Vf÷&RFV6Æ&–ærF†Rv÷&·76R&VG’f÷"6W'f–6R7&FW2à ¢22##bÓbÓb3£rUD2&—6²&Wf–Wp ¤æWr÷"6†'VæVB&—6·3 ¢ÒF†RÆVF–ær&—6²&VÖ–ç2&ööbÂæ÷B&÷6S¢F†Rf—'7B'W7B66fföÆB6â–æ6ÇVFR‡F6¶Â†VFvV†ör×G—W6ÂæBf—‡GW&RÖæ–fW7B–WB7F–ÆÂf–ÂFòFVÖöç7G&FRF†B6öçG&7Bf–öÆF–öç2&R6Vv‡Bâv—F†÷WBæVvF—fRf—‡GW&W2–â4’ÂF†RfÆ–FF÷"&V6öÖW26†V6¶Æ—7B&ææW"&F†W"F†ââVæf÷&6VÖVçB&÷VæF'’à¢ÒÖWFFF×ÆæR'—72&VÖ–ç2F†R†–v†W7BFFÖÆ÷72&—6²âvVæW&–2÷7Fw&U5Â×WFF–öâ†VÇW'2Â6W'f–6RÖ7&FR7Ç†FWVæFVæ6–W2Â÷"FÖ–â&W—"6†÷'F7WG2v÷VÆBÆWB†VBÂ&W—"Â÷"vVçB6öFR6–FW7FWF†RæÖVBv÷&¶fÆ÷rÖG&—‚Â–æ6ÇVF–ær–FV×÷FVæ7’ÂÆö6²÷&FW"ÂVF—BÂ÷WF&÷‚ÂæB–çf&–çB6†V6·2à¢Ò'F–Â×w&—FR6Æ76–f–6F–öâ—27F–ÆÂF†RÖ÷7BFævW&÷W2&WÆ–6F–öâVFvRâÖ–æ÷&—G’g7–æ2ÂÆFRf–æÂ&W7VÇG2gFW"&W6W'fF–öâW‡—'’ÂFVÆWFRWö6‚'V×2Â&Wfö¶VBÖæöFR6ö×ÆWF–öç2Â–çFW''WFVB&W—"6öçfW'6–öâÂ&W7F÷&R&WÆ’ÂæB6ÆVçWVæFW"&W77W&R×W7B6öçfW&vRF‡&÷Vv‚öæRÖWFFFÖ6÷&VFV6—6–öâF‚&Vf÷&Rç’ö&¦V7B&V6öÖW2f—6–&ÆR÷"ç’'—FW2&Rg&VVBà¢Ò66—G’W††W7F–öâ&—6²—27&÷72Ö&÷VæF'’F—6w&VVÖVçBâ÷7Fw&U5Â&W6W'fF–öç2Â7FÆR66—G’&W÷'G2ÂvVçBÆö6Â†&B&V¦V7F–öâÂVÖW&vVæ7’6ÆVçWÂFöÖ'7FöæR&WFVçF–öâÂ&W—"&W6W'fRW6RÂæB†VBG&ç6fW"F‡&÷GFÆW26â7F–ÆÂÖ¶R6öæfÆ–7F–ærFV6—6–öç2VæFW"7&—F–6ÆæBVÖW&vVæ7–à¢ÒFVw&FVB66†RæB&V6÷fW'’7FFR6â7F–ÆÂ&V6öÖR6†F÷rWF†÷&—G’ÆæRâGW&–ær&V6÷fW&–ævÂ66†W2Ö’Æöö²g&W6‚v†–ÆR÷WF&÷‚ÆrÂVF—B6öçF–çV—G’Â&W6W'fF–öâ&V6öæ6–Æ–F–öâÂvVçBÖæ–fW7G2ÂæB&W—"FVf–6—B&Ræ÷B–WB&V6öæ6–ÆVBà¢ÒÖWFFF&—f7’&VÖ–ç2VæFW"×7V6–f–VBB–×ÆVÖVçFF–öâFVfVÇG3¢FVæçB”G2ÂFF6WBæÖW2Âö&¦V7BÆ&VÇ2Â–çf—FRæB&Wfö6F–öâ7FFRÂæöFRÆ6VÖVçBÂ66—G’&W÷'G2ÂG&6R&öF–W2ÂÖWG&–2Æ&VÇ2ÂæBVF—BW‡÷'G26âÆV²6Vç6—F—fR7G'V7GW&RWfVâv†Vâ–ÆöG2&VÖ–âVæ7'—FVBà¢Ò'W7B†¦&G2&VÖ–â6öæ6VçG&FVB&÷VæBGW&&ÆR7–æ2&÷VæF&–W3¢6æ6VÆÆF–öâgFW"g7–æ2÷"FöÖ–2&VæÖRÂ&Æö6¶–ærF—6²v÷&²öâFö¶–òv÷&¶W'2ÂÆö6·2†VÆB7&÷72æv—FÂVç7WW'f—6VBFö¶–ó£§7væÂVæ&÷VæFVB6†ææVÇ2Â'6W"ÖÆ–v‡BfÆ–FF–öâÂæöâ×FW7BÖ6öçG&öÆÆVB6Æö6·2ÂæBW&Ö—76—fR&VF&&WÆ’gFW"7&6†W2à ¤Ö—F–vF–öâ–FV3 ¢ÒÆæBF†Rf—'7BfÆ–FF÷"6Æ–6Rv—F‚72æBf–Âf—‡GW&W2–âF†R6ÖR6öÖÖ—C¢6æöæ–6ÂÖÆ&VÂG&–gBÂWW&66RV&çF–æVB7FFRÂf÷&&–FFVâFWVæFVæ7’Â6W'f–6R7Ç†ÂÖ—76–ærv÷&¶fÆ÷ræÖRÂÖ—76–ærf—‡GW&R66Væ&–òÂÖ—76–ær&V6÷fW'’vFRÂÖ—76–ær&W77W&RÆ&VÂÂæBVç7WW'f—6VB7vâà¢Ò¶VW†VFvV†ör×G—W62W&R'W7B7FF–2Æ&VÂ&Vv—7G'’f÷"F†Rf—'7B66fföÆC²fÆ–FFRÆ÷vW&66RVÖ—GFVB7G&–æw2ÂF‚×6fRf—‡GW&R6ÇVw2ÂFöÖ–â×VÆ–f–VBÆöö·WÂGWÆ–6FRÆ&VÇ27&÷72FöÖ–ç2ÂæBöæRVçG'’W"6æöæ–6ÂÆ&VÂà¢ÒG&VBf—‡GW&W2÷66fföÆBöÖæ–fW7BçFöÖÆ26÷fW&vR6öçG&7BöæÇ“¢66Væ&–ò–BÂ÷væW"7&FRÂ÷væW"FW7BÂ&WFÖ&Æö6¶W"fÆrÂ6÷fW&VBv÷&¶fÆ÷w2Â&V6÷fW'’vFW2Â&W77W&RöFVw&FVBÆ&VÇ2ÂFöÖ–â×VÆ–f–VBÆ&VÇ2ÂæBfÆ–FF÷"6†V6·2à¢ÒVæf÷&6RÖWFFFWF†÷&—G’'’FWVæFVæ7’öÆ–7’f—'7C¢öæÇ’†VFvV†örÖÖWFFF×vÂÖ–w&F÷"6öFRÂæBW‡Æ–6—BFW7B†&æW76W2Ö’FWVæBöâ7Ç†÷"×WFFRWF†÷&—G’F&ÆW2à¢Ò–×ÆVÖVçBÖWFFFÖ6÷&VFV6—6–öâFW7G2f÷"7FÆR6ö×ÆWF–öç2ÂFVÆWFRWö6‡2Â&Wfö6F–öâÂ&W6W'fF–öâW‡—'’Â6ÆVçW6öçfW'6–öâÂ&W—"×&W6W'fRW††W7F–öâÂæB&W77W&R÷&FW&–ær&Vf÷&R7F÷&vRÖvVçBf–æÂ×&W7VÇB†æFÆ–ærà¢ÒFB'VçF–ÖRw&W'2V&Ç“¢7WW'f—6VBF6²w&÷W2Â&÷VæFVBVWVW2v—F‚W‡Æ–6—B÷fW&fÆ÷rFV6—6–öç2ÂF—6²×v÷&¶W"÷"7våö&Æö6¶–æv&÷VæF&–W2Â&WÆ–&ÆR÷WF&÷‚V&Æ–6F–öâÂ6æ6VÆÆF–öâ–æ¦V7F–öâÂFW7BÖ6öçG&öÆÆVB6Æö6·2ÂæB7G&–7B&VF&&WÆ’&V6öæ6–Æ–F–öâà ¤æW‡BFV6—6–öã ¢Ò'V–ÆB‡F6²÷7&2÷66fföÆEö6öçG&7B÷6VVBç'6Âf—‡GW&W2÷66fföÆBöÖæ–fW7BçFöÖÆÂæB7&FW2ö†VFvV†ör×G—W2÷7&2öÆ&VÇ2ç'6FövWF†W"Âv—F‚&WV—&VBæVvF—fRFW7G2&÷f–ærfÆ–FF÷"f–ÇW&R&V†f–÷"&Vf÷&Rç’6W'f–6R7&FR÷"Ö–w&F–öâ—266WFVBà ¢22##bÓbÓbC£bUD2&—6²&Wf–Wp ¤æWr÷"6†'VæVB&—6·3 ¢ÒF†RæW‡B–×ÆVÖVçFF–öâ&—6²—2fÇ6R6öæf–FVæ6Rg&öÒw&VVâfÆ–FF÷"F†BöæÇ’6†V6·2F†R†’×F‚66fföÆBâ–bæVvF—fRf—‡GW&W2&Ræ÷BfW'6–öæVB&W6–FRF†R6VVBÂgWGW&R6W'f–6R7&FW26â'—72Æ&VÇ2Âv÷&¶fÆ÷w2Â&W77W&RöÆ–7’Â÷"'VçF–ÖRwV&G&–Ç2v†–ÆR6&vò‡F6²fÆ–FFR×66fföÆBÖ6öçG&7F7F–ÆÂV'2†VÇF‡’à¢ÒF†RfÆ–FF÷"—G6VÆb6â&V6öÖRâWF†÷&—G’7Æ—BâFV×÷&'’‡F6¶6VVBFFÂ†VFvV†ör×G—W6Æ&VÂÖWFFFÂf—‡GW&RÖæ–fW7G2Â5Â66WFVBfÇVW2ÂF6†&ö&G2ÂæBFö726âG&–gBVæÆW72F†Rf—'7B–×ÆVÖVçFF–öâFVf–æW2öæR6ö×&—6öâF—&V7F–öâæBf–Ç2v†Vâ&÷F‚6÷W&6W2W†—7B'WBF—6w&VRà¢ÒÖWFFF×ÆæR'—72&VÖ–ç2F†R6VçG&ÂFFÖÆ÷72F‚âWfVâ&VBÖ÷&–VçFVB†VÇW"—26â&V6öÖR×WFF–öâ&6¶Fö÷'2–bF†W’W‡÷6RÆöFVB&÷w2ÇW2vVæW&–2F6‚ÖWF†öG2&F†W"F†âæÖVB†VFvV†örÖÖWFFF×vv÷&¶fÆ÷w2F†BÇv—2VæBVF—BæB÷WF&÷‚&V6÷&G2à¢Ò'F–Â×w&—FR†æFÆ–ær7F–ÆÂ†2FöòÖç’7&÷72Ö7WGF–ærG&–vvW'2f÷"B†ö26W'f–6RFV6—6–öç3¢ÆFR4²gFW"W‡—'’ÂFVÆWFRWö6‚'V×Â&Wfö¶VBæöFR6ö×ÆWF–öâÂ–çFW''WFVB&W—"6öçfW'6–öâÂ&W7F÷&R&WÆ’ÂvVçBÖæ–fW7BæöÖÇ’ÂæB6ÆVçWVæFW"&W77W&RÆÂæVVBöæR6Æ76–f–6F–öâ’&Vf÷&R†VB÷"vVçB6öFR–çFW'&WG2f–æÂ&W7VÇBà¢Ò66—G’W††W7F–öâ6â&R×Æ–f–VB'’F†RfÆ–FF÷"æBf—‡GW&R7G&FVw’—G6VÆbâ–bV&Ç’f—‡GW&W2öæÇ’æÖR&W77W&RÆ&VÇ2'WBFòæ÷Bf÷&6Ræ÷&ÖÆÂ&W77W&VÂ7&—F–6ÆÂæBVÖW&vVæ7–&V†f–÷"F‡&÷Vv‚6†&VB—2Â6W'f–6W26â–×ÆVÖVçB6ö×F–&ÆR7G&–æw2v—F‚–æ6ö×F–&ÆRFÖ—76–öâ6VÖçF–72à¢ÒFVw&FVB&V6÷fW'’6â7F–ÆÂ&V÷VâFöòV&Ç’â&V6÷fW&–æv7FGW2F†B—26ö×WFVBW"†VB–ç7FVBöbg&öÒ÷7Fw&U5Â&VF–æW72vFR6âÆWBöæR†VB&W7VÖRw&—FW2v†–ÆRæ÷F†W"7F–ÆÂ†27FÆR66†RÂ÷WF&÷‚ÂVF—BÂ&W6W'fF–öâÂ÷"Öæ–fW7B&V6öæ6–Æ–F–öâ7FFRà¢ÒÖWFFF&—f7’—2Æ–¶VÇ’FòÆV²F‡&÷Vv‚F†Rf—'7BÆö6ÂÖ6ÇW7FW"æBö'6W'f&–Æ—G’66fföÆG3¢f—‡GW&RæÖW2ÂF6†&ö&Bf&–&ÆW2ÂG&6Rf–VÆG2ÂVF—BW†×ÆW2ÂæBÆöw2Ö’æ÷&ÖÆ—¦R&rFVæçBÂFF6WBÂö&¦V7BÂ–çf—FRÂæöFRÂ÷"Æ6VÖVçB–FVçF–f–W'2&Vf÷&R&VF7F–öâ'VÆW2W†—7Bà¢Ò'W7B†¦&G2æ÷r–æ6ÇVFRfÆ–FF÷"'6W"6†÷'F7WG22vVÆÂ26W'f–6R'VçF–ÖR'Vw2â&VvW‚ÖöæÇ’66ç2f÷"DôÔÂÂ¥4ôâÂ'W7BÂ÷"5Â6âÖ—72FWVæFVæ7’'—76W2ÂvVæW&FVBF6†&ö&BÆ&VÇ2ÂV&Æ–2×WFF–öâgVæ7F–öç2ÂVç7WW'f—6VBF6²w&W'2Â&Æö6¶–ærf–ÆW7—7FVÒ6ÆÇ2ÂæBWW&66R7FFR–×÷'G2à ¤Ö—F–vF–öâ–FV3 ¢Ò6öÖÖ—BF†Rf—'7BfÆ–FF÷"6Æ–6RöæÇ’v—F‚&÷F‚76–ær66fföÆBf—‡GW&W2æBf–Æ–ærf—‡GW&R66W2f÷"V6‚f—'7B×66÷R6†V6³²Ö¶RF†Rf–ÇW&Rf—‡GW&W2÷&F–æ'’FW7G2Âæ÷B6öÖÖVçG2–âF†RFW6–vâFö2à¢ÒG&VB†VFvV†ör×G—W62F†R6÷W&6Röæ6R—BW†—7G2æB&WV—&R‡F6¶6VVB×g2Ö7&FRWVÆ—G’VçF–ÂF†R6VVB—2FVÆWFVC²Fö72ÂÖæ–fW7G2Â5ÂÂF6†&ö&G2ÂæBFÖ–âf–ÇFW'26†÷VÆB&R6öç7VÖW'2à¢Ò¶VW†VFvV†örÖÖWFFF×vv÷&¶fÆ÷r—2æ'&÷s¢æòvVæW&–2&÷rF6†W'2Âæò6W'f–6RÖ÷væVBG&ç67F–öç2f÷"WF†÷&—G’&÷w2Âæò&rÆöFVBWF†÷&—G’&V6÷&G2&WGW&æVBFò×WFF–öâF‡2ÂæBæòVF—Bö÷WF&÷‚Ö÷F–öæÂ×WFF–öâ†VÇW'2à¢ÒÖ¶R'F–Â×w&—FR6Æ76–f–6F–öââW‡Æ–6—B†VFvV†örÖÖWFFFÖ6÷&VFV6—6–öâ7W&f6Rv—F‚f—‡GW&W2f÷"WfW'’ÆFRÂ7FÆRÂ&Wfö¶VBÂ&W7F÷&VBÂ&W77W&RÂæBÖæ–fW7BÖæöÖÇ’6ö×ÆWF–öâ&Vf÷&R7F÷&vRÖvVçBf–æÂ×&W7VÇB†æFÆ–ærÆæG2à¢Ò&WV—&R&W77W&R×öÆ–7’f—‡GW&W2Fò&÷fR÷&FW&–æræBFVæ–Â&V†f–÷"Âæ÷B§W7BÆ&VÂ&W6Væ6S¢VÖW&vVæ7’6ÆVçW&VG2æWrw&—FW2ÂÖ–æ–×VÒ×7W'f—f&–Æ—G’&W—"&VG2FW6—&VBF÷×WÂæBvVçBÆö6Â†&B&V¦V7B&VG2ÖWFFFFÖ—76–öâà¢ÒV&Æ—6‚&V6÷fW'’&VF–æW72g&öÒöæRÖWFFFÖ&6¶VBvFRv—F‚W"ÖvFRf–ÇW&R&V6öç3²†VG2Ö’F—7Æ’Æö6Â6öææV7F—f—G’7FFR'WB6ææ÷B–æFWVæFVçFÇ’FV6Æ&RF†R6ÇW7FW"&6²Fòæ÷&ÖÆà¢ÒFB&VF7F–öâæB6&F–æÆ—G’'VÆW2FòF†R66fföÆBfÆ–FF÷"V&Ç’f÷"ÖWG&–72ÂF6†&ö&G2ÂG&6W2ÂÆöw2ÂæBf—‡GW&RæÖW3¢7F&ÆR6Æ72Æ&VÇ2&RÆÆ÷vVBÂ&rFVæçBöö&¦V7Bö–çf—FRöæöFR–FVçF–f–W'2&Ræ÷Bà¢ÒW6R&VÂ'6W'2f÷"6&vòDôÔÂæBf—‡GW&RDôÔÂg&öÒF†Rf—'7BfW'6–öâÂ¥4ôâ'6–ærf÷"F6†&ö&G2Â7–æf÷"'W7BV&Æ–2—2v†W&RæVVFVBÂæB&÷VæFVB5ÂFö¶Vâ6†V6·2öæÇ’VçF–Â7Ç'6W&—2§W7F–f–VBà ¤æW‡BFV6—6–öã ¢ÒFV6–FRF†RfÆ–FF÷"WF†÷&—G’†æFöfc¢gFW"†VFvV†ör×G—W6ÆæG2Â6†÷VÆB‡F6¶¶VWFV×÷&'’6VVBöæÇ’2&—G’6†V6²f÷"öæRÖ–ÆW7FöæRÂ÷"6†÷VÆBF†R6VVB&RFVÆWFVB–ÖÖVF–FVÇ’æB&WÆ6VB'’7&FRÖ÷væVBÆ&VÂÖWFFFÇW2Öæ–fW7BÖG&—fVâæVvF—fRFW7G3ğ