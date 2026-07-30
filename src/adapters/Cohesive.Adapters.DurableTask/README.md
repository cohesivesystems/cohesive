# Cohesive.Adapters.DurableTask

Read-only Azure Durable Task monitoring integration for historical Cohesive Process executions.

The former adapter executed callback-bearing Process definitions through a single-cursor checkpoint. ARI-170
retired that path because it could not preserve canonical Process semantics. This package deliberately does not
start, resume, or host Processes until a future Durable Task interpretation implements
`Cohesive.Storage.Processes.IProcessDurableStore` and executes an exact `CompiledProcessPlan`.

## Install

```bash
dotnet add package Cohesive.Adapters.DurableTask
```

## Use When

- You need to query task-hub records created by the retired adapter during migration.
- You need an `IProcessExecutionRepository` monitoring projection over an existing Durable Task hub.
- You do not need to start or advance canonical Processes through Durable Task.

## Monitoring boundary

```csharp
using Cohesive.Adapters.DurableTask;

IProcessExecutionRepository repository = new DurableTaskProcessExecutionRepository(
    queryClient,
    taskHubName: "orders");
```

The repository projects lifecycle, timing, input, output, and failure evidence only. Historical current-node,
place, wait, run-option, callback, and definition-registry data is intentionally not an execution authority.

## Related Packages

- `Cohesive.Processes` for canonical Process IR, compilation, and monitoring contracts.
- `Cohesive.Storage` for the canonical durable Process runtime and store port.
