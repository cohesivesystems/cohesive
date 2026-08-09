# Cohesive.Adapters.DurableTask

Azure Durable Task integration for historical Process monitoring and non-executing realization planning.

The former adapter executed callback-bearing Process definitions through a single-cursor checkpoint. ARI-170
retired that path because it could not preserve canonical Process semantics. This package deliberately does not
start, resume, or host canonical Processes today. It now provides a versioned planning profile and physical-plan
compiler so that exact target feasibility can be inspected before the generic interpreter is implemented.

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
workflow. It is a planning artifact only. No public API in this package accepts it for execution yet.

## Accepted execution target

The planned interpreter will use the standalone Microsoft Durable Task SDK and Azure Durable Task Scheduler. One
generic interpreter will execute exact canonical plans. Activities will represent bounded target or domain I/O,
not an opaque whole-Process callback. Native timers, external events, sub-orchestrations, versioning, tags, custom
status, lifecycle APIs, and dashboards will be used only where their guarantees preserve the requested canonical
semantics.

Every canonical Process construct and cross-cutting guarantee receives an explicit native, composed, constrained,
or unavailable realization decision in the planning profile. Missing inventory coverage and unknown constructs are
hard planning errors; unsupported semantics fail before a physical plan is produced.

## Related Packages

- `Cohesive.Processes` for canonical Process IR, compilation, and monitoring contracts.
- `Cohesive.Storage` for the independent native durable Process runtime and store port.
