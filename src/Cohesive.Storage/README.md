# Cohesive.Storage

Provider-neutral storage abstractions for entity repositories, observation streams, outbox records, seeding, and process repository adapters.

## Install

```bash
dotnet add package Cohesive.Storage
```

## Use When

- You need repository contracts for Cohesive entities and observations.
- You want storage behavior to attach to semantic entity and relation models without binding application code to a database SDK.
- You need adapters between entity snapshots, observation records, canonical relation/query source readers, and process execution.

## Durable Process aggregates

`Cohesive.Storage.Processes` defines the physical durability boundary for canonical Process execution. A
`ProcessDurableCheckpoint` composes the existing semantic authorities—start receipt, complete multi-token
continuation, lifecycle control, activation and host-operation receipts, durable inbox, interaction outbox, and
durable Request-operation state—without introducing parallel copies of their fields.

Activation receipts are attempt-scoped and contiguous within an attempt. Each receipt fingerprints the complete
continuation before and after its activation: the first receipt begins at the canonical clean start or restart,
adjacent receipts form an exact chain, and the current attempt's final fingerprint names the checkpoint
continuation. A Control-authorized restart therefore creates a clean zero-activation continuation while retaining
the abandoned attempt's immutable activation, inbox, outbox, host-operation, and durable-operation evidence;
pending or Buffered inbox entries are atomically closed as Stale under the abandoned continuation.
Restore validation also proves exact wait topology and bidirectional closure between execution traces, exact
occurrence-keyed host-operation receipts, fingerprinted outbox envelopes, outstanding Requests, and
Request-operation state before any host operation can execute. Restored Fork and Join evidence must retain its
derived occurrence identities, policy-shaped completion history, canonical selected branches, and coherent
resolved state. Cached host-operation results form a closed union: either a typed successful value with optional
emissions, or one error diagnostic with no emissions. Every host-operation emission must carry the exact Process
attempt, activation, token, node, and operation-kind provenance, and every outbox entry has exactly one producer.

`IProcessDurableStore` persists that aggregate under one atomic contract:

- an expected `ProcessStorageRevision` provides physical compare-and-swap;
- a leased `ProcessWorkerFence` makes an expired, not-yet-acquired, or superseded activation owner stale, and
  store observations cannot predate retained aggregate or lease-renewal evidence; providers evaluate lease
  liveness from fresh physical commit-boundary time rather than caller-retained semantic timestamps;
- `ProcessCommitId` plus the deterministic commit fingerprint makes ambiguous exact retries replayable and rejects
  conflicting identity reuse;
- inbox admission is durable without a live worker and advances the same revision as checkpoint commits, preventing
  a registration/commit race from losing an early input; and
- checkpoint replacement and eligible provider-neutral local mutations commit all-or-none.

Inbox disposition is attributable to the Process attempt that decided it and remains a projection of canonical
continuation receipts. A pending entry may be buffered and a buffered entry may reach one terminal disposition;
committed terminal evidence cannot be rewritten. Late input is still admitted after Process completion so the
canonical interpreter can apply authored late/stale/observe/reject/dead-letter policy. Physical publication and
durable-operation histories likewise append attempts while permitting only legal monotonic advancement of the
latest attempt snapshot.

`InMemoryProcessDurableStore` is the copy-on-write reference implementation and semantic test oracle. It exposes
fault-injection cuts before and after initialization, inbox admission, worker acquisition, worker renewal, and
aggregate commit. It is not a production durability provider and does not claim physical exactly-once external
publication. `ProcessCheckpointCompatibilityValidator` must admit a restored checkpoint against the exact compiled
Process definition before any host operation executes.

`ProcessDurableRuntime` is the Storage-owned reference driver over that boundary. It composes the canonical
Process interpreter, lifecycle-control reducer, durable Request executor, and store without becoming another
semantic authority. Initialization, activation, control, affinity binding, cancellation, and durable-operation
advancement all validate the exact pinned definition before acquiring a worker. Worker acquisition and aggregate
commit use bounded retries of one unchanged physical intent when a provider classifies the outcome as ambiguous;
changed identities or content are never substituted during reconciliation. Cancellation propagates directly only
when the caller token is cancelled; provider-local cancellation and timeout exceptions with a live caller token
pass through the configured store-mutation ambiguity classifier.

A finite activation restores the complete continuation, replays exact committed Transition or Relation/Query
host-operation receipts, captures first-time observations, and commits its continuation, control safe point,
inbox dispositions, outbox emissions, durable Request states, and eligible local mutations as one aggregate
successor. Compatibility failure, a stale expected attempt, pause, or terminal state prevents host execution.
Host-operation ports must therefore be deterministic for an exact occurrence; externally impure or long-running
work belongs behind a canonical durable Request rather than a synchronous host call.

An optional asynchronous entity Transition adapter composes that finite interpreter with the entity repository's
existing atomic state-and-receipt protocol. An unmaterialized Transition suspends interpretation, commits or replays
the exact entity handoff, and restarts against the same activation-local observation cache. The Process aggregate
then admits the Transition result, occurrence receipt, continuation, and canonical envelopes in one commit. Entity
receipts are durable handoff evidence; the Process outbox remains the sole publication authority.

A Transition with canonical subject-creation semantics uses the same adapter and Process occurrence. The adapter
requires authoritative absence, derives and validates the complete version-zero state against the repository's
entity definition, and commits that state plus the exact operation receipt atomically under `MustBeAbsent`. A
present subject or a subject created after the initial read fails without replacement. Exact retries replay the
retained receipt; changed operation content conflicts. Update-only Transitions retain `MustExist` plus optimistic
concurrency, and a missing subject remains a structured rejection. Transition emissions cross the entity boundary
only as receipt handoff evidence—the Process outbox remains publication authority.

Durable Request advancement persists claim and dispatch evidence before adapter I/O, reloads and reacquires the
aggregate before accepting returned evidence, and persists acknowledgement separately from target admission. A
crash after dispatch can repeat only the same fenced invocation and stable target idempotency identity when the
binding proves replay safe; otherwise the authored reconciliation, escalation, or terminal-outcome requirement
remains explicit. A durable acknowledgement skips further adapter execution, and its Reply admission is an atomic
operation-state/inbox cut. While external adapter work is in flight, the driver releases its aggregate critical
section but retains a per-operation single-flight guard, renews the Process worker and operation claim without
changing either fence, and reloads the latest revision before accepting evidence. Pause and Continue retain the
current attempt and its affinities. Pause prevents new dispatch, redispatch, and reconciliation, while work already
admitted before the pause may reach only its legal monotonic durable cut. RestartAttempt creates one clean
replacement continuation under the accepted replacement attempt identity and atomically classifies every pending
or buffered pre-cut input as stale under the abandoned continuation. Exact replay returns the retained decision;
closed attempts cannot add new logical evidence or mint another physical operation attempt, while an already
retained claimed or dispatched attempt may advance monotonically under its exact identity and fence. An ordinary
completed or failed continuation closes the current attempt for new work even when Control remains in Running mode.
Only cancellation tied to the caller's cancellation token propagates as caller cancellation. Provider-local
timeouts or cancellation exceptions are classified as post-dispatch failure evidence; reconciliation exceptions
retain an unresolved observation and follow the Request's authored recovery policy.

Cooperative cancellation requested while the driver is already at a retained safe boundary uses the applied
Cancel receipt itself as the terminal durable cut and commits the cancelled continuation in the same aggregate
mutation. The canonical Control protocol still defers cancellation observed during an in-flight activation to its
next safe point; the reference driver does not persist incomplete activation frames. Forced Terminate remains an
explicit control semantic but does not yet have a Storage-owned continuation-composition method. The current
operation driver returns authored timeout, cancellation, escalation, and terminal-outcome requirements to its
caller but does not yet expose a command that durably applies caller-supplied resolutions. General classification
of inputs admitted only after terminal commit is likewise a subsequent runtime surface; late operation results are
dispositioned before Reply admission, and every input pending at cooperative cancellation is classified in that
terminal cut.

### Operational explanations

`ProcessDurableExecutionExplainProjector` composes an existing Process compilation with a canonical durable
checkpoint. It reuses the common status and normalized-trace projectors, then adds payload-free active-wait
registrations, exact authored interaction contracts and timers, attempt/token/node lineage, and a structured
`process.wait.inputRequired` diagnostic. The Motion DQ durable conformance fixture is the reference case: a blocked
caseworker review identifies the exact registration and the evidence that can resolve it without disclosing the
onboarding case or application payload.

`MaterializationIndexSyncExecutionExplainProjector` adds the existing typed index-sync status extension and
Storage-owned explain evidence to that same artifact. Routing, source-feed progress, backlog, lag, generation
health, measured Control pressure, retained recommendation, and effective operating point continue to come from
their canonical Storage and Control values. Congestion emits the structured
`materialization.indexSync.throttled` diagnostic with safe next actions. Both projectors return
`ExecutionExplainArtifact`, so API, CLI, tests, and documentation can serialize one common contract through
`ExecutionExplainJsonSerializer`; runtime observations remain marked as measured and cannot silently change the
authored definition.

## Canonical relation/query sources

Storage contributes physical acquisition to `Cohesive.Relations`; it does not define another predicate, join,
projection, aggregation, or paging model. Register an exact graph-qualified entity shape with its canonical source
instance, reader, selectors, capability profile, and limits. The immutable catalog then authors plan-affine
placement and constructs the existing canonical evaluator:

```csharp
var source = EntityRelationQuerySourceRegistration.InMemory(
    loadShape,
    loadRepository,
    logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
    observationVersionSemanticPath: FieldPath.FromField("SourceEntityVersion"),
    limits: new(
        maximumBatchSize: 100,
        maximumBufferedRows: 10_000,
        maximumFanOut: 100,
        maximumConcurrency: 4));

var catalog = new EntityRelationQuerySourceCatalog([source]);
IRelationQueryEvaluator evaluator = catalog.CreateEvaluator(physicalPlanningPolicy);
var outcome = await evaluator.EvaluateAsync(evaluation, cancellationToken);
```

When `observationVersionSemanticPath` is configured, that semantic field is projected from the repository
snapshot's `Observation.Version`; a same-named payload field cannot become a competing authority. The convention is
part of the derived source identity. The in-memory reader supports bounded enumeration, identity batches,
relationship-reference batches, exact field selection, authoritative absence, partial/inconclusive evidence, and
cancellation. Canonical interpretation owns
filters, joins, output shaping, aggregation, and paging. Query source roots are read from registered sources;
relation roots remain invocation inputs and must be supplied by the evaluation.

Every reader and supplied root in one evaluation declares the same provider-neutral logical partition before I/O.
Materialization source scopes retain that identity separately from adapter-defined feed partitions. Because logical
partition evidence is now fingerprint-significant, Channel projections use `materialization-channel-scope:v2` and
`materialization-channel-settlement:v2`; persisted v1 identities must not be mixed with v2 progress.

`Cohesive.Adapters.Postgres` supplies a production Npgsql-backed implementation of the same source-reader port.
`PostgresRelationQuerySourceReader` uses the exact persisted PostgreSQL binding for bounded enumeration, point/batch
identity reads, and set-oriented relationship-key predicate batches. Provider types remain in the adapter package;
the Storage and Relations ports remain backend-neutral.

The same facilities can be registered with `IServiceCollection` through `RegisterEntityRelationQuerySource` and
`RegisterEntityRelationQueryEvaluator`. Registration order does not choose a source: the v1 catalog permits exactly
one source per graph-qualified shape and rejects duplicate shape or source identities.

## Canonical aggregate storage realization

`Cohesive.Storage.Realization` separates canonical aggregate structure from its physical interpretation. A
`StorageStructureDefinition` retains the exact `ShapeGraphDocument` as its field and type authority and adds only
the storage semantics absent from that graph: the independently governed root, root identity, inherited logical
partition, and owned ordered collections. An owned component has a stable root-local identity and ordinal, but no
independent tenant scope or lifecycle.

The same structure can be interpreted by multiple adapters without changing its structure fingerprint:

- `StorageEmbeddedOwnedCollectionRealization` declares in-document expansion, single-document atomicity, and
  root-document change attribution; and
- `StorageDecomposedOwnedCollectionRealization` declares bounded root-correlated component acquisition,
  transaction atomicity across records, and component-parent change attribution.

These alternatives describe semantic guarantees rather than duplicate a provider schema. Physical container,
table, field, and column catalogs remain adapter authorities; realizations retain stable references or fingerprints
to that adapter-owned binding evidence. `StorageRealizationDocument` fences the complete semantic structure and one
target interpretation with independent deterministic fingerprints. Strict JSON loading recompiles neither side:
it validates the retained shape graph, owned paths, complete target coverage, linkage, and both fingerprints before
the document is admitted. `StorageRealizationExplainProjector` exposes the effective semantic paths, target
strategy, guarantees, change attribution, and adapter evidence for review and tooling.

`PostgresStorageRealizationCompiler` and `CosmosStorageRealizationCompiler` are the official adapter interpretations.
Both consume the same canonical structure and their existing relation/query storage-binding authorities. PostgreSQL
requires a root page bounded before a tenant-and-parent-correlated component join, exact component field coverage,
ordinal evidence, transaction atomicity, and component-to-parent change attribution. Cosmos requires an embedded
structured-array binding, canonical ordinal array order, complete child coverage, single-document atomicity, and
root-document change attribution. Either compiler returns a `StorageRealizationCompilationResult`; capability or
binding gaps are structured diagnostics and never mutate the canonical structure to fit the target.

## Relation-derived materialization

`Cohesive.Storage.Materialization` defines one backend-neutral contract for rebuild and incremental synchronization.
A `MaterializationDocument` persists the exact `RelationQueryCompilationRequest`, its compiled-plan fingerprint, and
the selected output under the `cohesive-materialization/v2` schema. Loading recompiles that request and fails closed
if the plan, output, or acquisition-source contract has drifted. The compiled Relations requirement graph,
dependency manifest, and lineage remain the sole dependency authorities; Storage does not copy their edges.
Definition validation covers both source-set and relationship-traversal acquisitions, deriving bounded enumeration,
batched identity lookup, or parameterized predicate requirements from the canonical Relations acquisition kind.

`MaterializationImpactPlanCompiler` projects that canonical dependency manifest into a fingerprinted
`cohesive-materialization-impact-plan/v1` execution template. Routes retain only canonical Relations input and
relationship identities; they never copy a relationship definition, dependency edge, effect, or provenance trace.
`MaterializationImpactPlanLinker` must reproduce a persisted plan from the exact materialization definition before
interpretation, and the strict JSON loader performs this definition-bound link automatically. Explain projections
dereference route identities back to the original manifest entries, relationship inputs, and capability requirements.

The v1 impact strategies are deliberately closed and bounded:

- a non-set relation-root change maps exactly to that root;
- contributor changes may follow canonical relationships toward roots using complete parameterized predicate reads
  or before/after relationship references;
- a contributor ledger is exact only as the explicit union of complete prior associations and roots resolved from
  current canonical relationship state; and
- bounded global invalidation is the only conservative strategy and always enumerates the complete admitted root set.

Set-valued outputs cannot use root-local or ledger-local exactness. Unsupported relationship paths, missing before
images, absent capabilities, and insufficient item/byte bounds fail compilation unless bounded global invalidation
is explicitly permitted. A runtime must fail an operation that exceeds a compiled bound; it must never truncate an
affected-root set and advance progress. Auxiliary predicate and root-enumeration reads inherit the definition's
ordering, request-completeness, and coordinated-snapshot or reconciliation guarantees; an impact route cannot weaken
the materialization consistency contract.

A predicate impact step is a portable semantic read template. Materialization realization binds an auxiliary
relationship-key lookup for the step's reference-bearing source role using its canonical relationship input and then
proves the referenced source capability; the original relation-query physical plan is not assumed to already contain
that reverse placement.

`RelationQueryMaterializationRebuildHydrator` and `RelationQueryMaterializationImpactRuntime` share one exact
Relations physical-execution mechanism. The semantic plan, successful realization, physical plan, selected complete
output, and non-root readers are fingerprint-affine; capability evidence is projected from that exact realization.
Materialization v1 requires invocation parameters to be bound into the persisted definition rather than supplied as
ambient runtime state. Incremental hydration supplies the complete bounded current root set, correlates every output
through canonical root-occurrence provenance, and preserves the same zero-or-one-per-root projection invariant as
baseline hydration. Direct-root selection remains part of the impact-plan interpreter. Every inverse route requires
an explicit `MaterializationImpactRootResolver` binding; Storage never infers a provider query from the portable
impact IR. Hydration evaluation identities are fenced by impact-plan, generation, channel scope, feed, and opaque
through-position evidence so replay is stable without allowing evidence from another generation to alias it.

Deployment and incident procedures for this lifecycle are collected in the
[index synchronization operations runbook](../../docs/INDEX_SYNC_RUNBOOK.md).

Contributor-ledger keys use materialization, generation, definition, impact-plan, canonical input, semantic shape,
and stable contributor identity—not evaluation-local occurrence identity. Entries retain complete root associations
and prior emitted item identities so moves and deletes can remove stale outputs. Exact ledger capability additionally
requires association replacement and corresponding target item mutations to commit atomically before an application
checkpoint advances. The target-coordinated operation that realizes this declared capability belongs to incremental
execution; a separate best-effort dual-write ledger would not satisfy the contract.

`MaterializationImpactPlanCatalog` indexes independent plans by changed semantic shape without merging their routes,
allowing one entity change to fan out to several materializations or several roles under distinct definition,
generation, and plan fences.

Definitions declare source and target capability requirements by synchronization mode. Requirements include hard
item/byte/concurrency limits and semantic guarantees such as stable complete enumeration, at-least-once delivery,
explicit settlement, fenced idempotent versioned writes, generation isolation, exact per-item outcomes, and atomic fenced
promotion. `MaterializationCapabilityMatcher` resolves those requirements against attributable adapter evidence and
returns structured diagnostics instead of weakening a guarantee.

The persisted `cohesive-materialization-rebuild-plan/v6` realization also contains the deterministic result of
compiling explicitly workload-bound Control loops against those plan and adapter limits. A generation-scoped
`MaterializationIndexSyncControlRuntime` owns one durable CAS state per exact materialization, effective Control
definition, plan, physical target, generation, workload, and loop. Source, transform, and target operating points
become effective only at their declared batch or work-admission safe points. Pause and continue retain the same
generation and Control epoch; Process restart uses a new generation and therefore a fresh epoch. Shared admission is
non-preemptive and realtime-first, and rebuild work cannot consume explicitly reserved realtime capacity even while
that capacity is idle. Target batching rereads applied bounds at every batch boundary, deterministically rechunks
pending mutations, and retries only the retryable rejected subset; already applied items are never resubmitted.

Change-delivery evidence may omit item or byte maxima when a provider exposes only advisory callback hints. Such
evidence can satisfy an unbounded managed-execution requirement but cannot satisfy a definition that requires hard
change-item or read-byte limits. Bounded pull sources continue to advertise and enforce their exact limits.
Definitions also distinguish `CompleteMutationDelivery`—every retained create, update, and delete without
latest-version coalescing—from `LatestVersionUpsertDelivery`, which promises only currently visible upserts. These
typed guarantees are not interchangeable during capability matching.

`CompleteCurrentObservation` separately proves that a delivered change carries the authoritative complete current
logical observation, not merely the physical row or component that emitted the signal. A native change image may
prove this directly. For aggregates split across physical structures,
`MaterializationCurrentStateEnrichmentCompiler` selects attributable change and bounded point-read evidence and
persists a `BatchedIdentityRead` plan on the direct-root feed. Its runtime deduplicates page identities, reads them in
bounded batches through the shared provider-neutral observation-read port, and preserves source delivery identity,
position, ordering, before-image, and settlement behavior. The resulting current state is explicitly
`ReconciledLatest`; it does not claim a coordinated snapshot with the earlier change position. Failed reads produce
no enriched page, checkpoint, or settlement, and replay repeats the same stateless composition.

Capability matching establishes whether a binding can satisfy the declared consistency strategy; it is not proof
that a particular run acquired a coordinated snapshot or baseline/change-feed cut. The later execution planner must
persist the concrete run-scoped snapshot, feed-position, and retention evidence before it authorizes work.

The outer rebuild-planning chain is canonical and durable. `MaterializationRebuildRequestDocument` retains the exact
materialization, subject selection, pinned backend pool, scheduling demand, and promotion guarantee.
`MaterializationRebuildPlanSetCompiler` freezes complete membership at an attributable cut and compiles explicit
subject-to-target placement with separate physical-capacity evidence. `MaterializationRebuildPlanSetLinker` then
requires one exact leaf per placement slice and produces a fingerprinted `MaterializationRebuildPlanSet` containing
the effective schedule and declared promotion/partial-failure policy. Linking and replay reject changed membership,
pool, target, subjects, slice fingerprint, leaf content, or promotion semantics; slice identity alone is never
sufficient authority.

`MaterializationRebuildPlan` is the one-target realization consumed by the reference baseline interpreter. Its strict
fingerprinted document revalidates and pins the complete materialization IR, exact source and target capability
matches, its exact independently promoted placement slice, stable root shards, each canonical scan request, each
hydration physical-plan fingerprint, and finite page, bulk, activation, parallelism, and cumulative per-shard bounds.
`MaterializationRebuildLeafExecutionAuthority` binds the exact plan-set reference, leaf-plan reference, and full
placement slice into the single durable authority used for execution and promotion. A resolved execution requires
the verified plan set itself and rejects a detached leaf before any target I/O; shard work and active-generation
evidence retain that same authority rather than independently recombining fingerprints. Runtime bindings must
reproduce all of that evidence;
a restart cannot silently select another Relations lowering or reader placement under the same plan. The v1 page
interpreter admits only `OnePerRoot` and `ZeroOrOnePerRoot` outputs. `Set` requires whole-set evaluation and
`ManyPerRoot` requires an explicit expansion bound, so both fail plan validation instead of weakening boundedness.
Canonical coordinator and worker Processes carry only exact linked-leaf authority and attempt-bound shard references. The Storage
interpreter creates an isolated Loading candidate, captures one change position per shard, hydrates each bounded
baseline page through the pinned canonical Relations realization, writes deterministic idempotent bulks, and
terminates at `baseline-complete/catch-up-required`. It does not seal, promote, or make that candidate readable.

`MaterializationRebuildProcessLifecycle` is the materialization-specific gate around the generic durable Process
runtime. It binds the attempt's deterministic generation affinity before physical candidate creation. Pause and
Continue preserve that affinity without target lifecycle I/O. Restart first commits or replays the replacement
Process attempt, then atomically abandons the old generation identity, binds the replacement affinity, and begins
exactly one replacement candidate. An absent old candidate receives a durable tombstone, closing the delayed-Begin
race; replay resumes the same post-commit steps.

The runtime ports keep three progress concepts separate:

- `MaterializationSourcePosition` is a versioned opaque provider cursor bound to one exact Relations physical plan,
  source-placement binding, partition, and ordering scope. `MaterializationSourceContinuation` additionally carries
  the fingerprint of the exact Relations read request, so a page cursor cannot be reused for another input, stage, or
  constraint set. Materialization page state is separate from Relations evidence completeness: an exhausted page may
  still report partial source evidence and therefore cannot produce completion proof. Change pages expose a
  page-level `ThroughPosition`, including for an empty caught-up cut, rather than inferring checkpoint progress from
  the last delivery.
- `MaterializationApplicationCheckpoint` records what a specific definition fingerprint and generation durably
  applied under compare-and-swap and a worker fence. A completed batch checkpoint retains the exact source scope,
  read fingerprint, authoritative complete/not-found Relations evidence, and one-based cumulative page ordinal
  instead of an unattributed completion flag. That ordinal is part of mutation identity and cannot exceed the
  persisted per-shard bound, including after crash recovery. Exhausted reads with `Partial`, `Failed`, or
  `Inconclusive` evidence stop before hydration and target I/O and cannot become completion proof.
- `MaterializationSourceSettlement` records a source acknowledgement. The engine must first persist its application
  checkpoint, then settle the source, and finally persist or emit the returned receipt. Pull sources may expose
  `IMaterializationSettlingSource` for an out-of-band acknowledgement. A managed source instead owns its
  callback-scoped acknowledgement and may invoke it only after the handler returns an applied or exact-replayed
  `MaterializationProgressMutationResult` whose change checkpoint covers the batch's exact through-position and
  delivery identities.

`IMaterializationChangeSource` is the common descriptor-bearing authority for change delivery.
`IMaterializationPullChangeSource` reads bounded pages from caller-owned positions without changing source state;
`IMaterializationManagedChangeSource` runs provider-managed delivery and retains the settlement operation inside the
adapter boundary. Both deliver `MaterializationChangePage` and `MaterializationChangeDelivery`; there is no second
observation-stream envelope. Provider lease, bookmark, consumer-group, and worker-owner identities are execution
evidence only. They are neither application checkpoints nor interchangeable with source positions.

A managed adapter must bind provider checkpoint ownership to the exact `MaterializationManagedChangeRequest`:
materialization identity, execution-definition fingerprint, and generation. Workers resuming that same request may
share provider ownership, but a new generation must receive an isolated provider checkpoint namespace so it cannot
begin after a prior generation's acknowledged input.

The former `IObservationStream`, `ObservationBatchContext`, `ObservationRecord`, and `IChangeStreamRepository`
contracts were removed. Entity/outbox repositories remain write-side persistence ports. Consumers migrate by
binding the persisted entity or outbox shape to a materialization change source and handling its canonical change
pages. The handler must apply effects and durably save the supplied progress key before returning its progress-store
result; callback success alone is not durable application evidence.

The invariant order is `apply effects → commit application checkpoint → settle source → record settlement
observation`. A crash after the application commit and before source settlement may redeliver the batch; stable
change and delivery identities plus an exact replayed checkpoint make that safe. A crash after source settlement
cannot expose uncommitted application work because settlement was not reachable before the durable proof.

`IMaterializationProgressStore` returns a bounded snapshot containing the latest baseline/batch checkpoint, the
latest incremental Channel checkpoint, and the latest settlement. The two checkpoint tracks advance independently:
baseline enumeration cannot overwrite the captured change cut, and change delivery cannot erase the baseline
continuation or completion proof. Implementations may retain additional idempotency and audit evidence internally
without making unbounded history part of the core port.

A crash after a successful bulk but before its checkpoint re-reads the exact same page identity. If the source now
produces different canonical target intent under that identity, the shard returns terminal `RestartRequired` with
source-replay-drift evidence and leaves the candidate Loading and unreadable. The worker never abandons or replaces
its own generation; external Control must issue `RestartAttempt`, after which the lifecycle protocol durably excludes
the old generation and allocates exactly one fresh generation.

`IMaterializationTarget` accepts fenced, idempotent, version-aware mutations for an isolated candidate during rebuild
and for the active generation during incremental maintenance. Batches return one keyed outcome per input so retries
can contain only failed items. An unresolved retryable outcome is retained by exact mutation identity, version, and
content fingerprint, so unrelated work for the same item cannot make a candidate appear complete. A candidate is
sealed, validated, and then promoted through an active-pointer compare-and-swap fence that is distinct from its
generation worker fence. Promotion makes the previous active generation inactive and records the displacement boundary;
retirement remains a separate policy operation. `AbandonGenerationAsync` atomically retires a non-active candidate
or installs a tombstone when physical generation state does not yet exist, so a delayed begin or write cannot revive
an abandoned Process attempt. Definitions that support rebuild therefore require the distinct
`TargetGenerationAbandonment` capability with `AtomicDurableGenerationExclusion`; ordinary `TargetRetirement` does
not satisfy that exclusion contract. Physical cleanup removes retained index data without removing that abandonment
claim.
Ordinary target snapshots expose bounded lifecycle metadata rather than all
materialized items. Pause and Continue retain the same generation, while a Process restart creates a fresh generation. `InMemoryMaterializationSource`,
`InMemoryMaterializationProgressStore`, and `InMemoryMaterializationTarget` are deterministic reference fakes for adapter
and engine conformance tests, not production durability implementations.

`IMaterializationBackendRouter` owns routing independently for every exact
`MaterializationPlacementSliceReference`. Inspect and read/write resolution require the full slice, and every routing
command, proof, snapshot, binding, and receipt retains it. Revisions, ownership fences, command idempotency, routes,
and lifecycle state are isolated by the slice fingerprint rather than shared by pool or slice ID. The router can
therefore expose two independently promoted slices from the same pool without letting one slice's command identity,
takeover fence, or route transition affect the other. A readable generation activated under an earlier target or
membership cut may initialize a newer slice only when both authorities retain the exact materialization, pool
definition, and canonical subject set.
Subject-set merges, splits, additions, or removals require an explicit future placement-transition/coverage proof;
the low-level router never infers that equivalence. Physical cleanup is a two-phase cross-slice protocol: each
router authority durably captures all of its retired placement claims and terminally excludes future admission before
the adapter deletes data; each captured slice then acknowledges the same reservation-bound physical proof
independently. A coordinator must aggregate reservations from every pool or router that can address shared physical
storage—one router's reservation alone is not cross-authority deletion permission.

`MaterializationIndependentPromotionExecutor` realizes the currently supported high-level plan-set promotion mode.
Its strict `MaterializationIndependentPromotionRequest` durably binds the exact plan set, placement-bound leaf, full
slice, active-generation evidence, pre-admission routing revision, fence, command identities, and timestamps. Exact
execution admits the activated candidate and atomically switches that slice's paired read/write routes; recovery
replays the same retained request. `AllReadyProgressive` and `AtomicVisibility` are canonical declared requirements,
but still need durable parent coordination interpreters before they can be executed without weakening their
readiness, partial-failure, compensation, or all-or-none guarantees.

Index-sync status schema `index-sync-status/v3` includes the placement-slice ID and complete fingerprint. Publish each
projection under `MaterializationIndexSyncStatusWireNames.PlacementStatusPath(slice)`, which includes materialization,
pool, slice identity, and fingerprint components. Do not collapse independently revisioned slices into one
pool-global status key or use slice ID without its fingerprint.

`PostgresMaterializationSource` is a production source-side binding for rebuild and reconciliation. It reuses the
canonical PostgreSQL Relations reader, applies explicit item and canonical encoded-byte page bounds, and resumes with
an opaque, size-bounded HMAC-authenticated keyset continuation over a UUID or ordering-proven ordinal-text identity.
The caller supplies and durably manages the continuation-authentication secret. Each page executes in a new PostgreSQL
statement snapshot. Its capability evidence therefore claims stable ordering, request-local completeness, and
reconciliation—not coordinated cross-page snapshot, change delivery, settlement, or target writes. Pause/resume
retains the continuation boundary but cannot retain an MVCC snapshot.

## Bounded lifecycle control

The `Cohesive.Control` namespace is incubated in this package. It defines portable regulation semantics without
embedding channels, semaphores, timers, CPU samplers, retry libraries, or target SDKs. A control loop combines:

- typed fixed-point observations for CPU, memory, latency, throughput, rejection, lag, backpressure, request-unit
  consumption, queue depth, and batch shape;
- explicit objective polarity, so high-pressure and low-pressure metrics cannot be silently inverted;
- attributable semantic, compiler, adapter, and deployment hard limits whose intersection cannot be overridden;
- item/byte batching, concurrency, item/byte rates, finite buffers, and reserved workload capacity;
- deterministic convention resolution with per-setting explicit/profile/adapter/default provenance;
- a pure AIMD reference reducer with hysteresis, healthy-evidence windows, cooldown, and minimum dwell;
- an exact definition-content fingerprint on state, recommendations, observations, and safe-point evidence; and
- a pending recommendation that becomes effective only at an authorized epoch/revision/fence application point of
  the actuator's required kind (work admission, batch, rate-window, or buffer admission).

The regulator receives an explicit UTC evaluation time and returns complete durable state:

```csharp
var state = ControlLoopState.Create(definition, new ControlEpochId("index-generation-42"), now);
var decision = AimdControlReferenceRegulator.Evaluate(definition, state, observation, now);

// The recommendation is still non-authoritative here. A Process or materialization runtime maps
// its invariant-preserving durable cut into this generic contract.
var result = AimdControlReferenceRegulator.Apply(
    definition,
    decision.State,
    applicationPoint,
    appliedAtUtc: now);
```

`ControlBoundedAdmission` is the corresponding mechanism-neutral admission interpretation. It checks selected
operating points without owning work: concurrency reductions drain existing work, batch boundaries retain the next
item, an oversized indivisible batch/buffer/rate candidate is unfulfillable rather than retried forever, and
temporary finite buffer/rate pressure defers rather than drops or reorders work.

Observation freshness is measured from the end of the measurement window, not envelope emission time. A pending
increase may be superseded by newer non-healthy evidence, and every recommendation expires when its supporting
window is no longer fresh at actual application. State validation requires revision-reachable transition evidence,
rederives retained classifications and AIMD steps, and validates current actuation receipts against the exact
safe-point authority. Workload-budget capacity is an exclusive per-loop allocation; a compiler or runtime must
arbitrate shared physical pools before creating each loop's budget. Replay identities are scoped to an exact loop,
definition, epoch, and revision. Every recommendation also carries a paired prior-actuation identity/revision fence,
or an explicit absence that denotes the definition's initial operating point. While that prior receipt remains in
current state, validation binds the fence directly to it. Once bounded state rolls forward, the runtime's durable
ledger must resolve the fence to the exact immutable latest preceding receipt in the same loop, target, epoch, and
definition, with the stated post-actuation revision and proposed operating point; a missing fence asserts that no
preceding actuation exists. Pure local validators do not consult this ledger. The same ledger owns arbitrary-history
replay identities.

`ControlJsonSerializer` persists definitions, observations, controller state, decisions, application points, and
actuation receipts using strict case-sensitive canonical JSON. Unknown or duplicate properties, wrong scalar
encodings, unsupported schemas, and noncanonical collection order are rejected.

## Query authority

`Cohesive.Relations` canonical relation/query IR is the sole authority for predicates, joins, projections,
aggregations, and paging. Storage repositories retain point reads, writes, typed object mapping, and atomic outbox
behavior; they do not expose a parallel entity-query contract. Query consumers author a `RelationQueryEvaluation`,
register explicit `IRelationQuerySourceReader` implementations through `EntityRelationQuerySourceRegistration`, and
execute through `IRelationQueryEvaluator` or a target's canonical artifact executor.

The former query-repository compatibility facade and observation adapters were removed intentionally. Storage does
not provide an automatic bridge from that deleted model to canonical relation/query definitions.

## Related Packages

- [Execution Kernel adoption and migration guide](../../docs/EXECUTION_KERNEL_GUIDE.md) for durable execution and materialization examples, ownership, and migration guidance.
- `Cohesive.Transitions` for entity state and transition models.
- `Cohesive.Relations` for canonical relation/query semantics, evaluation, placement, and source-reader contracts.
- `Cohesive.Processes` for canonical Process IR, continuation state, validation, and reference interpretation.
- `Cohesive.Adapters.Cosmos` for Cosmos DB-backed storage.
- `Cohesive.Adapters.Postgres` for Npgsql-backed canonical Relations acquisition and rebuild/reconciliation sources.
