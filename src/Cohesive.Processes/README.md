# Cohesive.Processes

Canonical, portable Process semantics for coordinating entity transitions, relation/query evaluations, durable
interactions, waits, parallel work, recurrence, and terminal outcomes without binding the definition to a workflow
engine, storage system, or host-language callback.

## Install

```bash
dotnet add package Cohesive.Processes
dotnet add package Cohesive.Analyzers
```

`Cohesive.Analyzers` supplies the expression-first C# frontend. Add it as an analyzer when using project references.

## Author a Process in C#

Human-written Processes start with a syntax-only `async ProcessTask<T>` method marked by
`GenerateProcessDefinition`. `await` binds results from semantic Process operations; ordinary local expressions are
fused into the nearest effectful operation or terminal result rather than becoming Compute nodes.

<!-- <docs:sequential-process> -->
```csharp
[GenerateProcessDefinition(nameof(Run))]
public static partial class CustomerQueryProcess
{
    /// <summary>Exact Relation reference used by the generated Process.</summary>
    public static ExecutionDefinitionReference Relation { get; } = new(
        new("relation/customer-query"),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('1', 64)));

    static async ProcessTask<string> Run(
        ProcessContext process,
        string input)
    {
        var queryInput = input;
        var row = await process.Query<string>(Relation, queryInput);
        return row;
    }
}
```
<!-- </docs:sequential-process> -->

This exact excerpt is compiled and exercised by
[`ProcessComputationAuthoringTests.cs`](../Cohesive.Tests/ExecutionKernel/ProcessComputationAuthoringTests.cs); a
documentation invariant test prevents the README from drifting from that executable source. The generated `Define`
factory accepts `ProcessAuthoringMetadata` and returns a typed handle containing the canonical execution-definition
document and validation result. The annotated `Run` method is never invoked.

When a Relation is authored through the typed expression frontend, pass its canonical handle directly. C# infers
both the input and singular result types, while generation lowers the handle's exact reference to the same canonical
evaluation node used by the raw overload:

```csharp
var row = await process.Query(CustomerRelations.ById, input);
var read = await process.Read(CustomerRelations.ById, input);
```

The Relation document remains authoritative for semantics and fingerprinting. The handle carries only typed
projections and captured dependency evidence; `CreateProcessDefinitionLink()` derives validation evidence from that
same handle. Raw `ExecutionDefinitionReference` overloads remain available for imported definitions and advanced
lowering code.

The same direct syntax accepts a typed canonical hosted Query when the call includes acquisition policy outside a
portable logical graph:

```csharp
var source = await process.Read(EventSourceQueries.SchemaMapping, start);
```

The hosted-Query document—not the Process call site or its runtime handler—owns the invocation/result contracts,
implementation identity and version, exact definition dependencies, portable configuration, and fingerprint.
Generation still emits the unchanged canonical `EvaluateRelationProcessNode`, and
`CreateProcessDefinitionLink()` derives validation evidence from that one authority. Runtime registration is a
separate interpretation of the exact hosted-Query document.

Processes can also emit canonical one-way interactions directly. The typed calls accept exact contract references
and portable C# expressions, then lower to the existing event and Signal nodes:

```csharp
await process.EmitEvent(
    TrainingDatasetMaterializationsGenerated.Contract,
    generated,
    id: new("dataset/publish-completed"),
    nextRole: "published");

await process.SendSignal(
    TrainingSignals.Refresh,
    input.OperatorTarget,
    generated,
    id: new("dataset/signal-operator"),
    nextRole: "sent");
```

These calls create no host callback or parallel messaging model. The exact contract, portable payload and target
expressions, stable node and edge identities, and source attribution are retained in canonical IR. At execution,
the Process continuation and activation derive the envelope's emission, correlation, causation, idempotency,
authority, delivery, and provenance evidence. The selected interpreter must still satisfy its declared publication
and Signal-delivery capability requirements.

The same syntax supports typed Transition results, Requests/effects, entity reads represented by Relations,
`if`/`else`, exact `switch`, explicit Choice/Match policies, durable waits, tuple-valued Fork/Join, bounded admission,
child Processes, partition work, and recurrence. Use the executable
[`ApproveCustomerProcess`](../Cohesive.Tests/ExecutionKernel/ProcessComputationAuthoringTests.cs) for a compact
query/read/Transition/effect/Fork-Join example. Use the Motion DQ
[`onboarding`](../Cohesive.ExecutionKernel.TestFixtures/MotionDq/MotionDqProcessDefinition.cs) and
[`monitoring`](../Cohesive.ExecutionKernel.TestFixtures/MotionDq/MotionDqMonitoringProcessDefinition.cs) definitions
for business-shaped branching, Request outcomes, durable waits, bounded parallelism, polling, escalation, and
recurrence. Those executable definitions remain the source of truth; this smaller excerpt illustrates the typed-wait
shape in isolation.

When one Process invokes another, derive the closed Request/Reply protocol from the typed child handle:

```csharp
var childProtocol = ChildProcess.Define(metadata).InvocationProtocol(
    new("request/customer-normalization"),
    new("1"),
    ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(30)),
    provenance);
```

The child Process remains authoritative for its exact definition reference and portable input/result contracts.
The invocation protocol owns the Request identity, response policy, terminal mapping, generated Request/Reply
documents, and the non-success evidence schemas. Successful completion carries the child's exact `TResult`.
Unhandled child execution failure carries `ProcessChildFailure`, a portable projection of the child's canonical
terminal node and retained diagnostics. Cancellation and forced termination carry the canonical terminal kind but
do not fabricate a child result. Schema revisions, outcome identities, and the Reply identity prefix have
deterministic defaults and can be supplied explicitly when they are part of an established compatibility contract.
Only a valid child using `ContinueAttempt` recovery can author this protocol, matching the exact-attempt join
semantics enforced during Process linking.

Within a Process computation, the typed protocol removes the repeated child reference, Request reference, outcome
mapping, and explicit outcome identities. The four named handlers remain exhaustive, but only successful completion
receives the protocol's `TResult`:

```csharp
await process.InvokeProcess(
    protocol: childProtocol,
    input: source,
    purpose: ProcessChildPurpose.Work,
    cancellation: ProcessChildCancellationPolicy.Propagate,
    completed: Completed,
    failed: Failed,
    cancelled: Cancelled,
    terminated: Terminated);

async ProcessTask Completed(NormalizedCustomer result) { }
async ProcessTask Failed(ProcessChildFailure failure) { }
async ProcessTask Cancelled() { }
async ProcessTask Terminated() { }
```

`purpose` and `cancellation` remain explicit because they describe this invocation occurrence, not the reusable
protocol. A domain rejection that is part of ordinary child behavior remains an authored result rather than being
reclassified as an operational failure. The raw exact-reference overload remains available to generators and
importers. Both forms lower to the same canonical `InvokeProcessProcessNode` and therefore have identical
interpreter and replay behavior.

Typed Request protocols can also project heterogeneous terminal outcomes into an ordinary closed C# family:

```csharp
var outcome = await process.Effect(trainingSubmissionProtocol, submission);
switch (outcome)
{
    case TrainingSubmissionAccepted(var accepted):
        return accepted.SubmissionId;
    case TrainingSubmissionRejected(var failure):
        return failure.Reason;
    case TrainingSubmissionTimedOut(var failure):
        return failure.Reason;
}
return process.Unreachable<string>();
```

The three-generic protocol projection names the request payload, closed result-family root, and its typed case
descriptor set. Each case has one public payload property, and each public descriptor property associates one
source-only case with one canonical protocol outcome in declaration order. The analyzer uses that metadata as the
complete case inventory, requires every case exactly once in the immediately following switch, and binds the case's
direct positional or property payload to the existing canonical Request output. Outcomes that share a payload type
remain distinct because their case types differ.

The record family is not persisted and no case object is created at runtime. Outcome identity, kind, schema, and
Reply mapping remain owned by the canonical Request protocol; generated branches lower to the existing
`RequestProcessNode` and `RequestProcessOutcome` model. Default branch identity is derived from the Request node and
canonical outcome id. The raw outcome-array overload retains explicit identity control for imported or established
graphs. See [the typed Request outcome projection decision](../../docs/decisions/typed-request-outcome-projection.md)
for the native C# union replacement boundary.

Typed durable races bind a closed source-only result family and consume it with an immediately following exhaustive
type switch:

```csharp
var review = await process.AwaitMatch<CustomerReviewOutcome>(
    clauses:
    [
        process.Event<DocumentReviewSubmitted>(
            ReviewSubmitted,
            priority: 10,
            when: submitted => submitted.TaskId == reviewTask.Id),
        process.Deadline<DocumentReviewTimedOut>(reviewTask.DueAt)
    ],
    arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
    lateInput: ProcessAwaitInputDisposition.Observe,
    staleInput: ProcessAwaitInputDisposition.Reject,
    duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
    missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
    retentionHorizon: TimeSpan.FromDays(30));

switch (review)
{
    case DocumentReviewTimedOut _:
        return TimedOut();
    case DocumentReviewSubmitted { Decision: var decision }:
        await ApplyDecision(decision);
        break;
}
```

The case records and bound `review` local are C# projection types only. Generation fuses each switch section into
the corresponding canonical `AwaitMatch` clause continuation; no union wrapper, discriminator, callback, or CLR
state machine is serialized. Every declared alternative must appear exactly once, and adding a clause makes the
switch diagnostically incomplete until its case is handled. Interaction case values are the exact typed payload;
timer cases are markers and use lexically visible due-time data rather than manufacturing a runtime value. Runtime
admission still addresses the exact durable Process token; a portable guard may further constrain the originating
business occurrence without replacing that canonical target.

## One semantic lifecycle

Expression source, canonical IR, compiled plans, and runtime evidence have distinct ownership:

| Stage | Authority and lifetime |
| --- | --- |
| Expression source | A human-readable C# producer. The generator reads its syntax; the method, locals, local branch functions, and compiler state machines are never execution authority. |
| Canonical IR | The persisted `ExecutionDefinitionDocument` containing `Cohesive.Processes.IR.ProcessDefinition`. It is normalized, versioned, fingerprinted, inspectable, and is the semantic source of truth. |
| Compiled plan | A target-independent or adapter-specific interpretation derived from one exact canonical definition plus declared capabilities and linking evidence. It is replaceable and retains provenance to the document. |
| Runtime evidence | Durable continuations, attempts, inputs, outputs, operation receipts, traces, and control state produced while interpreting the compiled plan. It references the exact definition fingerprint and never requires authoring source to resume. |

Persist and restore the canonical document. Do not persist an expression tree, generated builder callback, CLR task,
compiled plan, or authoring session as the Process definition. Static compilation and restored execution consume the
document plus explicit linking, policy, capability, and runtime evidence.

## Identity and compatibility

Omitting `ExecutionNodeId` uses deterministic conventions. Conventions are appropriate for local structural details
whose identity has no independent compatibility promise. They are not a substitute for explicit durable identity.

| Use conventions when | Use explicit identities when |
| --- | --- |
| A node is local structure and may legitimately receive a new identity when its semantic source path changes. | A persisted continuation, checkpoint, migration, external target, operational command, or cross-revision contract names the node. |
| A derived branch, edge, output, or outcome is owned entirely by an explicitly identified parent. | A revision must preserve byte-identical canonical IR or resume state authored by an earlier revision. |
| The definition is new and no deployed state or external integration depends on its internal topology. | Independent producers must converge on the same established identity or an operator must address the construct directly. |

Convention-derived decisions are deterministic and recorded in the source map. Inserting unrelated constructs does
not globally renumber semantic roles, but moving or changing structurally identified operations may intentionally
change their identities and therefore the fingerprint. Treat a change to an explicitly durable identity as a
compatibility/versioning decision. Motion DQ intentionally spells out durable identities because its reference
definitions lock recovery and cross-revision behavior.

## Restricted computation model

The expression frontend accepts only source constructs it can lower into the finite canonical Process model.
Semantic operations use `await`; pure locals use the portable expression closure. Named local functions may describe
branches, but are erased after lowering. Arbitrary CLR services, I/O, tasks, reflection, mutable loops, recursion,
captured runtime delegates, and host-language suspension cannot enter canonical IR. Unsupported syntax is a source
diagnostic, not a callback deferred to runtime.

Durability- and scheduling-relevant policy remains explicit. Await arbitration and input disposition, Fork/Join
completion and cancellation, admission limits, child cancellation, recurrence bounds, and compensation purpose are
canonical facts. The C# frontend does not infer weaker guarantees or hide those policies behind convenient syntax.

`Read` is an authoring alias for exact Relation/Query evaluation; it does not create a second entity-read execution
model. `Effect` lowers to a typed durable Request and selected terminal outcome. `EmitEvent` and `SendSignal` lower
to the existing canonical one-way interaction nodes; their target and payload values must remain within the portable
expression closure, and a Signal requires an explicit non-null semantic target expression. Referenced Processes,
Transitions, Relations/Queries, and interaction contracts use exact definition identity, revision, and fingerprint
evidence.

## Canonical validation and execution

`ProcessDefinitionDocuments.Validate` checks graph integrity, exact references, portable expression types, binding
visibility, Choice/Match coverage, Fork-token and Join structure, AwaitMatch policies, child protocols, bounded work,
recurrence, and finite activation. `ProcessStaticCompiler` consumes the persisted document and a
`ProcessDefinitionValidationContext` containing exact linked-definition and interaction evidence.

`ProcessReferenceInterpreter` is the reference in-memory interpretation. Durable execution composes compiled
canonical definitions with `Cohesive.Storage.Processes.ProcessDurableRuntime` and `IProcessDurableStore`. Other
interpreters and adapters must declare supported capabilities and preserve the canonical semantics or emit precise
diagnostics.

The synchronous `IProcessReferenceHost` remains the evidence port used by the pure reducer. Infrastructure that
performs naturally asynchronous work implements `IAsyncProcessReferenceHost` and invokes the same semantic
authority through `ProcessReferenceInterpreter.ActivateAsync`. That driver suspends on an unmaterialized exact
host occurrence, awaits physical execution with `OperationContext` cancellation, retains the evidence by
continuation/attempt/activation/token/node/occurrence, and re-enters the unchanged synchronous reducer. Physical
cancellation returns no partial Process decision and is distinct from authored semantic cancellation.

Hosted Queries bind their runtime handlers separately from their canonical documents:

```csharp
var handlers = new HostedQueryHandlerCatalog([
    HostedQueryHandlerRegistration.CreateOutcome(
        EventSourceQueries.SchemaMapping,
        async (context, evaluation, start) =>
        {
            var source = await repository.ReadPinnedAsync(start, context.CancellationToken);
            return source is null
                ? HostedQueryHandlerOutcome<PinnedSource>.Failed(new(
                    "source.missing",
                    DiagnosticSeverity.Error,
                    "The admitted source no longer exists."))
                : HostedQueryHandlerOutcome<PinnedSource>.Completed(source);
        })
]);
```

The immutable catalog dispatches only by complete definition identity, revision, and fingerprint, validates the
portable input and output contracts, and passes the complete `ProcessRelationEvaluation` to the handler unchanged.
A change to contracts, implementation version, dependencies, or portable configuration changes the document
fingerprint and cannot silently reach the old handler. Handler delegates, repositories, credentials, and deployment
state never enter canonical content. `CreateOutcome` preserves a statically typed success while allowing an expected
inability to produce that value to become structured Process failure evidence. Thrown exceptions remain physical
execution failures. `SynchronousProcessReferenceHostAdapter` is the explicit bounded compatibility path; it checks
cancellation before invocation but cannot interrupt a synchronous call already in progress.

`IProcessExecutionTraceRepository` is the opt-in runtime boundary for reading retained payload-safe traces without
adding them to ordinary execution status or listing records. Its result distinguishes not found, in-progress,
available, and terminal-without-artifact executions. Application reads use trusted authority scope plus logical
Process identity; physical-key reads remain an engine-administration path. An available result carries the versioned
portable `ProcessExecutionTraceArtifact`, whose explicit missing-prefix count prevents pre-retention gaps from being
mistaken for complete coverage. `ProcessExecutionTraceJsonSerializer` emits and verifies its strict canonical wire.
Neither the artifact nor its JSON contains the physical repository key. The availability envelope and collection
artifact do not replace `NormalizedExecutionTrace`; they describe acquisition state and coverage of those shared
per-activation authorities.

`IProcessExecutionRepository` is the provider-neutral boundary for current safe status acquisition. Application and
API reads use its logical address formed by a server-resolved `InteractionAuthorityScope` and canonical
`ProcessInstanceId`; engine administration may use the separate exact physical-key overload. A retained pending
admission may have an exact definition but no `ExecutionStatus`, and consumers must not manufacture one from a
provider lifecycle value.

`IProcessExecutionExplainRepository` is the asynchronous runtime boundary for composing retained observations into
the existing `ExecutionExplainArtifact`. Pending and active executions may yield partial lifecycle artifacts without
trace evidence; a missing execution yields no artifact. The repository does not authorize another explain DTO or
re-execution path. `ProcessExecutionRecord.Definition` retains the exact canonical identity independently of optional
runtime status so admission-window observations can still resolve their deployed definition. An untrusted request
must never choose the authority or tenant supplied to either logical read.

## Interpreter capability realization

`ProcessInterpreterRequirementCollector` acquires a target-neutral inventory from one exact
`CompiledProcessPlan`. Concrete construct requirements come directly from the closed `ProcessNode` persisted-union
metadata; the collector does not maintain a second node-kind enum or recognizer switch. Cross-cutting requirements
are derived from the complete graph, linked definitions and interaction contracts, effect summary, and explicit
compilation demands.

A `ProcessInterpreterCapabilityProfile` declares one target's versioned capability evidence using the shared
`CapabilityRealizationKind`: native, composed, constrained, or unavailable. The profile is physical-target evidence,
not Process semantics. `ProcessInterpreterRealizationCompiler` matches the source inventory to that evidence and
always produces one `ProcessInterpreterRealizationDecision` per inventory item. Missing declarations become explicit
unavailable decisions and errors; multiple declarations are ambiguous errors; constrained decisions retain named
operating boundaries and warnings. `ProcessInterpreterRealizationLedger.ValidateCoverage` independently rejects a
missing, duplicated, or uninventoried disposition before target-specific planning or execution.

These contracts deliberately contain no Durable Task, storage-provider, or workflow-engine types. A target adapter
supplies its profile and consumes only a successful exhaustive report when compiling a physical realization plan.
The first intended consumer is the Durable Task parallel interpreter, but the same contracts also govern native
Postgres, Cosmos, simulation, and future orchestration profiles.

## Advanced lowering escape hatch

`ProcessAuthoring.Create` and `ProcessBuilder<TInput,TResult>` remain public, advanced APIs because source generators,
importers, compiler tests, and infrastructure may need direct construction of the closed Process-node union. They are
not the primary application-authoring surface and are hidden from ordinary IntelliSense through
`EditorBrowsableState.Advanced`. Their callbacks must be finite and synchronous, are discarded immediately, and
cannot become persisted or runtime authority.

The former `CreateExpression` collection DSL has been removed. It covered only sequential graph construction and
duplicated the human-facing role now owned by the more capable generated C# computation frontend. Migrate those
definitions to `GenerateProcessDefinition`; use the advanced builder only when code is itself lowering or importing
canonical structure.

## Retired execution authority

The callback-bearing `Cohesive.Processes.Model` graph, runtime-delegate source generator, and single-cursor
`Cohesive.Processes.Runtime.ProcessCheckpoint` path are not shipped. Canonical documents compile through
`ProcessStaticCompiler`, execute through declared interpreters, and become durable through the Storage-owned Process
runtime. Restoring a Process never loads the expression source, a builder callback, or an authoring state machine.

The DurableTask package currently retains authority-neutral task-hub query projections only. The accepted future
execution target is a parallel durable interpreter of exact compiled canonical definitions. It need not implement
`IProcessDurableStore` when Durable Task history directly preserves the declared semantics, but it must publish an
explicit capability closure, reject missing or weaker realizations, and pass differential conformance against the
reference interpreter. It must not revive registry-by-name definitions, delegate replay, single-cursor checkpoints,
or one opaque activity for an entire Process. See the accepted
[Durable Task Process interpreter decision](../../docs/decisions/durable-task-process-interpreter.md).

## Related packages

- [Execution Kernel adoption and migration guide](../../docs/EXECUTION_KERNEL_GUIDE.md)
- `Cohesive.Transitions` for canonical entity Transition semantics
- `Cohesive.Relations` for canonical relation and query semantics
- `Cohesive.Storage` for durable checkpoints, control, and the Process-store contract
- `Cohesive.Processes.Distribution` for optional portable worker pools, durable claims, capacity, leases, fencing, and recovery
- `Cohesive.Adapters.DurableTask` for current authority-neutral task-hub status projections and the accepted future parallel interpreter target
