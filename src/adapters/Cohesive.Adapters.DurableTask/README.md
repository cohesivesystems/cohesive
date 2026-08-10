# Cohesive.Adapters.DurableTask

Azure Durable Task integration for historical Process monitoring, realization planning, and the first executable
sequential profile over the standalone Microsoft Durable Task SDK.

The former adapter executed callback-bearing Process definitions through a single-cursor checkpoint. ARI-170
retired that path because it could not preserve canonical Process semantics. The replacement keeps the exact
`CompiledProcessPlan` and `ProcessReferenceInterpreter` as semantic authority. A generic Durable Task orchestration
now executes the admitted sequential slice; bounded activities invoke canonical Transition and Relation/Query host
operations, while Durable Task owns physical scheduling, history, and replay.

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
- You need to execute an exact sequential Process containing Transition, Relation/Query, Request, Choice, Match,
  Durable Cut, Return, and Fail constructs.

The executable profile is intentionally narrower than the complete planning profile. Timers, signals, fork/join,
child Processes, controls, full request dispatch/recovery, and complete operational lifecycle semantics remain
outside this slice and are rejected when the worker catalog is built.

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

## Sequential execution

Compile every deployed definition, retain its exact physical plan, and register one canonical host for bounded I/O:

```csharp
DurableTaskProcessRealizationPlan physicalPlan =
    DurableTaskProcessRealizationCompiler.Compile(compiledProcessPlan).Plan!;
var catalog = new DurableTaskSequentialProcessPlanCatalog([physicalPlan]);

services.AddSingleton<IProcessReferenceHost, ApplicationProcessHost>();
services.AddDurableTaskWorker(worker =>
{
    worker.AddCohesiveSequentialProcesses(catalog);
    worker.UseDurableTaskScheduler(connectionString);
});
services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(connectionString));
```

The worker catalog is a deployment projection, not a mutable definition registry. Each lookup requires the full
definition identity, revision, and fingerprint from the canonical `ProcessStartReceipt`; workers must reconstruct
an equivalent immutable catalog after restart. The package registers the same portable JSON converter for worker
and client payloads.

Schedule the admitted start evidence with the client extension:

```csharp
DurableTaskProcessScheduleResult scheduled =
    await client.ScheduleCohesiveProcessAsync(new(receipt, activationContext), cancellationToken);
```

The physical instance ID is deterministic for the authority scope and canonical Process instance. A duplicate,
byte-equivalent start reuses the instance; conflicting start evidence is rejected. Each Transition or Relation/Query
invocation runs as a bounded activity and is materialized back into the reference interpreter. Durable Task replay
then reuses activity history instead of committing that logical operation again.

A Request emits canonical request evidence and waits for a canonical `ProcessActivationInput` external event. Use
`RaiseCohesiveProcessInteractionAsync` to exercise that boundary. Automatic request dispatch, durable recovery,
redelivery, and reconciliation are deliberately deferred to the next profile slice; applications must not infer
those guarantees from this initial event bridge.

`Return` completes the orchestration. An authored `Fail` produces canonical failure evidence and a failed physical
orchestration. A canonical Durable Cut closes one finite activation and creates a zero-duration durable timer before
the next activation, preserving the activation boundary in Durable Task history.

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

The script pins the emulator image by digest. Emulator coverage proves successful completion, authored failure,
duplicate start admission, and worker restart while a Request is waiting. The restart assertion also verifies that
the Transition activity already retained in Scheduler history is not invoked again.

## Capability boundary

Every canonical Process construct and cross-cutting guarantee receives an explicit native, composed, constrained,
or unavailable realization decision in the planning profile. Missing inventory coverage and unknown constructs are
hard planning errors; unsupported semantics fail before a physical plan is produced.

## Related Packages

- `Cohesive.Processes` for canonical Process IR, compilation, and monitoring contracts.
- `Cohesive.Storage` for the independent native durable Process runtime and store port.
