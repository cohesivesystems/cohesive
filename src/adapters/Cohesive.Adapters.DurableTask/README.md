# Cohesive.Adapters.DurableTask

Azure Durable Task integration for current and migrated historical Process monitoring, realization planning, and
an executable bounded Process profile over the standalone Microsoft Durable Task SDK.

The former adapter executed callback-bearing Process definitions through a single-cursor checkpoint. ARI-170
retired that path because it could not preserve canonical Process semantics. The replacement keeps the exact
`CompiledProcessPlan` and `ProcessReferenceInterpreter` as semantic authority. A generic Durable Task orchestration
now executes the admitted bounded slice; activities invoke canonical Transition, Relation/Query, and Request host
operations, publish canonical domain events, and resolve Signal targets, sub-orchestrations execute child Processes,
and Durable Task owns physical scheduling, history, and replay.

The accepted execution direction is a parallel durable interpreter that consumes an exact `CompiledProcessPlan`
and uses Azure Durable Task Scheduler as physical execution evidence. It is not required to implement
`IProcessDurableStore`; it must preserve the same canonical semantics inside its declared capability closure and
pass differential conformance against the reference interpreter. See the accepted
[Durable Task Process interpreter decision](../../../docs/decisions/durable-task-process-interpreter.md).

## Install

```bash
dotnet add package Cohesive.Adapters.DurableTask
```

## Use When

- You need to query task-hub records created by the retired adapter during migration.
- You need an `IProcessExecutionRepository` monitoring projection over an existing Durable Task hub.
- You need to inspect whether an exact `CompiledProcessPlan` has a complete intended Durable Task realization.
- You need to execute an exact Process containing Transition, Relation/Query, Request, durable after-origin
  domain-event emission, Choice, Match, bounded Fork/Join, AwaitMatch, Timer, Signal send to a Process token, child
  Process, bounded partition, bounded recurrence, Durable Cut, Return, and Fail constructs.

The executable profile remains narrower than the complete planning profile. Reply emission nodes, non-Process Signal
targets, activation-local Signal delivery, lifecycle Signal qualification, atomic-with-origin event publication, and
complete provider cleanup/recovery semantics remain outside this slice and fail closed. Domain-event publication
requires durable after-origin visibility and a target that deduplicates the exact contract by the canonical scoped
key. Request dispatch, bounded retry, reconciliation, acknowledgement, and Reply admission are implemented; typed
timeout, terminal-failure, and escalation paths fail closed with their canonical operation ledger because this slice
does not fabricate the authored recovery outcome.

## Monitoring boundary

```csharp
using Cohesive.Adapters.DurableTask;

var currentRepository = new DurableTaskProcessExecutionRepository(
    client,
    taskHubName: "orders");

ProcessExecutionRecord? execution = await currentRepository.GetAsync(
    operationContext,
    trustedAuthorityScope,
    logicalProcessInstanceId);

ProcessExecutionTraceReadResult traceRead = await currentRepository.GetTracesAsync(
    operationContext,
    trustedAuthorityScope,
    logicalProcessInstanceId);

if (traceRead.Artifact is { IsComplete: true } complete)
{
    foreach (NormalizedExecutionTrace trace in complete.Traces)
    {
        // Render or serialize the shared canonical artifact.
    }
}

IProcessExecutionExplainRepository explainRepository =
    new DurableTaskProcessExecutionExplainRepository(currentRepository, deployedPlanCatalog);

ExecutionExplainArtifact? explanation = await explainRepository.GetExplainAsync(
    operationContext,
    trustedAuthorityScope,
    logicalProcessInstanceId);

// Migration-only reader for task hubs created by the retired Core adapter.
IProcessExecutionRepository historicalRepository = new DurableTaskProcessExecutionRepository(
    historicalQueryClient,
    taskHubName: "orders");
```

The primary constructor queries the same standalone `DurableTaskClient` used to schedule canonical Process
orchestrations. Exact lookup accepts the physical task-hub ID returned by `ScheduleCohesiveProcessAsync`; the
`ProcessExecutionRecord.ProcessId` remains that authority-scoped physical identity. Its exact `Definition` is
retained from canonical start evidence even before custom status is available, while its
`RuntimeStatus.ProcessInstanceId` is the distinct logical Process identity. The repository validates the physical ID
from the retained start receipt and validates the custom status against the receipt's exact logical identity and
definition reference. The logical overload derives the same opaque authority-scoped physical ID used at scheduling
and performs one exact lookup; it does not enumerate task-hub pages.

New schedules publish immutable `cohesive.process.tags/v1` Scheduler discovery tags for the logical Process instance
and the exact definition identity, revision, fingerprint algorithm, canonicalization, and value. The centralized
`DurableTaskProcessTags` catalog owns their names and projection. Every value is validated against Scheduler's
1,000-byte UTF-8 limit before admission. The set excludes authority, tenant, command/idempotency identity, input,
output, interaction content, waits, failure detail, and all mutable state. Tags can be filtered in the Scheduler
dashboard; the pinned .NET `OrchestrationQuery` has no tag predicate, so programmatic exact lookup uses deterministic
key derivation rather than a hidden page scan.

Current canonical interpreter custom status is a serialized `ExecutionStatus`, not the full orchestration result.
It exposes the exact definition, logical instance and attempt lineage, control revision and mode, active activation,
safe token locations, active waits and deadlines, activation progress, work demand, health, and terminal kind. The
projection is derived from `ProcessControlState` and `ProcessContinuationState`; it contains no control commands or
receipts, interaction envelopes, buffered inputs, wait keys, bindings, operation ledgers, input/output values, or
terminal payload. Terminal detail is explicitly redacted while retaining its portable contract.

Every finite activation is also projected immediately from its authoritative `ProcessActivationDecision` through
the shared `ProcessExecutionTraceProjector`. The payload-safe `NormalizedExecutionTrace` artifacts accumulate in
`DurableTaskSequentialProcessResult` and cross Continue-as-new boundaries with the existing result carrier. This
preserves the exact definition, instance, attempt, activation, disposition, safe point, and normalized semantic event
order before those per-activation facts leave scope. Projection diagnostics fail the orchestration rather than
silently creating a trace gap. Traces remain outside custom status, and Scheduler history remains provider execution
evidence rather than a source from which missing semantic traces may be fabricated. Results written before trace
retention can therefore contain canonical activation evidence without a corresponding normalized trace prefix.

The same current repository implements `IProcessExecutionTraceRepository` as a separate opt-in physical or trusted
logical read. It performs one exact task-hub lookup, validates start, custom-status, terminal-result, and trace
affinity, and then returns only the versioned portable `ProcessExecutionTraceArtifact` plus acquisition disposition.
The physical task-hub key never enters the artifact. `NotFound`, `InProgress`, `Available`, and
`TerminalArtifactUnavailable` are distinct outcomes. Available artifacts report the exact count of earliest
activation-evidence entries without a trace; only a zero missing-prefix count is complete. Trace artifacts are
available after a canonical terminal result exists—this boundary does not stream live event traces or enlarge custom
status.

`DurableTaskProcessExecutionExplainRepository` composes that same current repository with the immutable exact plan
catalog. It returns the shared `ExecutionExplainArtifact`, never a Scheduler-specific diagnostics DTO. Static
compilation evidence comes from the already compiled canonical plan; realization evidence is projected one-to-one
from the plan's exhaustive requirement disposition ledger; safe current state comes from `ExecutionStatus`; and only
the latest retained normalized trace for the current attempt becomes the artifact's trace reference. Pending and
active executions therefore remain explainable as partial lifecycle artifacts without inventing traces. Legacy
missing prefixes and terminal executions without canonical results become structured warnings, preserving the exact
coverage limitation. Missing plans and definition, instance, attempt, status, or trace conflicts fail closed.

The current repository returns that exact projection in `ProcessExecutionRecord.RuntimeStatus` and derives the
compatibility lifecycle field from it. A terminal Scheduler state may close stale nonterminal custom status, but a
contradictory terminal cut fails instead of being normalized away. Although the pinned client API requires
`FetchInputsAndOutputs` to retrieve custom status, current records never project the fetched start payload,
orchestration output, provider failure body, or raw JSON. Scheduler custom status and task-hub history remain
operational evidence, never semantic authority.

The Core query-client constructor remains an explicit migration reader for the retired adapter's status, input,
output, and failure projections. It can be removed only after those task hubs are outside the supported retention
window. Tagless canonical instances created before the discovery projection remain readable, while a recognized
partial or conflicting Cohesive tag set fails closed. Normalized trace retention is complete for newly executed
activations, and terminal trace retrieval is available through the current standalone-client repository. The
migration-only Core reader explicitly does not fabricate canonical traces from historical provider records.
Canonical runtime explain composition is available through the current repository and exact deployed plan catalog.
The provider-neutral execution, explain, and retained-trace boundaries support trusted
authority-scope/logical-identity reads for application surfaces and separate physical-key reads for engine
administration. ASP.NET inspect, explain, and retained-trace bindings perform only logical reads without accepting
caller-authored authority evidence. Inspect returns the retained canonical custom-status projection; a valid pending
admission without custom status remains unavailable rather than being inferred from Scheduler lifecycle. The trace
binding writes exact portable artifact bytes and maps every repository availability state through the route-neutral
catalog. Lifecycle mutation bindings, live trace streaming, richer dashboard presentation, and history-event
normalization remain follow-up work.

## Realization planning

```csharp
using Cohesive.Adapters.DurableTask;

DurableTaskProcessPlanningResult result =
    DurableTaskProcessRealizationCompiler.Compile(compiledProcessPlan);

if (!result.IsSuccessful)
{
    // Present result.Realization.Diagnostics; no physical plan or partial execution exists.
}

DurableTaskProcessRealizationPlan plan = result.Plan!;
```

`DurableTaskProcessTargetProfile.Planning` explicitly disposes every current canonical Process construct and
cross-cutting guarantee. The compiler first acquires the target-neutral inventory from the exact canonical plan,
then pairs every requirement and its source-node/link provenance with one target decision. Missing, invalid, or
unavailable semantics produce structured diagnostics and no physical plan. In particular, a whole-definition
multi-resource atomicity demand is rejected.

The resulting plan retains the exact `CompiledProcessPlan`; it contains no generated or hand-authored Durable Task
workflow. A planning-profile plan is design evidence and cannot be deployed through the worker catalog.

`DurableTaskProcessTargetProfile.Executable` is a separate, versioned profile for the bounded conformance-tested
runtime. It also disposes the complete canonical construct and guarantee catalogs. Its v2 profile realizes
domain-event emission only inside the declared durable after-origin, target-deduplicated publication boundary, while
Reply discharge and whole-definition multi-resource atomicity remain unavailable. Adding a canonical construct or
guarantee without an executable disposition fails profile construction and the inventory-completeness test; omission
cannot imply support.

## Process execution

Qualify every deployed definition against the executable profile, retain its exact physical plan, and register one
canonical host for bounded I/O. Unsupported requirements produce source-attributed realization diagnostics before a
plan can enter the catalog. To dispatch Requests automatically, also supply exact concrete bindings and an adapter
resolver. Child invocation protocols derive their Request and terminal Reply mappings; deployment code declares
only physical attempt, lease, idempotency, timeout, and recovery policy:

```csharp
DurableTaskProcessPlanningResult executable =
    DurableTaskProcessRealizationCompiler.CompileExecutable(compiledProcessPlan);
if (!executable.IsSuccessful)
{
    // Present executable.Realization.Diagnostics; nothing can enter the worker catalog.
}

DurableTaskProcessRealizationPlan physicalPlan = executable.Plan!;
DurableRequestBinding childBinding = childInvocationProtocol.BindDurably(
    maxAttempts: 3,
    claimLease: TimeSpan.FromMinutes(2),
    DurableOperationIdempotencyEvidence.TargetDeduplication,
    reconciliationTarget: childReconciliationTarget);
var catalog = new DurableTaskSequentialProcessPlanCatalog(
    [physicalPlan],
    [childBinding],
    new ApplicationDomainEventPublisherResolver());

services.AddSingleton<IAsyncProcessReferenceHost, ApplicationProcessHost>();
services.AddSingleton<IDurableOperationAdapterResolver, ApplicationDurableOperationAdapterResolver>();
// Register a provider-aware IDurableOperationExceptionClassifier here when available.
services.AddDurableTaskWorker(worker =>
{
    worker.AddCohesiveSequentialProcesses(catalog);
    worker.UseDurableTaskScheduler(connectionString);
});
services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(connectionString));
```

When the exact deployment catalog depends on application services, compose it as a singleton through the worker
registration factory. Catalog construction and admission run while the host constructs its hosted services, before
the worker starts processing, and the same immutable instance reaches the orchestrator and activities:

```csharp
services.AddDurableTaskWorker(worker =>
{
    worker.AddCohesiveSequentialProcesses(serviceProvider =>
        ApplicationProcessDeploymentCatalog.Create(
            serviceProvider.GetRequiredService<ApplicationProcessDefinitions>(),
            serviceProvider.GetRequiredService<IDomainEventPublisherResolver>()));
    worker.UseDurableTaskScheduler(connectionString);
});
```

One worker registration has exactly one catalog composition authority. Registering a separate catalog and also
supplying a factory is rejected instead of allowing the orchestrator and activities to observe different catalogs.
Standalone Durable Task does not resolve orchestrator constructors through application dependency injection, so the
adapter registers an SDK orchestrator factory closed over a host-scoped activation slot. The catalog admission hosted
service fills that slot before the worker starts; activation before admission or replacement after admission fails
closed. The slot is scoped to one service collection and is not ambient or process-global state.

`IAsyncProcessReferenceHost` is the physical worker port for Transition, Relation/Query, and Signal-target
activities. Naturally asynchronous implementations must implement it directly. A bounded legacy synchronous host
remains available only through the named compatibility projection:

```csharp
services.AddSingleton<IProcessReferenceHost, BoundedSynchronousProcessHost>();
services.AddSingleton<IAsyncProcessReferenceHost>(provider =>
    new SynchronousProcessReferenceHostAdapter(
        provider.GetRequiredService<IProcessReferenceHost>()));
```

The compatibility adapter checks cancellation before entering the synchronous call but cannot interrupt it after
entry. Do not use it for asynchronous I/O or unbounded work.

`IDomainEventPublisherResolver` is deployment policy keyed by the exact `DomainEventContractReference`. Every
resolved `IDomainEventPublisher` declares the exact contracts for which its target durably suppresses redelivery by
`DomainEventPublicationDeduplicationKey`. The key combines authority scope, exact contract identity, revision,
fingerprint, and the canonical envelope idempotency key. Plans containing direct `EmitEventProcessNode` contracts
fail catalog admission when that guarantee is missing. Domain events produced dynamically by a host Transition are
resolved and rejected before publication I/O.

The orchestrator schedules `DurableTaskDomainEventPublicationActivity` with the unchanged canonical
`DomainEventEnvelope`; Durable Task activity scheduling provides the after-origin boundary. Activity execution may be
repeated after an ambiguous failure, so the publisher's target-deduplication declaration is mandatory rather than an
optimization. `DurableTaskDomainEventPublication` retains the exact emission identity, scoped key, envelope content
fingerprint, UTC acknowledgement time, and optional bounded target receipt. That evidence survives replay and
Continue-as-new. This profile does not claim physical exactly-once delivery, atomic-with-origin publication, an
adapter-owned retry policy, or ordering beyond the canonical envelope requirements that the publisher must honor.

Register application operation resolvers before `AddCohesiveSequentialProcesses`; the worker method installs
empty, fail-closed defaults only when the application has not supplied them. The immutable
`DurableRequestBindingCatalog` is built directly from the worker catalog's bindings and is reused during replay.
`IDurableRequestBindingResolver`, `IDurableOperationAdapterResolver`, and
`IDurableOperationExceptionClassifier` remain shared execution ports used by both native Storage and Durable Task
interpretations, rather than target-specific copies.

The worker catalog is a deployment projection, not a mutable definition registry. It admits only plans carrying the
exact executable profile identity; planning evidence cannot authorize execution. Each lookup requires the full
definition identity, revision, and fingerprint from the canonical `ProcessStartReceipt`; workers must reconstruct
an equivalent immutable catalog and deterministic Request bindings after restart. The package registers the same
portable JSON converter for worker and client payloads. The initial public SDK names retain `Sequential` for source
compatibility, but executable qualification includes the bounded higher-order constructs listed above.

Schedule the admitted start evidence with the client extension:

```csharp
DurableTaskProcessScheduleResult scheduled =
    await client.ScheduleCohesiveProcessAsync(new(receipt, activationContext), cancellationToken);
```

The physical instance ID is deterministic for the authority scope and canonical Process instance. A duplicate,
byte-equivalent start reuses the instance; conflicting start evidence is rejected. Each Transition or Relation/Query
invocation runs as a bounded activity, awaits `IAsyncProcessReferenceHost` without blocking, and is materialized back
into the reference interpreter. The activity creates an `OperationContext` with the active worker trace and
`IHostApplicationLifetime.ApplicationStopping` token. Durable Task replay then reuses activity history instead of
committing that logical operation again.

Durable Task activity delivery is at-least-once: an ambiguous worker failure can deliver the same complete canonical
invocation again. Its continuation, attempt, activation, token, node, and occurrence identity remain unchanged, and
the host must use that identity to provide idempotent or target-deduplicated evidence. The adapter does not invent a
second physical identity or silently cache handler results outside Durable Task history.

### Lifecycle control

The worker subscribes to the versioned `Cohesive.Processes.Control.v1` external-event stream and evaluates each
`InspectProcessCommand`, `PauseProcessCommand`, `ContinueProcessCommand`, `RestartProcessAttemptCommand`,
`CancelProcessCommand`, or `TerminateProcessCommand` with the canonical `ProcessControlReferenceExecutor`.
Durable Task transports and replays commands; `ProcessControlState`, exact command receipts, attempt/revision
expectations, authorization evidence, and canonical intents remain semantic authority.

```csharp
await client.RaiseCohesiveProcessControlAsync(start, pauseCommand, cancellationToken);
```

Completion of the client call confirms provider event admission only. Read the orchestration custom status or final
`DurableTaskSequentialProcessResult.Control` and `LatestControlDecision` according to the evidence required. Custom
status is the safe `ExecutionStatus` projection used for the current lifecycle fence and operational location; it
deliberately omits commands, receipts, reasons, and payloads. The final result retains the canonical command
disposition, diagnostics, receipt, and control state. The result requires its continuation and control state to
identify the same exact definition, Process instance, and current attempt; Continue-as-new carries both without
making target history a second checkpoint authority.

Every ordinary finite activation is enclosed by canonical `BeginActivation` and `ReachSafePoint` observations.
Commands are prioritized when co-ready with an ordinary stimulus. A command arriving while a Transition,
Relation/Query, or Signal-target activity is in flight is evaluated against the visible in-activation fence:
Pause, RestartAttempt, and Cancel drain that finite work and apply at its exact safe point, while Terminate stops
admission of its result. A paused orchestration remains alive and admits only control commands until Continue.
An already-admitted durable Request owns its current retry/reconciliation task and may finish that logical operation
while the Process is paused; its result is not admitted into a new Process activation until Continue. A physical
timer may likewise become ready but cannot advance canonical state while paused. ARI-302 owns qualification of this
policy across every provider recovery cut.

RestartAttempt retains the Process instance and canonical attempt lineage, closes the old attempt, creates the exact
authored replacement attempt, and starts the replacement with a `Control` activation cause. Before replacement child
work may start, every active child with `Propagate` cancellation is projected from the canonical restart intent plus
the abandoned continuation, sent its exact portable cancellation command, and drained to terminal evidence. The
replacement fence normally makes the old child result stale at the parent target. If its Reply became eligible in
the same deterministic wake as the prioritized restart command, the adapter retains the canonical admission ledger
with the explicit `SupersededByAttemptRestart` disposition instead. Neither result may advance the new attempt.
Scheduler sub-orchestrations have independent task-hub-unique identities; the adapter therefore creates the
replacement attempt's new exact child occurrence instead of pretending to reattach a second call to the old physical
instance. `Detach` remains the explicit authored policy under which an old child may outlive its parent attempt and
is not drained.

Old target-local timers and non-child pending result tasks are abandoned. Propagated child closure prevents old and
replacement child executions from overlapping, but it cannot undo a child side effect that committed before the
child reached its cancellation safe point. Such operations must retain their declared domain idempotency or target
deduplication boundary across a replacement attempt. Cancel performs a canonical cooperative cancellation activation
and retains its terminal trace. Terminate is represented by terminal `ProcessControlState`; the physical orchestration
completes normally so the canonical termination receipt and cleanup decision remain queryable. The adapter
intentionally does not substitute similarly named Scheduler suspend/terminate APIs because they cannot preserve this
complete protocol.

The current bounded cleanup profile accepts `RetainEvidence` for RestartAttempt and Terminate. Commands demanding
attempt-resource release or affinity abandonment fail before canonical admission because no general provider cleanup
port exists yet. Target-local timer cancellation and abandoned-task observation are physical hygiene, not a claim
that an external activity was recalled. Propagated child closure is stronger: the parent awaits the child's canonical
terminal control evidence before replacement child work begins. Complete durable Request retry/reconciliation
pausing, general external cleanup, lifecycle Signal qualification, and exhaustive crash/race closure remain the
follow-up qualification scope tracked by ARI-302.

Transport cancellation tokens cancel only scheduling or event delivery. Worker shutdown cancels the activity
`OperationContext` through `ApplicationStopping`; the resulting `OperationCanceledException` is physical failure
and never becomes `CancelProcessCommand` or semantic cancellation evidence. The current standalone .NET Durable
Task activity context exposes no per-activity cancellation token, and terminating an orchestration does not recall an
already-running activity, so host implementations must also honor their exact-operation idempotency boundary.

Entity-creation Transitions hosted through `EntityTransitionProcessOperationAdapter` provide one such natural
boundary when their repository implements `IEntityTransitionOperationRepository`: after exact-occurrence lookup,
an existing subject can replay only an attributable atomic creation receipt with the same authority scope, exact
Transition, subject, and materialized input. The original typed result and envelopes are returned unchanged, so a
replacement attempt retains the original emission and target-deduplication identities. Changed intent conflicts, and
an existing subject without an atomic creation receipt remains rejected; this is not general upsert behavior.

A non-child Request without an exact binding still emits canonical evidence and waits for a canonical
`ProcessActivationInput` external event; use `RaiseCohesiveProcessInteractionAsync` for that deliberately external
boundary. `InvokeProcess` and `ForEachPartition` are different: worker catalog admission inventories their exact
Request contracts and rejects a missing, duplicate, fingerprint-drifted, or interaction-incompatible concrete
binding before orchestration starts. A bound Request creates the canonical `DurableOperationState`, crosses explicit
before/after dispatch and acknowledgement/admission history cuts, and dispatches through an activity. The canonical
`DurableOperationReferenceExecutor` alone decides claims, bounded retries, ambiguity, reconciliation,
acknowledgement, and Reply admission. Activity and orchestration replay retain the Request emission, scoped target
deduplication key, attempt IDs, fences, and Reply IDs.

Durable Task activities are at-least-once. The executable profile therefore rejects a binding whose
`IdempotencyEvidence` is `None`; automatic dispatch requires `TargetDeduplication` or `NaturallyIdempotent`, with
matching adapter capability evidence. No SDK retry policy is installed around the activity. Explicit adapter
failure evidence feeds the canonical retry policy, and thrown adapter exceptions use the registered classifier
(conservatively ambiguous by default). Claim leases are renewed with durable timers while activity I/O is in
flight. Ambiguous outcomes invoke the exact adapter reconciliation path before retry or admission.

Fork branches retain canonical tokens and lineage while bound Requests are scheduled concurrently. The canonical
Join alone selects winning branches and applies its authored cancellation policy. A child invocation becomes a
sub-orchestration with the interpreter-derived child instance and attempt; its terminal status is mapped through the
authored child outcome mapping rather than through physical task success or failure. `Propagate` sends the portable
`ProcessChildCancellationIntent` to the exact child instance. The child validates the exact definition and
continuation, deterministically lowers the intent to a canonical `CancelProcessCommand`, and closes its control
attempt and continuation through the same receipt and cancellation-activation protocol; the parent waits for that
child to close.
`Detach` deliberately stops awaiting the child. A late result from either policy is admitted through the Request's
late-result rule and cannot advance the already-closed parent wait.

`ForEachPartition` uses the canonical retained work inventory and enforces maximum items, starts per activation, and
parallelism before scheduling sub-orchestrations. It does not truncate excess work. `RepeatAcrossActivation` and
Fork/Durable Cut boundaries use Durable Task Continue-as-new with the complete canonical result at the cut. The
resume carrier is target-owned derived evidence: it retains definition, continuation, recurrence, operation, and
activation lineage and cannot replace the exact compiled plan.

A `Timer` node evaluates its absolute due expression once in the canonical reference interpreter. The persisted
`ProcessTimerState.DueAtUtc` is the semantic authority; the adapter only projects that instant into a Durable Task
timer relative to replay-stable orchestration time. An early physical wake remains canonically quiescent and
reschedules the same retained wait. Closing a competing branch cancels only its physical timer projection, and
an active timer prevents Continue-as-new from discarding its physical task. Worker replay reconstructs timers from
active canonical waits. Timer clauses inside `AwaitMatch` use the same projection without becoming a second source
of deadline or winner semantics.

`AwaitMatch` uses the same canonical wait state for every typed interaction and timer clause. The adapter subscribes
to the Durable Task external-event stream before the first canonical activation so an already queued unscoped input
retains canonical early-delivery evidence. Active timer clauses become physical timers keyed by exact wait and clause
identity. When an external input and one or more timers are ready in the same deterministic wake, they are presented
together at one canonical activation time; `ProcessReferenceInterpreter` alone applies guards, priority,
clause-identity tie-break, winner selection, and early, late, stale, duplicate, or missing-target policy. This admits
canonical `ProcessActivationInput` evidence for external inputs and addressed Signals. Reply emission nodes remain
outside this executable slice.

`SendSignalProcessNode` target evaluation stays inside the canonical reference interpreter. When materialization is
required, a replayable activity asynchronously asks the registered `IAsyncProcessReferenceHost` for the existing closed
`ProcessSignalTargetResult`; no Durable Task target DTO or second resolution policy exists. The interpreter then
authors the exact `SignalEnvelope`, including contract, target, correlation, delivery, ordering, origin, occurrence,
and provenance. The orchestrator routes that envelope unchanged inside a `ProcessActivationInput` external event to
the exact authority-scoped Process instance.

The external event is only delivery evidence. The recipient `ProcessReferenceInterpreter` remains the authority for
target validation, wait arbitration, and consumed, stale, duplicate, early, late, or missing-target disposition. A
replayed sender history cannot reapply a logical Signal as a second canonical admission. This executable slice
requires durable delivery and a `ProcessTokenInteractionTarget`; activation-local delivery and Transition targets
fail before dispatch. General external adapter dispatch and full lifecycle `Signal` qualification remain separate
capabilities; the control stream preserves the canonical command family without advertising that remaining closure.

If the semantic deadline wins, or canonical policy requires a typed terminal outcome or escalation that this slice
cannot author, the orchestration fails closed with `DurableTaskDurableOperationRecoveryRequiredException`. Its
safe custom status identifies the exact Process cut and reports degraded or unhealthy runtime health without copying
the operation ledger. Canonical operation state remains in deterministic orchestration history; a later ARI-292
repository/explain slice owns supported retrieval of that evidence. The runtime does not turn worker cancellation
into semantic cancellation or invent timeout/escalation values.

`Return` completes the orchestration. An authored root `Fail` produces canonical failure evidence and a failed
physical orchestration; child failure remains a semantic child result for its parent. A canonical Durable Cut closes
one finite activation and resumes with exact continuation evidence, using Continue-as-new in the SDK realization.

### Why the orchestration keeps a target-local suspension driver

The physical activity boundary and the target-neutral `ProcessReferenceInterpreter.ActivateAsync` share the same
canonical operation payloads, asynchronous host contract, result validation, and occurrence identity. The Durable
Task orchestrator deliberately retains its small suspension/materialization loop because it must await only
Durable-Task-created tasks on the deterministic orchestration scheduler and, while one host activity is in flight,
race the lifecycle-control event stream so Terminate can abandon result admission. The target-neutral driver owns
neither scheduler affinity nor control-event arbitration. Replacing the loop with it would either hide target policy
in the core interpreter or weaken Durable Task replay/control semantics. Differential and restart tests keep the two
drivers aligned around the unchanged pure `ProcessReferenceInterpreter.Activate` reducer.

## Validation

Run the focused tests without external infrastructure:

```bash
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release \
  --filter FullyQualifiedName~DurableTaskSequentialProcessInterpreterTests
```

Run the Scheduler-emulator integration test with Docker, or point the same script at a supplied
`DURABLE_TASK_SCHEDULER_CONNECTION_STRING`:

```bash
eng/test-durable-task-integration.sh
```

The script pins the emulator image by digest. Emulator coverage proves successful completion, canonical Pause,
Inspect, exact replay, Continue, RestartAttempt, Cancel, and Terminate through the public event API, bound Request
activity dispatch and Reply admission, child sub-orchestration, recurrence history rollover, authored failure,
duplicate start admission, cross-instance and self-Signal delivery, and worker restart while an unbound Request,
canonical Timer, and AwaitMatch are waiting. The restart assertions verify that retained Transition activity history
is not reinvoked, Timer keeps its persisted due instant, and an AwaitMatch input is admitted once after replay. The
emulator reads the safe `ExecutionStatus` custom-status projection directly and through
`IProcessExecutionRepository` while orchestrations are live and after semantic cancellation; it does not use custom
status as a hidden continuation, inbox, outbox, or control-receipt channel. It also proves both AwaitMatch
interaction and timer winners. Deterministic conformance tests additionally cover
lifecycle authorization and revision fences, deferred safe-point control during active host work,
replacement-attempt lineage, naturally asynchronous host awaiting, worker-shutdown cancellation projection,
duplicate activity delivery, structured host-failure retention, operational/semantic cancellation separation,
exact Signal target and envelope
preservation, recipient missing/stale/duplicate/consumed dispositions, the
interaction/timer priority and tie-break matrix, early and policy-disposition evidence, multiple timer clauses,
concurrent fork Requests, Join selection, timer replay and competing-wait cancellation, child lineage and
cancellation, partition bounds, recurrence bounds, bounded retry, reconciliation, deadline and escalation
fail-closed behavior, and crash cuts before dispatch, after dispatch, after acknowledgement, and before Reply
admission.

## Capability boundary

Every canonical Process construct and cross-cutting guarantee receives an explicit native, composed, constrained,
or unavailable realization decision in the planning profile. Missing inventory coverage and unknown constructs are
hard planning errors; unsupported semantics fail before a physical plan is produced.

## Related Packages

- `Cohesive.Processes` for canonical Process IR, compilation, and monitoring contracts.
- `Cohesive.Storage` for the independent native durable Process runtime and store port.
