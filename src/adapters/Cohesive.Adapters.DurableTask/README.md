# Cohesive.Adapters.DurableTask

Azure Durable Task integration for historical Process monitoring, realization planning, and an executable
bounded Process profile over the standalone Microsoft Durable Task SDK.

The former adapter executed callback-bearing Process definitions through a single-cursor checkpoint. ARI-170
retired that path because it could not preserve canonical Process semantics. The replacement keeps the exact
`CompiledProcessPlan` and `ProcessReferenceInterpreter` as semantic authority. A generic Durable Task orchestration
now executes the admitted bounded slice; activities invoke canonical Transition, Relation/Query, and Request host
operations, sub-orchestrations execute child Processes, and Durable Task owns physical scheduling, history, and replay.

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
  Fork/Join, Timer, child Process, bounded partition, bounded recurrence, Durable Cut, Return, and Fail constructs.

The executable profile remains narrower than the complete planning profile. AwaitMatch, signals, general external
waits, root lifecycle control, and complete operational lifecycle semantics remain outside this slice and are
rejected when the worker catalog is built. Request dispatch, bounded retry, reconciliation, acknowledgement, and
Reply admission are implemented; typed timeout, terminal-failure, and escalation paths fail closed with their
canonical operation ledger because this slice does not fabricate the authored recovery outcome.

## Monitoring boundary

```csharp
using Cohesive.Adapters.DurableTask;

IProcessExecutionRepository repository = new DurableTaskProcessExecutionRepository(
    queryClient,
    taskHubName: "orders");
```

The repository projects lifecycle, timing, input, output, and failure evidence only. Historical current-node,
place, wait, run-option, callback, and definition-registry data is intentionally not an execution authority.

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
`ProcessChildCancellationIntent` to the exact child instance and the parent waits for that child to close;
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
active canonical waits. This does not yet realize timer clauses inside `AwaitMatch`, whose arbitration and input
policies remain a separate executable slice.

If the semantic deadline wins, or canonical policy requires a typed terminal outcome or escalation that this slice
cannot author, the orchestration fails closed with `DurableTaskDurableOperationRecoveryRequiredException`. Its
custom status contains the full canonical operation ledger and exact recovery intent when one exists. The runtime
does not turn worker cancellation into semantic cancellation or invent timeout/escalation values.

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

The script pins the emulator image by digest. Emulator coverage proves successful completion, bound Request
activity dispatch and Reply admission, child sub-orchestration, recurrence history rollover, authored failure,
duplicate start admission, and worker restart while an unbound Request and a canonical Timer are waiting. The
restart assertions verify both that retained Transition activity history is not reinvoked and that the Timer keeps
its persisted due instant. Deterministic conformance tests additionally cover concurrent fork Requests, Join
selection, timer replay and competing-wait cancellation, child lineage and cancellation, partition bounds,
recurrence bounds, bounded retry, reconciliation, deadline and escalation fail-closed behavior, and crash cuts
before dispatch, after dispatch, after acknowledgement, and before Reply admission.

## Capability boundary

Every canonical Process construct and cross-cutting guarantee receives an explicit native, composed, constrained,
or unavailable realization decision in the planning profile. Missing inventory coverage and unknown constructs are
hard planning errors; unsupported semantics fail before a physical plan is produced.

## Related Packages

- `Cohesive.Processes` for canonical Process IR, compilation, and monitoring contracts.
- `Cohesive.Storage` for the independent native durable Process runtime and store port.
