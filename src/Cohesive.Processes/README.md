# Cohesive.Processes

Canonical, portable Process semantics for coordinating entity transitions, relation and query evaluations,
interactions, durable waits, timers, parallel branches, and terminal outcomes without binding the definition to a
workflow engine, storage system, or host-language callback.

## Install

```bash
dotnet add package Cohesive.Processes
```

## Canonical Process IR

`Cohesive.Processes.IR.ProcessDefinition` is the persisted semantic authority. A definition is a normalized graph
with stable node and edge identities, typed inputs and results, portable expressions and bindings, exact references
to other canonical definitions, explicit recovery policy, and closed Process-node and AwaitMatch-clause unions.

```csharp
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;

var text = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
var definition = new ProcessDefinition(
    input: text,
    result: text,
    entry: new("persist-cut"),
    nodes:
    [
        new DurableCutProcessNode(
            new("persist-cut"),
            new(new("resume-after-cut"), new("complete"))),
        new ReturnProcessNode(new("complete"), Expr.Const("done"))
    ],
    recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt);

var document = ProcessDefinitionDocuments.Create(
    new("process/example"),
    new("revision/1"),
    definition,
    new(
        new("example-producer", "1"),
        new("examples/process/example"),
        DocumentOrigin.Generated));

var validation = ProcessDefinitionDocuments.Validate(document);
```

The shared `ExecutionDefinitionDocument` owns definition identity, revision, fingerprint, provenance, source maps,
and extensions. `ProcessDefinitionDocuments` is a typed facade over that envelope; it does not introduce another
metadata or fingerprint model.

Static validation checks graph integrity, exact reference families, portable expression types, definite binding
visibility, proven Choice/Match coverage, Fork-token ownership and Join structure, AwaitMatch policies, child
Process protocols, bounded partition-work and recurrence policies, and finite activation. Process v2 uses the same
fixed pure expression-capability closure as Transition v1 rather than the ambient expression catalog. A control-flow
recurrence is valid only when it crosses a Request, InvokeProcess, ForEachPartition, RepeatAcrossActivation,
AwaitMatch, Timer, or explicit durable cut. Fork branches may recur across those boundaries when every finite exit
belongs to the reciprocal Join and at least one structural Join exit exists. The definition contains coordination
facts—not copied aggregate business state, runtime services, adapters, compiled plans, or delegates.

An AwaitMatch clause receiving a Request retains two distinct facts: its typed application payload and a
`ProcessRequestObligationBinding` representing the admitted logical Request envelope. `ReplyProcessNode` must
consume that definitely visible obligation, and linking proves that its Reply contract discharges the exact Request
contract. The Request identity is therefore never reconstructed from an arbitrary application expression.

## Canonical C# authoring

`Cohesive.Processes.Authoring` is a typed C# producer for the same canonical Process IR. Stable semantic identities
are explicit; owner-relative helpers derive only mechanically owned edge, value-binding, and Request-obligation
identities. Typed member selectors are captured immediately as portable `FieldPath` values, and the builder callback
is discarded after the document is created. The resulting handle retains an `ExecutionDefinitionDocument`, not
executable Process control flow.

```csharp
using Cohesive.Execution;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;

var review = ProcessAuthoring.Create<ReviewInput, string>(
    new(
        new("process/review"),
        new("revision/1"),
        new("choose"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new(ProcessAuthoring.Producer),
            new("reviews/process/review"),
            DocumentOrigin.Generated)),
    process =>
    {
        ExecutionNodeId accepted = new("return/accepted");
        ExecutionNodeId rejected = new("return/rejected");
        var approved = process.Input.Field(input => input.Approved);

        process.Choice(
            new("choose"),
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            [
                process.ChoiceCase(
                    new("case/approved"),
                    approved,
                    process.Edge(new("edge/approved"), accepted))
            ],
            process.Fallback(
                new("fallback/rejected"),
                process.Edge(new("edge/rejected"), rejected)));
        process.Return(accepted, process.Constant("approved"));
        process.Fail(rejected, process.Constant("rejected"));
    });

var compilation = review.Compile(new ProcessDefinitionValidationContext());
```

The authoring API exposes every canonical Process node and nested construct: exact Transition and Relation/Query
calls, Requests, events, Signals, Choice and Match, Fork and Join, AwaitMatch and Timer, Reply, durable cuts, child
Processes, bounded partition work, recurrence across activations, and typed terminal outcomes. Arbitrary CLR
callbacks, services, tasks, loops, and suspended host frames cannot enter the persisted definition. Unsupported
portable values, member selectors, expressions, links, contracts, or graph shapes fail during authoring, validation,
or static compilation with source-mapped diagnostics.

Use the explicit input/result contract overload—and explicit output contracts where needed—when top-level CLR
generic types erase semantic optionality or nullable-reference occurrence. `CanonicalValue` is the deliberate escape
hatch for an already-portable expression with an explicitly attested contract; canonical validation remains the
authority for whether the expression and contract agree.

When a Process invokes a Transition or evaluates a Relation/Query, use an exact `ExecutionDefinitionReference`.
Supply `ProcessDefinitionValidationContext` while linking to prove that the referenced definition family and its
input/result contracts match the call site. Interaction nodes resolve their exact typed references through an
`InteractionContractCatalog`. Referenced definitions remain the semantic authorities; the Process does not embed
or duplicate them. `ProcessDefinitionLink.TryCreateTransition` derives evidence from a validated canonical
Transition document. Until Relations adopts the shared execution-definition envelope, Relation/Query evidence is
an explicit linker attestation boundary rather than a document-derived proof.

## Retired execution authority

The callback-bearing `Cohesive.Processes.Model` graph, its source generator, and the single-cursor
`Cohesive.Processes.Runtime.ProcessCheckpoint` execution path are no longer part of the shipped assemblies. They
cannot be selected as a compatibility fallback or interpreted by an adapter. Canonical documents compile through
`ProcessStaticCompiler`, execute through `ProcessReferenceInterpreter`, and become durable through
`Cohesive.Storage.Processes.ProcessDurableRuntime` and `IProcessDurableStore`.

The DurableTask package retains authority-neutral task-hub query projections only. A future DurableTask execution
adapter must consume compiled canonical definitions and implement or compose the canonical durable-store boundary;
it must not revive registry-by-name definitions, delegate replay, or single-cursor checkpoints.

## Related packages

- `Cohesive.Transitions` for canonical aggregate transition semantics.
- `Cohesive.Relations` for canonical relation and query semantics.
- `Cohesive.Storage` for canonical durable checkpoints, the Process-store contract, and its reference runtime.
- `Cohesive.Adapters.DurableTask` for authority-neutral task-hub execution-status queries.

```csharp
public sealed record ReviewInput(bool Approved);
```
