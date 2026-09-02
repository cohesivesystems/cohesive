# Cohesive.Adapters.DurableTask

`Cohesive.Adapters.DurableTask` executes an explicitly bounded canonical Process profile on Azure Durable Task and
provides monitoring projections for current and migrated historical task hubs.

## Install

```bash
dotnet add package Cohesive.Adapters.DurableTask
```

## Check a Process realization

Planning consumes the exact canonical Process plan and returns either a complete target realization or structured
diagnostics:

```csharp
var result = DurableTaskProcessRealizationCompiler.Compile(compiledProcessPlan);

if (!result.IsSuccessful)
{
    foreach (var diagnostic in result.Realization.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");

    return;
}

DurableTaskProcessRealizationPlan plan = result.Plan!;
```

The physical orchestration retains `CompiledProcessPlan` and `ProcessReferenceInterpreter` as semantic authority.
Durable Task owns scheduling, deterministic replay, activities, sub-orchestrations, timers, and provider history.

## Implemented profile

The executable slice covers Transition and Relation/query operations, bound Requests, durable after-origin domain
events, Choice, Match, bounded Fork/Join, AwaitMatch, Timer, Process-token Signals, child Processes, bounded
partitioning and recurrence, durable cuts, Return, and Fail.

The adapter also provides:

- Canonical start admission and exact replay.
- Safe Process status, trace, and explain projections.
- Separate authority-scoped retrieval of retained canonical input and terminal values.
- Lifecycle control across Continue-as-new.
- Current standalone-SDK monitoring repositories.
- A migration-only reader for hubs created by the retired Core adapter.
- Differential and Scheduler-emulator conformance coverage.

## Important boundaries

The executable profile is narrower than the planning profile. Reply emission nodes, non-Process Signal targets,
activation-local Signal delivery, lifecycle Signal qualification, atomic-with-origin event publication, and complete
provider cleanup/recovery semantics remain unavailable and fail closed.

Scheduler custom status and history are physical evidence, not a replacement Process definition, continuation,
inbox, outbox, or semantic trace. Potentially sensitive input and terminal values are available only through the
separate trusted value repository and never enter generic monitoring records.

The migration reader does not fabricate canonical evidence that historical runs did not retain.

## Continue

- [Internals](INTERNALS.md) contains monitoring, trusted value retrieval, planning, worker registration, execution,
  suspension, recovery, validation, and the full capability boundary.
- [Durable Task interpreter decision](../../../docs/decisions/durable-task-process-interpreter.md) records the
  accepted architecture.
- [`Cohesive.Processes`](../../Cohesive.Processes/README.md) owns canonical Process semantics.
- [`Cohesive.Storage`](../../Cohesive.Storage/README.md) provides the separate provider-neutral durable Process
  runtime and store port.
