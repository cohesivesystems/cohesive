# Cohesive.Adapters.DurableTask

Read-only Azure Durable Task monitoring integration for historical Cohesive Process executions.

The former adapter executed callback-bearing Process definitions through a single-cursor checkpoint. ARI-170
retired that path because it could not preserve canonical Process semantics. This package deliberately does not
start, resume, or host canonical Processes today.

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
- You do not need to start or advance canonical Processes through Durable Task.

Do not select this published package as an execution profile until its package documentation declares an
implemented executable capability closure.

## Monitoring boundary

```csharp
using Cohesive.Adapters.DurableTask;

IProcessExecutionRepository repository = new DurableTaskProcessExecutionRepository(
    queryClient,
    taskHubName: "orders");
```

The repository projects lifecycle, timing, input, output, and failure evidence only. Historical current-node,
place, wait, run-option, callback, and definition-registry data is intentionally not an execution authority.

## Accepted execution target

The planned interpreter will use the standalone Microsoft Durable Task SDK and Azure Durable Task Scheduler. One
generic interpreter will execute exact canonical plans. Activities will represent bounded target or domain I/O,
not an opaque whole-Process callback. Native timers, external events, sub-orchestrations, versioning, tags, custom
status, lifecycle APIs, and dashboards will be used only where their guarantees preserve the requested canonical
semantics.

Every canonical Process construct and cross-cutting guarantee must receive an explicit native, composed,
constrained, or unavailable realization decision. Missing inventory coverage and unknown constructs are hard
planning errors; unsupported semantics fail before partial execution.

## Related Packages

- `Cohesive.Processes` for canonical Process IR, compilation, and monitoring contracts.
- `Cohesive.Storage` for the independent native durable Process runtime and store port.
