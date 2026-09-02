# Cohesive.Processes

`Cohesive.Processes` describes typed workflows that coordinate Relations, Transitions, requests, events, signals,
waits, parallel work, recurrence, and terminal outcomes without binding the workflow to one runtime.

## Install

```bash
dotnet add package Cohesive.Processes
dotnet add package Cohesive.Analyzers
```

`Cohesive.Analyzers` supplies the expression-first C# source generator. Add it as an analyzer reference when using
project references.

## Author a Process

Human-written Processes use ordinary asynchronous structure. Local node identities are deterministic conventions and
do not appear in the common authoring path:

<!-- <docs:sequential-process> -->
```csharp
[GenerateProcessDefinition(nameof(Run))]
public static partial class FindCustomerProcess
{
    static async ProcessTask<CustomerResult> Run(
        ProcessContext process,
        FindCustomerInput input)
    {
        var customer = await process.Query(
            CustomerRelations.ByEmail,
            input);

        return customer;
    }
}
```
<!-- </docs:sequential-process> -->

`CustomerRelations.ByEmail` is a typed canonical Relation handle. It remains authoritative for its input, result,
identity, revision, and fingerprint; the Process call site does not repeat them.

The generated `Define` factory materializes a canonical Process document. The annotated method is syntax inspected by
the generator and is never invoked as an application callback. No delegate, closure, suspended CLR state machine, or
ambient service survives into the persisted definition.

## What you can express

- Typed Relation reads and queries, Transition invocations, and request/effect protocols.
- `if`, exact `switch`, Choice and Match, typed returns, and authored failures.
- Durable events, signals, timers, waits, and deterministic arbitration.
- Fork/Join, child Processes, bounded partitions, and bounded recurrence.
- Cancellation finalization, compensation requirements, retries, and recovery policy.
- Static validation, capability realization, simulation, reference execution, and durable-runtime interpretation.

## Identity and durability

Conventions are appropriate for local structural nodes. Explicit occurrence identities remain available when an
external protocol, persisted history, or evolution boundary needs them. Top-level Process identity and revision are
provided when the generated definition is materialized.

The canonical document remains the source of truth. Compiled plans, runtime continuations, durable checkpoints, and
provider histories are interpretations tied to its exact reference.

## Current boundary

The package owns portable Process meaning, compilation, and the reference interpreter. Durable persistence lives in
`Cohesive.Storage`; Azure Durable Task execution lives in `Cohesive.Adapters.DurableTask`. Each runtime declares a
capability closure and must fail before execution when it cannot preserve the requested semantics.

## Continue

- [Internals](INTERNALS.md) covers the complete authoring surface, lifecycle, identity, restricted computation,
  validation, execution, and lowering boundaries.
- [Execution kernel guide](../../docs/EXECUTION_KERNEL_GUIDE.md) explains how Processes compose with other blocks.
- [`Cohesive.Storage`](../Cohesive.Storage/README.md) provides the provider-neutral durable aggregate and runtime.
- [`Cohesive.Adapters.DurableTask`](../adapters/Cohesive.Adapters.DurableTask/README.md) provides the current bounded
  Azure Durable Task interpretation.
