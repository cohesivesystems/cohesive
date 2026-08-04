# Cohesive.Processes

Canonical, portable Process semantics for coordinating entity transitions, relation and query evaluations,
interactions, durable waits, timers, parallel branches, and terminal outcomes without binding the definition to a
workflow engine, storage system, or host-language callback.

## Install

```bash
dotnet add package Cohesive.Processes
# Required only for GenerateProcessDefinition computation-expression authoring:
dotnet add package Cohesive.Analyzers
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

### Computation-expression authoring

Human-facing definitions may use ordinary C# locals, `await`, `if`/`else`, exact-value `switch`, and `return` by
annotating a syntax-only method with `GenerateProcessDefinition`. `await` is reserved for semantic Process
operations. Ordinary locals remain pure: the generator translates them into the fixed portable `Expr` closure and
fuses them into the nearest query input, Transition input, Request payload, predicate, or terminal result. It emits
hidden output bindings and canonical builder calls in `Define`; it does not emit Compute nodes, CLR delegates, or an
executable workflow state machine.

```csharp
[GenerateProcessDefinition(nameof(Run))]
public static partial class ApproveCustomerProcess
{
    static async ProcessTask<ApproveCustomerResult> Run(
        ProcessContext process,
        ApproveCustomerInput input)
    {
        var lookup = new CustomerLookup(input.Email);
        var customerId = await process.Query<CustomerId>(CustomerByEmail, lookup);
        var customer = await process.Read<Customer>(CustomerById, customerId);

        if (customer.Status == CustomerStatus.Suspended)
            return new(customer.Id, ApprovalDisposition.Rejected, deliveryId: null);

        var approval = await process.Transition<Approval>(
            ApproveCustomer,
            customer.Id,
            new ApproveTransitionInput(input.Reason));
        var message = new WelcomeMessage(customer.Email, "Welcome " + approval.DisplayName);
        var delivery = await process.Effect<Delivery>(SendWelcome, WelcomeSent, message);

        async ProcessTask Audit()
        {
            var receipt = await process.Effect<OperationReceipt>(
                RecordApprovalAudit,
                AuditRecorded,
                new ApprovalAudit(customer.Id, approval.DisplayName));
        }

        async ProcessTask NotifyOwner()
        {
            var receipt = await process.Effect<OperationReceipt>(
                SendOwnerNotification,
                OwnerNotified,
                new OwnerNotification(customer.Id, delivery.Id));
        }

        await process.ForkJoin(Audit(), NotifyOwner());
        return new(customer.Id, ApprovalDisposition.Approved, delivery.Id);
    }
}
```

`Read` is deliberately an authoring alias for exact Relation/Query evaluation; it does not restore a separate
Process-native entity model. `Effect` lowers to an exact Request contract and selected terminal outcome. Generated
factories accept `ProcessAuthoringMetadata`, honor an explicit entry when it agrees with the derived entry, and
otherwise materialize deterministic identities from semantic structure. Inserting or refactoring a pure local does
not renumber effectful nodes. `ForkJoin` accepts two or more parameterless local `async ProcessTask` branch
functions and lowers them to the canonical Fork/Join pair. Its convention is an all-branches, fail-fast Join that
awaits remaining branches, does not expose completion order, and resolves ties by stable branch identity; the local
functions and their compiler state machines are never retained.

Durable races and partial convergence retain the same readable local-branch style while requiring the canonical
policies that affect recovery and determinism:

```csharp
async ProcessTask OnApproved(Approval approval) { /* semantic Process operations */ }
async ProcessTask OnTimeout() { /* semantic Process operations */ }

await process.AwaitMatch(
    clauses:
    [
        process.Signal<Approval>(Approved, OnApproved, priority: 10),
        process.Deadline(expiresAt, OnTimeout)
    ],
    arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
    lateInput: ProcessAwaitInputDisposition.Observe,
    staleInput: ProcessAwaitInputDisposition.Reject,
    duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
    missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
    retentionHorizon: TimeSpan.FromDays(7));

var winner = await process.ForkAny(
    branches: [Audit(), Notify()],
    policy: ProcessJoin.Any(ProcessJoinCancellationPolicy.CancelRemaining));
```

`AwaitMatch` local functions and inline pure guards are source syntax only; generated documents contain canonical
interaction bindings, Request obligations, timer clauses, arbitration, retention, and input-disposition policies.
Multi-outcome `Effect` uses `process.Outcome<T>(outcome, LocalBranch)` and binds the selected typed result only inside
that outcome branch. `ForkAny` returns one `ProcessJoinWinner<T>`; `ForkRequired` returns an immutable selection in
canonical winner order. Neither can be deconstructed as an all-branch tuple.

Exact child invocation reuses the same outcome syntax and lowers through the canonical child Request/Reply protocol:

```csharp
await process.InvokeProcess(
    process: TrainModel,
    contract: StartTraining,
    outcomeMapping: TrainingOutcomes,
    input: trainingInput,
    purpose: ProcessChildPurpose.Work,
    cancellation: ProcessChildCancellationPolicy.Propagate,
    outcomes:
    [
        process.Outcome<TrainingReceipt>(TrainingOutcomes.Completed, OnCompleted),
        process.Outcome<TrainingFailure>(TrainingOutcomes.Failed, OnFailed),
        process.Outcome<TrainingFailure>(TrainingOutcomes.Cancelled, OnCancelled),
        process.Outcome<TrainingFailure>(TrainingOutcomes.Terminated, OnTerminated)
    ]);
```

Finite partition work keeps collection traversal, identities, child inputs, and resource limits in one canonical
node. Projection lambdas are pure source syntax, not runtime callbacks or enumerators; successful settlement resumes
the normal C# flow while the named failure branch represents the canonical failed edge:

```csharp
await process.ForEachPartition<TenantPlacement, RebuildInput>(
    partitions: request.Placements,
    progressIdentity: placement => placement.Tenant,
    process: RebuildTenant,
    contract: StartTenantRebuild,
    outcomeMapping: RebuildOutcomes,
    childInput: placement => new RebuildInput(placement.Tenant, placement.Index),
    limits: new ProcessWorkLimits(100, 10, 8),
    failure: ProcessPartitionFailurePolicy.FailFast,
    capacityIdentity: placement => placement.Backend,
    capacityDomains: [new("elastic-primary", maximumParallelism: 4)],
    cancellation: ProcessChildCancellationPolicy.Propagate,
    failed: OnPartitionFailure);
```

Finite recurrence is authored as one typed local occurrence plus pure termination and progress projections. Every
admitted repeat crosses the canonical durable activation cut, and both total occurrences and unchanged progress are
explicitly bounded. The final completed occurrence result remains available to ordinary sequential C# flow:

```csharp
async ProcessTask<TrainingStatus> PollTraining()
{
    await process.Timer(nextPollAt);
    var status = await process.Query<TrainingStatus>(TrainingStatusQuery, trainingId);
    return status;
}

var status = await process.RepeatAcrossActivation(
    occurrence: PollTraining(),
    continueWhen: observation => observation.State == "running",
    progress: observation => observation.Version,
    policy: new ProcessRecurrencePolicy(
        maximumOccurrences: 100,
        maximumUnchangedProgressOccurrences: 5),
    exhausted: OnPollingExhausted,
    stalled: OnTrainingStalled);
```

Compensation and reconciliation deliberately reuse exact child Process invocation rather than introducing a second
recovery protocol. State the purpose on the existing durable child operation:

```csharp
await process.InvokeProcess(
    process: UndoReservation,
    contract: StartUndoReservation,
    outcomeMapping: UndoOutcomes,
    input: reservation,
    purpose: ProcessChildPurpose.Compensation,
    cancellation: ProcessChildCancellationPolicy.Propagate,
    outcomes:
    [
        process.Outcome<UndoReceipt>(UndoOutcomes.Completed, OnUndone),
        process.Outcome<UndoFailure>(UndoOutcomes.Failed, OnUndoFailed),
        process.Outcome<UndoFailure>(UndoOutcomes.Cancelled, OnUndoCancelled),
        process.Outcome<UndoFailure>(UndoOutcomes.Terminated, OnUndoTerminated)
    ]);
```

Use `ProcessChildPurpose.Reconciliation` for recovery work with the same protocol. Host `while`, `for`, `foreach`,
recursion, mutable loop state, and runtime-derived recurrence policy are rejected by the generator.

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

The callback-bearing `Cohesive.Processes.Model` graph, its runtime-delegate source generator, and the single-cursor
`Cohesive.Processes.Runtime.ProcessCheckpoint` execution path are no longer part of the shipped assemblies. They
cannot be selected as a compatibility fallback or interpreted by an adapter. Canonical documents compile through
`ProcessStaticCompiler`, execute through `ProcessReferenceInterpreter`, and become durable through
`Cohesive.Storage.Processes.ProcessDurableRuntime` and `IProcessDurableStore`.

The active computation-expression generator is only a C# syntax producer for canonical IR. Its generated factory
uses `ProcessBuilder<TInput,TResult>` and discards all construction state before returning; it is not a restoration
of the retired callback-bearing model or runtime.

The DurableTask package retains authority-neutral task-hub query projections only. A future DurableTask execution
adapter must consume compiled canonical definitions and implement or compose the canonical durable-store boundary;
it must not revive registry-by-name definitions, delegate replay, or single-cursor checkpoints.

## Related packages

- [Execution Kernel adoption and migration guide](../../docs/EXECUTION_KERNEL_GUIDE.md) for the end-to-end canonical lifecycle, executable examples, and retired-surface replacements.
- `Cohesive.Transitions` for canonical aggregate transition semantics.
- `Cohesive.Relations` for canonical relation and query semantics.
- `Cohesive.Storage` for canonical durable checkpoints, the Process-store contract, and its reference runtime.
- `Cohesive.Adapters.DurableTask` for authority-neutral task-hub execution-status queries.

```csharp
public sealed record ReviewInput(bool Approved);
```
