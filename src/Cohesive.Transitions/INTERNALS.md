# Cohesive.Transitions internals

This document contains the explicit identity authoring, canonical IR, compilation, and interpretation details behind
the [package overview](README.md).

Canonical entity-transition IR, authoring, compilation, interpretation, and entity-shape primitives.

## Install

```bash
dotnet add package Cohesive.Transitions
```

## Use When

- You want entities to declare semantic fields and invariants while authoring transitions as durable canonical IR.
- You need transition interpretation to produce explicit sparse patches, interaction emissions, and execution evidence.
- You want domain behavior represented as a model that can later be interpreted by storage, process, API, or UI adapters.

## Semantic Authority

`Cohesive.Transitions.IR` is the canonical persisted semantic authority for execution-kernel
Transitions. Its structured definitions are stored through the shared execution-definition envelope
and remain inspectable without the original authoring assembly.

Typed C# authoring through `TransitionAuthoring` is a producer of that IR, not a second execution
model. The returned `Transition<TEntity, TInput, TOutcome>` contains only a canonical
`ExecutionDefinitionDocument` and its validation result. It does not retain the builder callback,
expression trees, an `Apply` delegate, entity state, or a runtime service. Persist the document; a
consumer can deserialize, validate, compile, and interpret it without loading the authoring assembly.

The earlier flat `Cohesive.Transitions.Model.TransitionDefinition`, delegate-backed two-parameter
`Transition` handle, CLR effect-handler surface, and local apply runtime have been removed. Entity
definitions now own shape and invariant semantics only; transitions are exact canonical documents.

Entity shapes may remain genuinely inline, or bind an exact graph-qualified root through
`EntityShapeGraphBinding`. A graph-backed definition retains the immutable `ShapeGraphDocument` as
the named-type authority; state validation resolves nested structural, enum, and union types directly
from that snapshot without copying their fields into inline object types. `EntityShapeGraphValidator`
fails closed with deterministic diagnostics for missing bindings or types, duplicate identities,
incompatible graph revisions or roots, and recursive named components. Runtime state creation refuses
an invalid binding through `EntityShapeGraphValidationException`, which retains those diagnostics.
The fluent `EntityBuilder.ShapeGraph(...)` projection lowers to the same graph-backed definition as
direct IR construction and preserves the graph document's provenance metadata.

## Canonical C# Authoring

The C# frontend accepts a finite, typed syntax for inputs, observations, absent-subject initialization,
admission rules, lexical
locals, ordered `Choice` and exact `Match` branches, algebraic sparse updates, interaction emissions,
Machine movements, candidate-state invariants, and typed outcomes. Every definition, revision,
body, rule, branch, binding, update, emission, movement, and outcome receives an explicit stable
identity. Nested body identities are derived deterministically from their owning case or fallback;
source file paths and line numbers never participate in identity or fingerprinting.

```csharp
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

public enum LoadStatus { Draft, Assigned }

public enum AssignCarrierOutcome { Assigned, NotDraft, InvalidCarrier }

public sealed record AssignCarrierInput(string CarrierId);

public sealed class Load : Entity<Load>
{
    public Load()
    {
        Status = Field(nameof(Status), LoadStatus.Draft);
        CarrierId = Field<string?>(
            nameof(CarrierId),
            initialValue: null,
            configure: field => field.Optional());
    }

    public Field<LoadStatus> Status { get; }

    public Field<string?> CarrierId { get; }
}

var metadata = new TransitionAuthoringMetadata(
    new("transition/load/assign-carrier"),
    new("revision/1"),
    new("assign-carrier/body"),
    new(
        new(TransitionAuthoring.Producer),
        new("src/domain/Load.cs"),
        DocumentOrigin.User),
    displayName: "Assign carrier");

var authored = TransitionAuthoring.Create<Load, AssignCarrierInput, AssignCarrierOutcome>(
    Load.Define().Shape,
    metadata,
    transition =>
    {
        transition.Requires(
            new("assign-carrier/admit/draft"),
            (load, _) => load.Status == LoadStatus.Draft,
            (_, _) => AssignCarrierOutcome.NotDraft);

        transition.Choose(new("assign-carrier/validate"), choice => choice
            .Case(
                new("assign-carrier/valid"),
                (_, input) => input.CarrierId != "",
                valid => valid
                    .Set(
                        new("assign-carrier/set-carrier"),
                        load => load.CarrierId,
                        (_, input) => input.CarrierId)
                    .Set(
                        new("assign-carrier/set-status"),
                        load => load.Status,
                        LoadStatus.Assigned)
                    .Return(
                        new("assign-carrier/assigned"),
                        TransitionOutcomeDisposition.Applied,
                        AssignCarrierOutcome.Assigned))
            .Fallback(
                new("assign-carrier/invalid"),
                invalid => invalid.Return(
                    new("assign-carrier/rejected"),
                    TransitionOutcomeDisposition.DomainRejected,
                    AssignCarrierOutcome.InvalidCarrier)));

        transition.Invariant(
            new("assign-carrier/invariant/carrier-required"),
            load => load.Status != LoadStatus.Assigned || load.CarrierId != null);
    });
```

The callback above is construction-time syntax only. `authored.Document` is the sole authority, and
`authored.Reference` is the exact definition/revision/fingerprint reference other semantic blocks
should retain. Supplying the same explicit identities and semantics through direct IR produces the
same normalized definition and fingerprint. Display text, C# call-site locations, and source-map
entries are attribution metadata and are excluded from the semantic fingerprint.

The frontend deliberately rejects arbitrary C#. Expressions may reference declared input,
observation, and visible lexical-local values and may use only operations representable by portable
`Expr` IR. Captured state, arbitrary method calls, loops, mutation, reflection, and other unrestricted
CLR computation cause `TransitionExpressionTranslationException`; they are never hidden in a
persisted callback.

An optional root `CreatesFrom` declaration makes subject absence part of the fingerprinted Transition
semantics. Its input-only expression must construct a complete object whose fields exactly match the
authoritative entity observation shape. CLR array and enumerable occurrences are compared using the
entity model's element-type plus `Many` cardinality representation; element type, presence, nullability,
and cardinality must still match exactly. Omitting `CreatesFrom` preserves the ordinary requirement that
the subject already exist. Creation does not introduce a separate Process node:
`InvokeTransitionProcessNode` continues to invoke the exact Transition reference.

### Persist, compile, and activate

The durable path always crosses the canonical document boundary:

```csharp
var json = ExecutionDefinitionJsonSerializer.Serialize(authored.Document);
var compatibility = new ExecutionDefinitionCompatibilityDeclaration(
    new([authored.Document.Metadata.SchemaVersion]),
    [authored.Document.Kind],
    [authored.Reference]); // In production, this allowlist is owned by the activating interpreter.
var restored = ExecutionDefinitionJsonSerializer.Deserialize(json, compatibility);
var compilation = TransitionStaticCompiler.Compile(restored);

if (!compilation.IsSuccessful)
{
    // Return or publish compilation.Validation; do not activate a partial plan.
    return;
}

var plan = compilation.Plan!;
// Acquire the observations required by plan.Analysis, construct a TransitionActivation,
// then call TransitionReferenceInterpreter.Decide(plan, activation).
```

Authoring records call-site provenance in `ExecutionSourceMap` entries without changing canonical
identity. Canonical validation and compilation diagnostics resolve their semantic locations through
that map, so tooling can present the originating C# member and line while retaining the canonical
location and diagnostic code. A persisted document remains valid and independently interpretable if
those local source files are moved or unavailable.

Failure contracts are intentionally separated by phase:

- Authoring misuse and non-portable expressions or constants fail immediately with argument,
  structural-builder, `NotSupportedException`, or `TransitionExpressionTranslationException`
  failures; no partial authored handle is returned.
- Canonical semantic problems, including CLR shapes that cannot be inferred portably, are deterministic
  `DocumentValidationDiagnostic` values on `authored.Validation` or `compilation.Validation`. Error
  diagnostics prevent creation of a compiled plan; callers must not activate `compilation.Analysis` as
  though it were complete.
- Definition activation requires the exact schema, definition identity, semantic revision, and
  fingerprint admitted by the interpreter. Compatibility failure is a definition/activation failure,
  not a domain-rejected transition outcome.
- Reference interpretation performs no I/O and no commit. Domain rejection, no-change, conflict,
  unavailable observation, and interpreter diagnostics remain distinct decisions/evidence for an
  external storage or process integration to handle.
- Sparse observation preserves the distinction between an unobserved path and explicit `Absent`,
  `Null`, `Unknown`, or `Failed` values. Authoring and adapters must not collapse those states.

## Canonical IR

Canonical definitions are ordinary portable values and use the shared execution envelope for identity,
revision, provenance, normalization, and fingerprinting:

`TransitionDecisionKind` defines the closed terminal categories. Complete decisions and execution evidence
are interpreter artifacts, not authored definition nodes. Likewise, `EmitTransitionNode` references one exact
interaction definition; that referenced definition owns whether the interaction is a domain event or request.

```csharp
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;

var text = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
ValueBindingId assignedStatus = new("assignedStatus");
var definition = new TransitionDefinition(
    input: new(new ObjectTypeRef([new("status", text.Type!)])),
    observation: new(new ObjectTypeRef([new("status", text.Type!)])),
    outcome: text,
    preconditions: [],
    body: new(
        new("assign/root"),
        [
            new LetTransitionNode(
                new("assign/value"),
                assignedStatus,
                text,
                Expr.Const("assigned")),
            new UpdateTransitionNode(
                new("assign/status"),
                FieldPath.FromField("status"),
                new SetTransitionPatch(Expr.BoundValue(assignedStatus))),
            new OutcomeTransitionNode(
                new("assign/applied"),
                TransitionOutcomeDisposition.Applied,
                Expr.BoundValue(assignedStatus))
        ]));

var document = TransitionDefinitionDocuments.Create(
    new("transition/assign-carrier"),
    new("revision/1"),
    definition,
    new(
        new("direct-csharp", "1"),
        new("src/domain/loads"),
        DocumentOrigin.User));
```

## Reference Interpretation

`TransitionReferenceInterpreter` is the deterministic, non-committing interpretation of a successfully
compiled canonical Transition plan. Its public entry points are:

- `Decide(plan, activation)` for an explicit `TransitionActivation` and observation frames.
- `DecideFullState(...)` for one concrete coherent aggregate state and optional fresh commit state.
- `DecideSparse(...)` for exact finite `TransitionObservationEntry` values and optional fresh commit entries.
- `DecideCreation(...)` for a Transition that explicitly derives complete initial state from input while the
  authoritative subject is absent.

Full-state and sparse evaluation are adapters over the same execution core. A sparse frame distinguishes an
unobserved access, represented by no entry, from an observed `Absent`, `Null`, `Unknown`, or `Failed` value.
Full-state evaluation resolves the same semantic field accesses from the supplied aggregate value, so both modes
produce path-level actual-read evidence.

The interpreter performs no I/O, invokes no services or delegates, mutates no caller-owned state, and does not
commit its result. `TransitionDecision` instead returns the typed outcome, evaluated sparse patch, pure emission
intents, actual Machine movements, guarantee demands, conflicts, diagnostics, and a single ordered
`TransitionExecutionEvidence` trace. Storage or process integrations acquire observations and interpret those
returned values through their own capability-checked commit boundary.

Creation interpretation retains the complete initial observation as `SubjectInitialized` execution evidence and
requires an `Applied` decision to commit the subject. Calling an existing-subject entry point for a creation
Transition, or `DecideCreation` for an update-only Transition, fails with structured subject-state evidence.

Commit observations are optional caller-supplied fresh evidence. When a decision requires commit, freshness is
checked only for observations actually read by the selected execution path. A changed value produces a
`Conflict` decision with exact expected and observed evidence; omitted fresh evidence for an actual read fails
closed with a structured diagnostic. If fresh evidence is not supplied, the decision's concurrency-observation
demands tell an external commit interpretation what must remain coherent.

### Machine link boundary

`MoveMachineTransitionNode` persists only an exact fingerprint-bound Machine definition reference, an edge
identity, and its typed rejection outcome. A linker projects the authoritative Machine edge into an immutable
`TransitionMachineEdgeLink` containing source and target configuration predicates and edge-owned assignments;
`TransitionStaticCompiler` resolves that slice from `TransitionMachineLinkCatalog` and pins each used link into
the compiled plan. The reference interpreter validates the source configuration, applies the assignments to its
candidate state, and verifies the target configuration. It does not copy an independently authored lifecycle
graph into Transition IR or resolve Machine state through an ambient runtime service.

## Retired Compatibility Surface

The flat definition, two-parameter typed handle, local apply runtime, name-dispatched effects, CLR
effect handlers, continuation snapshots, and their host wrappers are not shipped. There is no
automatic compatibility reader because no retained persisted or external boundary requires one.
Git history is the recovery path for an explicitly versioned offline importer if such a boundary is
identified later.

## Related Packages

- [Execution Kernel adoption and migration guide](../../docs/EXECUTION_KERNEL_GUIDE.md) for the end-to-end canonical lifecycle, executable examples, and retired-surface replacements.
- `Cohesive.Processes` for workflows that invoke entity transitions.
- `Cohesive.Storage` for persistence adapters.
- `Cohesive.Analyzers` for source-generation support around semantic authoring patterns.

## Expression Sites

Transition expressions use the shared, non-generic `Cohesive.Model.Expr` IR and expression
requirements analyzer. The transition model supplies a different scope for each semantic site:

- Admission predicates see entity state and declared transition inputs and must produce a Boolean.
- Subject initializers see only declared transition input and must produce the complete entity observation.
- Sparse patch expressions see entity state and transition inputs and must satisfy the target field contract.
- Computed fields see entity state without transition inputs and must satisfy their field contract.
- Entity invariants see resulting entity state without transition inputs and must produce a
  Boolean.

These scopes are compiler-front-end descriptions, not serialized CLR evaluation contexts. The
transition runtime keeps its own state and input objects, while analysis exposes portable field,
parameter, function, operator, and ambient-capability requirements for validation, dependency
analysis, documentation, and reference or target interpreters.
