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


Async Process source authoring supports `await process.DurableCut(id: new("prepared"));` when a
workflow must retain acquired/computed bindings before mutations. The generator projects the existing
DurableCutProcessNode and resume edge; it introduces no timer, interaction, new checkpoint format or
execution model. A following operation/terminal is required. Runtime hosts persist the cut through
the existing native checkpoint boundary. Generated/native builder definition and fingerprint
equivalence is covered by ProcessComputationAuthoringTests.

### Ephemeral execution foundation

`EphemeralProcessExecutor` prepares a finite canonical plan for invocation-local execution through
`ProcessReferenceInterpreter.ActivateAsync`. It supports sequential Transition/Query calls and finite
branch selection. It needs no checkpoint store or scheduler; it does not provide restart, deduplication,
background continuation, or whole-definition atomicity. Call `Validate(plan)` to inspect unsupported
construct/atomic-scope diagnostics before constructing the executor. Preparation is shared; continuation,
host observations, timers and cancellation are invocation-scoped. The Process validator excludes free
activation cycles, and this realization excludes durable boundaries, so each observed node occurs at
most once per invocation.

The positive timeout requests cancellation using `OperationContext.TimeProvider`. The executor awaits
host quiescence rather than abandoning a writer, so the budget is cooperative, not forced preemption.
An `EphemeralProcessInterruptedException` preserves canonical returned operation outcomes and receipt
locators, plus the identity of a host operation interrupted without returning evidence. Returned evidence
can confirm a write; missing evidence cannot prove that no write occurred. These values may contain
private data and require the same authorization as the underlying operation. Do not export them as
exception payloads or telemetry attributes. Cancellation never implies rollback or a safe automatic retry.
Hosts remain responsible for resource authorization, concurrency and per-operation commit guarantees.
They must not emit interactions; unexpected emissions fail execution before another host operation runs.

This is the execution foundation for fluent service composition. Service-level lifetime/completion
policy projection, consumer migration remains work in progress. Shared service authoring now lowers
hydration/transition/enrichment into the canonical graph and exposes a terminal HTTP adapter. It is not yet
a claim that the service API offers this execution policy.

`ExecuteAsync` returns `EphemeralProcessResult`: the native decision plus `EphemeralProcessEvidence`.
The same evidence accompanies cancellation (`EphemeralProcessInterruptedException`) and physical failure
(`EphemeralProcessExecutionException`). Returned host outcomes and receipt locators survive a later query
failure; an in-flight node without returned evidence remains uncertain. This shares the canonical
`ProcessOperationResult` values rather than inventing another receipt format. Evidence is invocation-local
and not durable audit storage. Success, semantic failure, cancellation and physical failure never authorize
an automatic mutation retry.

Ephemeral admission uses `ProcessInterpreterRealizationCompiler`, with its report retained on the executor.
`ProcessExecutionLifetime` is shared by service policy and interpreter inventory; there is no service-local
copy of the lifetime enum. The default inventory remains durable. An ephemeral inventory omits only persistent
lifecycle control and worker evolution; deterministic interpretation, exact identity, trace/explain, payload
handling, explicit durable Requests and atomic-scope demands remain. Unsupported constructs and atomicity
still fail admission. The profile records constrained, invocation-local materialization without restart,
no automatic retry, trusted hosts without interaction emissions, and protected in-memory evidence. These
boundaries do not establish cross-host recovery, outbox delivery, durable audit retention or ACID. The host's
no-emission contract is checked on returned evidence before another operation executes. Profile preparation
is lazy and shared; the exact-plan realization report is prepared with the executor, not per invocation.

## Transition receipt outputs

A Transition's domain result and its physical commit evidence are different contracts. An
`InvokeTransitionProcessNode` may declare an optional `Receipt` output binding alongside the ordinary
continuation output. Typed authoring uses `InvokeTransitionWithReceipt`. The binding becomes visible
on that continuation and persists through native checkpoints like any other portable bound value.
Omission leaves older Process encodings and fingerprints unchanged.

Selecting a receipt requires matching `ProcessDefinitionLink.ReceiptContract` evidence from the host
binding; a link derived only from the domain Transition document makes no such promise. Compilation
rejects a missing or different attestation before invocation. This evidence is an assertion by the
qualified host, not proof that an arbitrary provider implements atomic persistence. A host that returns
a successful operation without its promised receipt causes terminal contract failure before subsequent
steps; returned operation evidence is retained because effects may already have committed.

A receipt locator is not an authorization grant or an entity snapshot. A subsequent relation query can
resolve it through the authoritative receipt provider, check tenant/subject/occurrence affinity and
resource authorization, and project the exact committed entity and token into the public Process result.
This avoids recovering a response by guessing an internal node name or reading a newer entity state.
Storage-backed enrichment and Ari adoption are separate qualification gates; the core tests prove typed
authoring equivalence, link admission, evidence preservation, wire round-trip and serialized-cut resumption.

## Compile an authored dependency closure

`ProcessDefinition.GetProcessDependencies()` derives unique, canonically ordered exact child references
from the node table, including ordinary invocations, partitioned work, and cancellation finalizers.
It does not add serialized data or change definition fingerprints. The same projection supplies
validated `ProcessDefinitionLink` dependency evidence.

Call `ProcessStaticCompiler.CompileClosure(roots, documents, externalContext)` to prepare selected roots
without encoding their child order in application code. `documents` is the existing integrity-checked
`ExecutionDefinitionDocumentCatalog`; it resolves exact identity, revision, and fingerprint. The caller
selects deployment roots and provides non-Process links, interaction contracts, and optional shapes.
Process links in external evidence are rejected: the canonical documents own that graph.

Compilation traverses the authored closure iteratively, validates every reachable document through the
ordinary compiler, and shares successful child plans within the call. The strict typed projection decoded
for discovery is reused during semantic validation; envelope checks, canonical-byte round-trip comparison,
and context-dependent diagnostics still run for each compilation. No validation result is cached.
A parent's evidence contains only
its transitive children, not previously compiled siblings. Unselected documents are not compiled.
Missing references, incompatible fingerprints, cycles, invalid definitions, unsupported extensions, and
scope demands fail admission. `FailedDefinition` identifies the failed reference and `Validation`
retains structured diagnostics. `Plans` is empty on failure and ordered bottom-up on success.
No I/O, target qualification, global cache, or persisted dependency model is introduced. Callers own
cross-call preparation lifetime and must still qualify plans for their physical target.

For example, when two roots invoke the same child, callers previously supplied validated child links
and arranged compilation manually. Closure compilation derives both edges and prepares the child once.
`ProcessClosureCompilationTests` qualifies this diamond, root selection, duplicate calls, canonical
plan equivalence, external-authority conflicts, and resolution/validation failures. The reusable graph
operation belongs in Cohesive; product root policy and runtime adapter admission remain in Ari.
