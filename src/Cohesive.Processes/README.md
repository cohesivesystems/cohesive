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

When a Process invokes a Transition or evaluates a Relation/Query, use an exact `ExecutionDefinitionReference`.
Supply `ProcessDefinitionValidationContext` while linking to prove that the referenced definition family and its
input/result contracts match the call site. Interaction nodes resolve their exact typed references through an
`InteractionContractCatalog`. Referenced definitions remain the semantic authorities; the Process does not embed
or duplicate them. `ProcessDefinitionLink.TryCreateTransition` derives evidence from a validated canonical
Transition document. Until Relations adopts the shared execution-definition envelope, Relation/Query evidence is
an explicit linker attestation boundary rather than a document-derived proof.

## Compatibility authoring and runtimes

The existing `Cohesive.Processes.Model`, authoring, source-generation, and runtime APIs remain compatibility
surfaces while canonical C# lowering and the finite Process runtime are introduced. Those APIs currently build and
execute delegate-bearing node objects and must not be treated as durable semantic authority. Existing DurableTask,
local runtime, effect-handler, transaction-gateway, and relation-query evaluation integrations continue to serve
legacy definitions until they are migrated to interpret canonical IR.

## Related packages

- `Cohesive.Transitions` for canonical aggregate transition semantics.
- `Cohesive.Relations` for canonical relation and query semantics.
- `Cohesive.Storage` for durable checkpoint and repository interpretations.
- `Cohesive.Adapters.DurableTask` for the current compatibility orchestration adapter.
