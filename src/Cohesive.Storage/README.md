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
the abandoned attempt's immutable activation, inbox, outbox, host-operation, and durable-operation evidence.
Restore validation also proves exact wait topology and bidirectional closure between execution traces, exact
occurrence-keyed host-operation receipts, fingerprinted outbox envelopes, outstanding Requests, and
Request-operation state before any host operation can execute. Restored Fork and Join evidence must retain its
derived occurrence identities, policy-shaped completion history, canonical selected branches, and coherent
resolved state. Cached host-operation results form a closed union: either a typed successful value with optional
emissions, or one error diagnostic with no emissions.

`IProcessDurableStore` persists that aggregate under one atomic contract:

- an expected `ProcessStorageRevision` provides physical compare-and-swap;
- a leased `ProcessWorkerFence` makes an expired, not-yet-acquired, or superseded activation owner stale, and
  store observations cannot predate retained aggregate or lease-renewal evidence;
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

The same facilities can be registered with `IServiceCollection` through `RegisterEntityRelationQuerySource` and
`RegisterEntityRelationQueryEvaluator`. Registration order does not choose a source: the v1 catalog permits exactly
one source per graph-qualified shape and rejects duplicate shape or source identities.

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
