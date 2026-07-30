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

## Canonical relation/query sources

Storage contributes physical acquisition to `Cohesive.Relations`; it does not define another predicate, join,
projection, aggregation, or paging model. Register an exact graph-qualified entity shape with its canonical source
instance, reader, selectors, capability profile, and limits. The immutable catalog then authors plan-affine
placement and constructs the existing canonical evaluator:

```csharp
var source = EntityRelationQuerySourceRegistration.InMemory(
    loadShape,
    loadRepository,
    limits: new(
        maximumBatchSize: 100,
        maximumBufferedRows: 10_000,
        maximumFanOut: 100,
        maximumConcurrency: 4));

var catalog = new EntityRelationQuerySourceCatalog([source]);
IRelationQueryEvaluator evaluator = catalog.CreateEvaluator(physicalPlanningPolicy);
var outcome = await evaluator.EvaluateAsync(evaluation, cancellationToken);
```

The in-memory reader supports bounded enumeration, identity batches, relationship-reference batches, exact field
selection, authoritative absence, partial/inconclusive evidence, and cancellation. Canonical interpretation owns
filters, joins, output shaping, aggregation, and paging. Query source roots are read from registered sources;
relation roots remain invocation inputs and must be supplied by the evaluation.

`Cohesive.Adapters.Postgres` supplies a production Npgsql-backed implementation of the same source-reader port.
`PostgresRelationQuerySourceReader` uses the exact persisted PostgreSQL binding for bounded enumeration, point/batch
identity reads, and set-oriented relationship-key predicate batches. Provider types remain in the adapter package;
the Storage and Relations ports remain backend-neutral.

The same facilities can be registered with `IServiceCollection` through `RegisterEntityRelationQuerySource` and
`RegisterEntityRelationQueryEvaluator`. Registration order does not choose a source: the v1 catalog permits exactly
one source per graph-qualified shape and rejects duplicate shape or source identities.

## Relation-derived materialization

`Cohesive.Storage.Materialization` defines one backend-neutral contract for rebuild and incremental synchronization.
A `MaterializationDocument` persists the exact `RelationQueryCompilationRequest`, its compiled-plan fingerprint, and
the selected output under the `cohesive-materialization/v1` schema. Loading recompiles that request and fails closed
if the plan, output, or acquisition-source contract has drifted. The compiled Relations requirement graph,
dependency manifest, and lineage remain the sole dependency authorities; Storage does not copy their edges.
Definition validation covers both source-set and relationship-traversal acquisitions, deriving bounded enumeration,
batched identity lookup, or parameterized predicate requirements from the canonical Relations acquisition kind.

Definitions declare source and target capability requirements by synchronization mode. Requirements include hard
item/byte/concurrency limits and semantic guarantees such as stable complete enumeration, at-least-once delivery,
explicit settlement, fenced idempotent versioned writes, generation isolation, exact per-item outcomes, and atomic fenced
promotion. `MaterializationCapabilityMatcher` resolves those requirements against attributable adapter evidence and
returns structured diagnostics instead of weakening a guarantee.

Capability matching establishes whether a binding can satisfy the declared consistency strategy; it is not proof
that a particular run acquired a coordinated snapshot or baseline/change-feed cut. The later execution planner must
persist the concrete run-scoped snapshot, feed-position, and retention evidence before it authorizes work.

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
  read fingerprint, and authoritative complete/not-found Relations evidence instead of an unattributed completion
  flag.
- `MaterializationSourceSettlement` records a source acknowledgement. The engine must first persist its application
  checkpoint, then call `IMaterializationSettlingSource`, and finally persist the returned receipt.

`IMaterializationProgressStore` returns a bounded snapshot containing only the latest checkpoint and settlement.
Implementations may retain additional idempotency and audit evidence internally without making unbounded history part
of the core port.

`IMaterializationTarget` accepts fenced, idempotent, version-aware mutations for an isolated candidate during rebuild
and for the active generation during incremental maintenance. Batches return one keyed outcome per input so retries
can contain only failed items. An unresolved retryable outcome is retained by exact mutation identity, version, and
content fingerprint, so unrelated work for the same item cannot make a candidate appear complete. A candidate is
sealed, validated, and then promoted through an active-pointer compare-and-swap fence that is distinct from its
generation worker fence. Promotion makes the previous active generation inactive and records the displacement boundary;
retirement remains a separate policy operation, and physical cleanup permanently tombstones the caller-assigned
generation identity so it cannot be reused. Ordinary target snapshots expose bounded lifecycle metadata rather than all
materialized items. Pause and Continue retain the same generation, while a Process restart creates a fresh generation. `InMemoryMaterializationSource`,
`InMemoryMaterializationProgressStore`, and `InMemoryMaterializationTarget` are deterministic reference fakes for adapter
and engine conformance tests, not production durability implementations.

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

- typed fixed-point observations for CPU, memory, latency, throughput, rejection, lag, and backpressure;
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
var state = AimdControlState.Create(definition, new ControlEpochId("index-generation-42"), now);
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

- `Cohesive.Transitions` for entity state and transition models.
- `Cohesive.Relations` for canonical relation/query semantics, evaluation, placement, and source-reader contracts.
- `Cohesive.Processes` for canonical Process IR, continuation state, validation, and reference interpretation.
- `Cohesive.Adapters.Cosmos` for Cosmos DB-backed storage.
- `Cohesive.Adapters.Postgres` for Npgsql-backed canonical Relations acquisition and rebuild/reconciliation sources.
