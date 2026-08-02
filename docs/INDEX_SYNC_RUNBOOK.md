# Index Synchronization Operations Runbook

This runbook covers the current Cohesive materialization rebuild, incremental synchronization, generation
activation, and backend-pool routing model. The outer authority is one exact linked
`MaterializationRebuildPlanSet`: a canonical request, complete frozen membership, explicit subject-to-target
placement, bounded scheduling realization, declared promotion policy, and one exact placement-bound leaf plan per
slice. The executable unit is one `MaterializationRebuildPlan`, its full `MaterializationPlacementSliceReference`,
and one candidate target generation. The current high-level promotion executor realizes `Independent` leaf
promotion; the other declared cross-target policies still require a parent coordinator.

The core lifecycle and adapter components are independently tested. The focused ARI-180 acceptance suite covers
shared source conformance, Elasticsearch target and canonical-query execution, and the composed Cosmos DB or
PostgreSQL vertical slices; this document does not turn deterministic evidence into an end-to-end deployment
guarantee. Run
`eng/test-index-sync-vertical-slices.sh` before claiming that composition.

The following rules are invariant:

1. Execute only the persisted, fingerprint-verified request, membership, placement, plan-set, leaf, materialization,
   impact, source, target, and pool artifacts admitted for the run. Do not silently recompile drifted artifacts
   during recovery.
2. Keep a candidate generation isolated from readers until catch-up converges and target-local seal, validation,
   and promotion complete.
3. For a settling source, preserve the order `apply target effects -> commit application checkpoint -> settle
   provider progress -> record settlement receipt`.
4. Pause and Continue retain the current Process attempt and index generation. RestartAttempt abandons that
   generation and begins a fresh generation.
5. Target-local promotion and placement-scoped backend routing are separate durable decisions.
6. Carry both the placement-slice identity and its complete fingerprint. Slice identity alone is not sufficient
   authority for execution, routing, status, or recovery.
7. Never retire or clean up a generation while it is routed, admitted as a candidate, or still has in-flight work
   under any placement slice that shares the physical generation.

## Prerequisites

### Common deployment prerequisites

- Persist the canonical `MaterializationRebuildRequestDocument`, complete
  `MaterializationRebuildMembershipEvidence`, `MaterializationTargetPlacementPlan`, linked
  `MaterializationRebuildPlanSet`, every referenced `MaterializationRebuildPlan`, `MaterializationDocument`, compiled
  `MaterializationImpactPlan`, Process artifacts, and `MaterializationBackendPoolDocument`. Persist their exact
  references as well. Deserialize through the strict serializers on startup so schema versions, canonical
  fingerprints, slice bindings, and no-gap/no-overlap leaf coverage are revalidated.
- Resolve every planned source, Relations hydration plan, target, backend-pool dependency, and leaf binding by its
  exact persisted identity and fingerprint. Runtime endpoints must satisfy the capability profile and physical
  affinity pinned by the leaf plan and its full placement slice.
- Provide durable implementations for Process checkpoints, materialization progress, synchronization work, Control
  state, and backend routing before using this workflow in production. Routing state, command receipts, revisions,
  and ownership fences must be isolated by exact placement-slice fingerprint, not only by pool, target, or slice ID.
  The repository's `InMemory*` implementations are semantic reference authorities and test fixtures, not
  process-restart durability claims.
- Store continuation and position authentication keys in a secret store. Retain the appropriate key while any
  issued continuation or position is resumable. Never log the key or decode an opaque provider position in
  operational tooling.
- Ensure the target selected for a rebuild is not currently serving reads under any placement slice if
  feature-flag-level route isolation is required. Elasticsearch promotion moves that target's stable alias
  immediately; a later slice route swap cannot hide an alias change from callers already routed to the same target.
- Register tracing, metrics, and typed adapter observers before starting work. Observers must be fast,
  thread-safe, non-throwing, and must not place provider response bodies or secrets in telemetry.

### PostgreSQL source

Follow the [PostgreSQL adapter guide](../src/adapters/Cohesive.Adapters.Postgres/README.md#logical-replication) and
verify all of the following:

- The ordinary `NpgsqlDataSource` and logical-replication connection factory address the same single server,
  database, user, TLS configuration, and other non-secret affinity.
- `wal_level` is `logical`, and `max_replication_slots` and `max_wal_senders` have deployment capacity.
- One permanent, inactive, non-temporary `pgoutput` slot and one publication are dedicated to the exact
  materialization source placement.
- Publication, replica identity, projected columns, and before-image requirements satisfy adapter preflight.
  The current realization rejects `TRUNCATE` delivery, row filters, partition-root publication, two-phase decoding,
  and unsupported partial-column configurations.
- `PostgresLogicalReplicationBinding.SlotGeneration` identifies the physical slot incarnation and is rotated
  whenever the slot is dropped and recreated, even if its name is reused.
- Position and continuation authentication keys contain at least 32 bytes and remain available across an ordinary
  recovery or Pause/Continue.
- When temporal acquisition is used, `Npgsql.DisableDateTimeInfinityConversions` was set before Npgsql
  initialization and the matching runtime evidence is declared.

### Cosmos DB source

Follow the [Cosmos adapter guide](../src/adapters/Cohesive.Adapters.Cosmos/README.md#materialization-source) and
pin the account, database, container, partition-key representation, document discriminator, storage-binding
fingerprint, and runtime resource identity.

The full-fidelity pull source requires attributable continuous-backup and all-versions-and-deletes retention
evidence that covers the operating horizon. The managed processor requires a separate lease container and exact
lease-store affinity. The monitored and lease containers must not be the same resource.

### Elasticsearch target

Follow the
[Elasticsearch adapter guide](../src/adapters/Cohesive.Adapters.Elastic/README.md#generation-materialization-target)
and verify:

- `ElasticMaterializationTargetBinding` identifies the cluster, target, materialization, generation-index
  namespace, stable read alias, canonical Relations search binding, and external single-writer scope.
- The runtime binding attests the exact caller-owned client and cluster identity.
- The deployed index template matches the persisted template fingerprint. The fingerprint is provenance, not a
  live drift check.
- The external single-writer guarantee is enforced across runtime instances. Target-local admission is not a
  distributed lease.
- Item, canonical-byte, parallelism, diagnostic, and indexed-identity limits are no larger than the cluster,
  proxy, and client can preserve.
- `ElasticMaterializationTelemetry.InstrumentationName` is registered with OpenTelemetry.

## Admission, fingerprints, and provenance

Record the following evidence together before starting or resuming a run:

| Authority | Evidence to retain |
| --- | --- |
| Semantic definition | Materialization schema version, definition identity, and `DefinitionFingerprint` |
| Rebuild request and plan set | Request/reference fingerprint, complete membership authority and cut, placement-plan fingerprint, plan-set fingerprint, scheduling realization, promotion mode, and partial-failure policy when applicable |
| Rebuild leaf realization | Rebuild-plan schema version, `Fingerprint`, exact plan reference, provenance, limits, stable shard catalog, and complete change-feed catalog |
| Placement slice | Slice ID, full algorithm/canonicalization/value fingerprint, materialization definition reference, membership fingerprint, pool reference, target, and canonical subjects |
| Incremental semantics | Impact-plan fingerprint and exact route/link evidence |
| Relations | Canonical plan and hydration physical-plan fingerprints, placement fingerprint, and adapter storage-binding fingerprints |
| Source | Source/profile identity, capability evidence, physical scope, runtime affinity, and authenticated initial cut |
| PostgreSQL | Database/runtime authority, publication, slot, `SlotGeneration`, server/timeline affinity, and replica-identity evidence |
| Cosmos DB | Account/database/container affinity, binding digest, feed mode, retention evidence, and lease-store/processor namespace when managed |
| Elasticsearch | Target-binding fingerprint, search-binding fingerprint, template fingerprint, cluster/target identities, read alias, and single-writer evidence |
| Process and generation | Process definition revision/fingerprint, instance, attempt, generation affinity, activation identity, and worker fences |
| Backend routing | Pool definition fingerprint plus exact placement-slice ID and fingerprint, resolved configuration with origin/precedence, slice-scoped routing revision and fence, and exact read/write generation references |
| Control | Effective loop fingerprints, generation epoch, durable revision, operating point, pending recommendation/override, and application fence |

Treat a fingerprint mismatch, unknown schema version, runtime-affinity mismatch, stale worker fence, or reused
identity with different content as a stop condition. Preserve the structured diagnostic and the conflicting
artifacts. Do not repair the mismatch by overwriting retained evidence or by constructing a new value under the old
identity.

Opaque continuations and change positions are durable adapter values, not operator-editable offsets. Redact them
from general logs; if they must be captured for incident analysis, use the same access controls as application
checkpoints.

## Start and recover a synchronization run

1. Load and validate the exact request, membership, placement, plan set, and referenced leaf plans. Re-link them and
   reject changed selection, membership, pool, target, subjects, scheduling, promotion, or leaf content.
2. Select one exact `MaterializationRebuildLeafPlanBinding`. Retain both its slice ID and fingerprint, and verify the
   leaf's `MaterializationRebuildPlanReference` carries that same slice fingerprint.
3. Call `IMaterializationBackendRouter.InspectAsync(context, placementSlice)` for that exact slice and inspect every
   generation it references. Call `ResolveReadAsync` or `ResolveWriteAsync` with the same slice only after its routes
   are initialized; never infer a route from pool state or target identity.
4. Resolve source and target bindings without substituting a similarly named endpoint, then run provider preflight.
   PostgreSQL preflight inspects publication, table, replica identity, slot, plugin, slot
   generation, and server affinity. Cosmos and Elasticsearch runtime bindings must prove their exact resource
   identities.
5. Initialize or restore the materialization rebuild Process. Persist the Process attempt-to-generation affinity
   before physical candidate creation. Exact initialization replay must resolve the same generation.
6. Begin the isolated target generation. Its deterministic identity includes the placement-bound leaf-plan
   fingerprint. Beginning the target candidate does not admit or expose it through placement-scoped routing.
7. Capture every planned change-feed cut before baseline enumeration. Persist those cuts independently from
   baseline continuations.
8. Execute bounded baseline pages. Each page uses deterministic hydration, mutation, batch, checkpoint, and
   Process-request identities. A replay must produce the same canonical target intent.
9. After all baseline shards report `baseline-complete/catch-up-required`, run bounded synchronization from the
   retained cuts. Persist effect-free position advances as well as pages containing changes.
10. For settling sources, settle only after the target effect and exact application checkpoint are durable. Drain an
   unfinished settlement before reading another page from that source scope.
11. Require a catalog-complete, fresh convergence receipt. Then let
    `MaterializationGenerationActivationExecutor` persist and reconcile seal, validation, and target-local
    promotion in that order, yielding a placement-bound `MaterializationActiveGenerationReference`.
12. For `Independent` promotion, create and persist one strict
    `MaterializationIndependentPromotionRequest` from the exact linked plan set, leaf, placement slice, active
    generation, pre-admission slice snapshot, routing fence, stable command IDs, and timestamps. Then invoke
    `MaterializationIndependentPromotionExecutor`. It first admits the exact candidate and then atomically switches
    that slice's paired read/write routes. Recovery must deserialize and replay the retained request, not reconstruct
    it from a later snapshot.
13. Do not execute `AllReadyProgressive` or `AtomicVisibility` leaf visibility changes independently. Their declared
    readiness barrier, partial-failure behavior, compensation, or all-or-none guarantee requires a durable parent
    coordinator that is not yet supplied by the reference runtime.

An ordinary retry of an exact durable operation reuses its operation, mutation, batch, and generation identities.
If the retained target intent no longer matches an exact replay, stop with RestartRequired; continuing the same
attempt would weaken idempotency.

## Inspect status

Use `MaterializationIndexSyncStatusProjector.CreateExtension` to combine one placement-scoped routing snapshot, exact
generation snapshots, per-scope progress, durable Control snapshots, provider lag/failure observations, and runtime
provenance. Schema `index-sync-status/v3` carries `placementSlice` and `placementSliceFingerprint`. Publish or index
each projection under `MaterializationIndexSyncStatusWireNames.PlacementStatusPath(placementSlice)`, whose path
includes the materialization, pool, slice ID, and full fingerprint components. Do not publish one pool-global status
document or key by slice ID alone. The `cohesive.storage.index-sync.status` extension is a projection of its
authorities; it must not become a second writable status store.

At minimum, display and alert on:

- Process instance, attempt, control mode/revision, current generation affinity, latest activation, and any
  RestartRequired diagnostic.
- Placement-slice ID and full fingerprint, backend-pool definition fingerprint, slice-scoped revision/fence, active
  read and write coordinates, candidate, draining, retired, cleaned, and effective configuration provenance.
- Generation state, visible item and tombstone counts, pending retryable mutations, permanent-failure flag, seal,
  validation, and promotion evidence.
- Baseline continuation/completion, incremental position, applied delivery identities, latest checkpoint, and
  latest settlement for every planned source scope.
- Convergence disposition and the exact feed catalog covered by its receipt. A provider `CaughtUp` result is
  bounded to the end observed by that read; it is not a promise that no later change can arrive.
- Current Control operating points, pending recommendations or operator overrides, last application fence,
  pressure classification, and measurement availability.
- PostgreSQL slot health and retained/pending/safe WAL estimates, Cosmos request-unit and lag evidence when
  available, Elasticsearch retryable rejection pressure, and structured sanitized failures.

Unknown lag or unavailable measurements must remain unknown. Do not synthesize zero, parse human-readable provider
errors into state, or publish an ETA without its bounded throughput inputs and observation window.

## Pause, Continue, and RestartAttempt

| Operation | Process and generation effect | Provider effect |
| --- | --- | --- |
| Pause | The accepted command reaches an ordinary Process safe point and retains the current attempt, generation affinity, source progress, and Control epoch. It performs no candidate lifecycle I/O. | No progress is invented. PostgreSQL WAL and Cosmos history can continue accumulating while work is paused. |
| Continue | Resumes the same attempt and generation from retained continuations, change positions, pending work, settlements, and Control state. | Reuses the same PostgreSQL slot generation or Cosmos managed deployment namespace. |
| Exact operation recovery | Reconciles or replays the same durable intent within the current attempt. It does not allocate another generation. | The adapter must return exact applied/replayed evidence or a typed failure requiring escalation or restart. |
| RestartAttempt | Commits the Process replacement, permanently abandons or tombstones the old generation, binds the replacement attempt, and begins exactly one fresh generation. Exact command replay resumes that same sequence. | Captures fresh cuts and starts a new Control epoch. A managed Cosmos rebuild gets a distinct lease namespace. Old progress is retained for audit but is not transplanted into the replacement. |

Do not implement RestartAttempt by deleting the old index first. The old identity must be durably excluded before a
delayed begin or write can revive it. If abandonment is unresolved, leave the restart pending and reconcile the
same abandonment request.

## Retryable rejection and adaptive throttling

`MaterializationTargetBatchWriter` splits work under the currently applied item and canonical-byte bounds and
retries only mutations returned as `RetryableRejected`. Successfully applied items are not rewritten. The
Elasticsearch adapter treats HTTP 408, 425, 429, 500, 502, 503, and 504 as retryable status evidence; retry remains
bounded by the materialization failure policy. Permanent failure, idempotency conflict, exhausted retry budget, or
stale ownership must not advance the application checkpoint.

Adaptive Control is evidence-driven rather than an implicit sleep loop:

1. Adapter observers emit typed, occurrence-safe measurements. For Elasticsearch, only
   `ElasticMaterializationTargetControlEvidenceKind.PressureSample` is eligible to influence the regulator; replay,
   mixed replay, cancellation, and otherwise ineligible operations carry unavailable measurements.
2. The runtime host maps eligible evidence to the exact generation and calls
   `MaterializationIndexSyncControlRuntime.ObserveStageAsync`. Adapter observers do not own the Control loop or its
   durable revision.
3. The AIMD reference regulator persists a recommendation under an exact epoch and revision.
4. Workers apply eligible recommendations only through `AtSafePointAsync`, `AcquireStageAsync`, or target batch
   resolution at the declared admission or batch boundary.

Applied limits may reduce concurrency, batch items, or batch bytes, but cannot exceed plan and provider hard bounds.
Admission is realtime-first and non-preemptive: existing work is not cancelled to reclaim capacity. Operator limit
updates use the same durable state, authorization scope, revision fence, and safe-point application rules as adaptive
recommendations.

For sustained rejection:

- Confirm that the observer-to-Control bridge is running and that evidence is eligible and attributed to the current
  epoch.
- Inspect both the recommendation and the last applied safe point; a pending recommendation is not yet an applied
  limit.
- Check Elasticsearch shard, thread-pool, disk-watermark, and cluster health outside Cohesive using deployment
  tooling. Preserve only sanitized provider classifications in Cohesive status.
- Reduce hard deployment bounds only through a new attributable policy/plan. Do not mutate a persisted plan in
  place.
- If retries exhaust or an item becomes permanent, stop activation and investigate or RestartAttempt after the
  cause is corrected.

## PostgreSQL slot retention and loss

`PostgresLogicalReplicationMaterializationChangeSource.InspectHealthAsync` is the primary typed health surface.
Alert on inactivity, retained bytes, pending WAL, remaining safe WAL, invalidation, and stable failure
classifications. Correlate this with deployment-owned `pg_replication_slots` monitoring.

Pause does not pause PostgreSQL writes or WAL retention. During a long pause, continue monitoring the slot and either
resume before the retention boundary is threatened or deliberately abandon the run and establish a new baseline.
Never advance `confirmed_flush_lsn` operationally to relieve disk pressure unless the corresponding target effects
and Cohesive application checkpoint are already durable.

If a slot is missing, invalidated, recreated, belongs to another server/database/publication, or no longer retains
the requested position:

1. Stop reads and settlement for that source scope.
2. Preserve the typed failure, last application checkpoint, settlement receipt, slot health, and physical slot
   evidence.
3. Do not replace the retained position with `CaptureCurrentPositionAsync` and continue the same generation; that
   would create an unproven gap.
4. Permanently abandon a non-active candidate. If the affected generation is currently active, mark it degraded,
   stop incremental advancement, build a replacement, and route traffic away before retirement; target abandonment
   must not be used against an active generation.
5. Reprovision or adopt an exact slot, rotate `SlotGeneration` for a recreated physical slot, and start a new
   baseline in a fresh index generation.

### Ordinary bootstrap and exported-snapshot boundary

The generic PostgreSQL path uses stable keyset enumeration with request-local statement snapshots. It captures a
logical-replication cut before scanning and catches up complete retained mutations after that cut. This is a
baseline-plus-catch-up and reconciliation guarantee; it is not a coordinated cross-page MVCC snapshot claim.

`PostgresLogicalReplicationBaselineHandoff` separately supports creating a new permanent slot with an exported
snapshot, an exact `ChangeStartPosition`, and a paired change source. The current generic rebuild-plan resolver and
executor do not yet compose that handoff's baseline profile and run-scoped initial position as one end-to-end
realization. Treat exported-snapshot bootstrap as a follow-up integration, not as an ARI-180 guarantee. Do not pass
the handoff source and ordinary change source into a plan whose pinned source-profile invariants they do not satisfy.

## Target promotion, pool swap, rollback, and cleanup

Target-local activation and backend routing protect different boundaries:

- `MaterializationGenerationActivationExecutor` converges, seals, validates, and promotes one generation on one
  `IMaterializationTarget`. For Elasticsearch, promotion atomically moves that target binding's stable read alias.
- `IMaterializationBackendRouter` selects concrete target dependencies for newly admitted reads and writes under one
  exact placement slice. `InspectAsync`, `ResolveReadAsync`, and `ResolveWriteAsync` require that slice explicitly;
  every mutation header, proof, receipt, and snapshot retains it. Revision, fence, idempotency, lifecycle roles, and
  route state are isolated by the slice fingerprint rather than shared across the backend pool.
- `MaterializationIndependentPromotionExecutor` is the current high-level realization for a plan set whose promotion
  mode is `Independent`. Its strict durable request binds the plan-set reference, leaf-plan reference, full slice,
  active-generation evidence, pre-admission revision, fence, two command identities, and issuance times.

Use this lifecycle for each independently promoted leaf:

1. **Activate the target generation.** Complete catch-up, seal, validation, and target-local promotion. Persist the
   placement-bound active-generation evidence.
2. **Inspect the exact placement slice.** Retain its pre-admission revision and select an ownership fence. A snapshot,
   revision, or fence from another slice is not interchangeable, even when pool and target IDs match.
3. **Persist independent promotion intent.** Serialize the exact `MaterializationIndependentPromotionRequest` before
   routing I/O. On recovery, submit those same bytes and command identities.
4. **Admit and swap.** The independent executor admits the activated generation as that slice's candidate and then
   atomically switches its paired read/write routes. Candidate admission alone is not visibility. Low-level callers
   may issue the equivalent `AdmitCandidateAsync` and `SwapAsync` commands, but must carry the same exact slice and
   must not weaken the plan set's declared promotion mode. A low-level migration may retain readable activation
   evidence from an earlier target or membership cut only when the materialization, exact pool definition, and
   canonical placement-subject set are unchanged. A changed subject set needs explicit placement-transition or
   coverage evidence; it is never inferred from matching target IDs.
5. **Drain displaced generations.** The swap closes new admissions for this slice and records its exact routing
   revision for each displaced generation. Wait for slice-scoped in-flight work to reach zero and submit a
   `MaterializationBackendDrainProof` bound to that slice and admission revision.
6. **Rollback if needed.** Roll back only while the prior generation remains physically retained and target-locally
   readable, and only with a current-revision `MaterializationBackendRollbackProof` establishing equivalence to the
   current routes under the same slice. The current Elasticsearch target promotes only a Validated candidate; it
   does not generically reactivate its Inactive predecessor. Same-target alias rollback therefore needs a separately
   supported target operation, while the reference pool rollback is directly usable when the prior backend still
   retains its own active readable generation.
7. **Retire from slice routing.** After exact quiescence, retire the generation's role under this placement slice.
   Routing retirement does not call the target lifecycle.
8. **Reserve routing exclusion.** Submit `MaterializationReserveBackendCleanupRequest` from one retired placement
   slice before deleting physical data. That router atomically proves that none of its placement slices still retains
   an active, candidate, or draining role; captures every exact slice-retirement revision it owns; and installs a
   terminal reservation that prevents the generation from being admitted or routed there again. Persist the returned
   reservation and token. If another pool, pool-definition version, or router can reference the same physical
   generation, the cleanup coordinator must collect an equivalent reservation from each authority; one router's
   reservation is not global deletion authority.
9. **Retire and clean the target.** Issue exact fenced target retirement and cleanup operations only after the target
   itself reports the generation non-active and every required routing-authority reservation has committed. Slice retirement does
   not deactivate a target-local generation. If a removed backend still considers that generation Active, retain it
   until another target-local lifecycle operation safely displaces it; the current generic target contract has no
   standalone deactivation command.
10. **Record every slice acknowledgement.** Submit `MaterializationBackendCleanupProof` derived from the same
    reservation token and adapter-owned physical-cleanup receipt for each captured slice-retirement claim. Retain
    every slice's cleanup tombstone so the generation cannot be readmitted under the old identity. A crash between
    physical deletion and the final acknowledgement replays the retained reservation and proof; it does not reserve
    or delete a different generation.

An Elasticsearch alias swap is atomic for that target, but it does not provide a stable multi-request search view.
Do not infer PIT-like consistency for paged Relations queries. Backend-pool routing likewise controls admission; it
does not cancel requests admitted under a prior revision. A revision from one placement slice says nothing about the
revision or ownership fence of another slice.

## Current provider and runtime limitations

- The repository currently ships in-memory reference stores and routing authorities; production durability and
  distributed ownership require concrete adapters that pass the same crash, CAS, fencing, and replay matrices.
- The in-memory router uses one process-local linearization gate across placement scopes, including target
  inspection. This preserves the atomic cross-slice cleanup proof but is not a capacity scheduler or a production
  concurrency model. A durable router should combine slice-local mutation serialization with an authoritative
  generation-reference index so cleanup reservation remains atomic without coupling unrelated target I/O.
- Canonical requests, complete frozen membership, subject-to-target placement, bounded scheduling realizations,
  placement-bound leaf plans, linked plan sets, and declared promotion/partial-failure policies are implemented.
  `Independent` leaf visibility has a durable reference executor. A durable parent scheduler and the coordination
  interpreters for `AllReadyProgressive` and `AtomicVisibility` remain to be implemented; declarations of those modes
  are not execution evidence.
- End-to-end Cosmos DB/PostgreSQL-to-Elasticsearch support is established only by the focused ARI-180 conformance
  suite. Independent compiler, source, synchronization, target, and router tests are not a substitute.
- Cosmos pull positions and managed-processor leases are distinct. The full-fidelity pull source does not settle a
  provider lease; the managed processor checkpoints only after an applied/replayed Cohesive checkpoint.
- The current Cosmos managed processor is latest-version upsert delivery. It does not claim deletes, previous
  images, or all-versions-and-deletes semantics because the installed SDK does not expose manual checkpointing for
  that mode.
- Neither the ordinary Cosmos nor PostgreSQL paged baseline claims a cross-page coordinated snapshot. Their supported
  path is baseline plus retained catch-up.
- The generic PostgreSQL executor does not yet consume the exported-snapshot handoff as its initial cut.
- The Elasticsearch stable alias does not provide a stable multi-request view; PIT-backed leases remain deferred.
- The generic target lifecycle has no standalone deactivation or reactivation command. Pool rollback requires an
  already-readable target generation; same-target Elasticsearch alias rollback is not inferred from retained
  Inactive generation data.
- The current control API and in-memory router are reference surfaces. A deployment must add authentication,
  authorization, durable stores, leader/worker ownership, and operational availability without weakening the typed
  command contracts.

## Verification commands

Run the deterministic adapter-backed acceptance suite:

```bash
./eng/test-index-sync-vertical-slices.sh
```

Run real PostgreSQL integration tests only against a disposable database:

```bash
COHESIVE_POSTGRES_TEST_CONNECTION_STRING='...' \
  ./eng/test-postgres-integration.sh

COHESIVE_POSTGRES_LOGICAL_REPLICATION_TEST_CONNECTION_STRING='...' \
  ./eng/test-postgres-logical-replication-integration.sh
```

The logical-replication suite creates and removes schemas, publications, and permanent slots. The deterministic
acceptance suite must remain the normal CI gate; provider integration is additional deployment evidence.
