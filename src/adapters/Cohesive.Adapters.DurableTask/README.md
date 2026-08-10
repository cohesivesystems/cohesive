# Cohesive.Adapters.DurableTask

Azure Durable Task integration for current and migrated historical Process monitoring, realization planning, and
an executable bounded Process profile over the standalone Microsoft Durable Task SDK.

The former adapter executed callback-bearing Process definitions through a single-cursor checkpoint. ARI-170
retired that path because it could not preserve canonical Process semantics. The replacement keeps the exact
`CompiledProcessPlan` and `ProcessReferenceInterpreter` as semantic authority. A generic Durable Task orchestration
now executes the admitted bounded slice; activities invoke canonical Transition, Relation/Query, and Request host
operations and resolve Signal targets, sub-orchestrations execute child Processes, and Durable Task owns physical
scheduling, history, and replay.

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
- You need to execute an exact Process containing Transition, Relation/Query, Request, Choice, Match, bounded
  Fork/Join, AwaitMatch, Timer, Signal send to a Process token, child Process, bounded partition, bounded recurrence,
  Durable Cut, Return, and Fail constructs.

The executable profile remains narrower than the complete planning profile. Domain-event and Reply emission nodes,
non-Process Signal targets, activation-local Signal delivery, lifecycle Signal qualification, and complete provider
cleanup/recovery semantics remain outside this slice and fail closed. Request dispatch, bounded retry, reconciliation,
acknowledgement, and Reply admission are implemented; typed timeout, terminal-failure, and escalation paths fail
closed with their canonical operation ledger because this slice does not fabricate the authored recovery outcome.

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

// Migration-only reader for task hubs created by the retired Core adapter.
IProcessExecutionRepository historicalRepository = new DurableTaskProcessExecutionRepository(
    historicalQueryClient,
    taskHubName: "orders");
```

The primary constructor queries the same standalone `DurableTaskClient` used to schedule canonical Process
orchestrations. Exact lookup accepts the physical task-hub ID returned by `ScheduleCohesiveProcessAsync`; the
`ProcessExecutionRecord.ProcessId` remains that authority-scoped physical identity. Its
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

The current repository returns that exact projection in `ProcessExecutionRecord.RuntimeStatus` and derives the
compatibility lifecycle field from it. A terminal Scheduler state may close stale nonterminal custom status, but a
contradictory terminal cut fails instead of being normalized away. Although the pinned client API requires
`FetchInputsAndOutputs` to retrieve custom status, current records never project the fetched start payload,
orchestration output, provider failure body, or raw JSON. Scheduler custom status and task-hub history remain
operational evidence, never semantic authority.

The Core query-client constructor remains an explicit migration reader for the retired adapter's status, input,
output, and failure projections. It can be removed only after those task hubs are outside the supported retention
window. Tagless canonical instances created before the discovery projection remain readable, while a recognized
partial or conflicting Cohesive tag set fails closed. Normalized trace and explain retrieval, richer dashboard
presentation, and history-event normalization remain follow-up ARI-292 work.

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
workflow. Successful plans may be deployed through the immutable exact-reference catalog below, but the catalog
performs the additional executable-slice check before worker startup.

## Process execution

Compile every deployed definition, retain its exact physical plan, and register one canonical host for bounded I/O.
To dispatch Requests automatically, also supply deterministic exact binding and adapter resolvers:

```csharp
DurableTaskProcessRealizationPlan physicalPlan =
    DurableTaskProcessRealizationCompiler.Compile(compiledProcessPlan).Plan!;
var catalog = new DurableTaskSequentialProcessPlanCatalog(
    [physicalPlan],
    new ApplicationDurableRequestBindingResolver());

services.AddSingleton<IProcessReferenceHost, ApplicationProcessHost>();
services.AddSingleton<IDurableOperationAdapterResolver, ApplicationDurableOperationAdapterResolver>();
// Register a provider-aware IDurableOperationExceptionClassifier here when available.
services.AddDurableTaskWorker(worker =>
{
    worker.AddCohesiveSequentialProcesses(catalog);
    worker.UseDurableTaskScheduler(connectionString);
});
services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(connectionString));
```

Register application resolvers before `AddCohesiveSequentialProcesses`; the worker method installs empty,
fail-closed defaults only when the application has not supplied them. `IDurableRequestBindingResolver`,
`IDurableOperationAdapterResolver`, and `IDurableOperationExceptionClassifier` are shared execution ports used by
both native Storage and Durable Task interpretations, rather than target-specific copies.

The worker catalog is a deployment projection, not a mutable definition registry. Each lookup requires the full
definition identity, revision, and fingerprint from the canonical `ProcessStartReceipt`; workers must reconstruct
an equivalent immutable catalog and deterministic Request bindings after restart. The package registers the same
portable JSON converter for worker and client payloads. The initial public SDK names retain `Sequential` for source
compatibility, but catalog admission now includes the bounded higher-order constructs listed above.

Schedule the admitted start evidence with the client extension:

```csharp
DurableTaskProcessScheduleResult scheduled =
    await client.ScheduleCohesiveProcessAsync(new(receipt, activationContext), cancellationToken);
```

The physical instance ID is deterministic for the authority scope and canonical Process instance. A duplicate,
byte-equivalent start reuses the instance; conflicting start evidence is rejected. Each Transition or Relation/Query
invocation runs as a bounded activity and is materialized back into the reference interpreter. Durable Task replay
then reuses activity history instead of committing that logical operation again.

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
authored replacement attempt, abandons old target-local timers and pending result tasks, and starts the replacement
with a `Control` activation cause. Cancel performs a canonical cooperative cancellation activation and retains its
terminal trace. Terminate is represented by terminal `ProcessControlState`; the physical orchestration completes
normally so the canonical termination receipt and cleanup decision remain queryable. The adapter intentionally does
not substitute similarly named Scheduler suspend/terminate APIs because they cannot preserve this complete protocol.

The current bounded cleanup profile accepts `RetainEvidence` for RestartAttempt and Terminate. Commands demanding
attempt-resource release or affinity abandonment fail before canonical admission because no general provider cleanup
port exists yet. Target-local timer cancellation and abandoned-task observation are physical hygiene, not a claim
that an external activity or child was recalled. Complete durable Request retry/reconciliation pausing, general
external cleanup, lifecycle Signal qualification, and exhaustive crash/race closure remain the follow-up
qualification scope tracked by ARI-302.

Transport cancellation tokens cancel only scheduling or event delivery. Worker shutdown and SDK task cancellation
never become `CancelProcessCommand` and cannot produce semantic cancellation evidence.

A Request without an exact binding still emits canonical evidence and waits for a canonical
`ProcessActivationInput` external event; use `RaiseCohesiveProcessInteractionAsync` for that deliberately external
boundary. A bound Request creates the canonical `DurableOperationState`, crosses explicit before/after dispatch and
acknowledgement/admission history cuts, and dispatches through an activity. The canonical
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
canonical `ProcessActivationInput` evidence for external inputs and addressed Signals. Domain-event and Reply
emission nodes remain outside this executable slice.

`SendSignalProcessNode` target evaluation stays inside the canonical reference interpreter. When materialization is
required, a replayable activity asks the registered `IProcessReferenceHost` for the existing closed
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
replacement-attempt lineage, operational/semantic cancellation separation, exact Signal target and envelope
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
