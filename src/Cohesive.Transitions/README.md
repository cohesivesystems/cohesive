# Cohesive.Transitions

Entity transition, invariant, effect, and domain model authoring primitives.

## Install

```bash
dotnet add package Cohesive.Transitions
```

## Use When

- You want entities to declare semantic fields, invariants, transitions, effects, and continuations.
- You need transition execution to produce explicit state changes and effect snapshots.
- You want domain behavior represented as a model that can later be interpreted by storage, process, API, or UI adapters.

## Semantic Authority

`Cohesive.Transitions.IR` is the canonical persisted semantic authority for execution-kernel
Transitions. Its structured definitions are stored through the shared execution-definition envelope
and remain inspectable without the original authoring assembly.

`Cohesive.Transitions.Model.TransitionDefinition`, the current builders, and
`DeclarativeEntityRuntime` are temporary compatibility surfaces for the earlier flat transition
model. They are not persisted kernel authority. Existing consumers may continue to use them during
migration, but new durable semantics should be expressed in `Cohesive.Transitions.IR`.

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

Full-state and sparse evaluation are adapters over the same execution core. A sparse frame distinguishes an
unobserved access, represented by no entry, from an observed `Absent`, `Null`, `Unknown`, or `Failed` value.
Full-state evaluation resolves the same semantic field accesses from the supplied aggregate value, so both modes
produce path-level actual-read evidence.

The interpreter performs no I/O, invokes no services or delegates, mutates no caller-owned state, and does not
commit its result. `TransitionDecision` instead returns the typed outcome, evaluated sparse patch, pure emission
intents, actual Machine movements, guarantee demands, conflicts, diagnostics, and a single ordered
`TransitionExecutionEvidence` trace. Storage or process integrations acquire observations and interpret those
returned values through their own capability-checked commit boundary.

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

## Compatibility Example

The following example uses the current compatibility authoring and runtime surface. Its eventual
lowering target is canonical `Cohesive.Transitions.IR` rather than the flat model it produces today.

```csharp
using Cohesive.Transitions.Authoring;

public enum LoadStatus
{
    Draft,
    Assigned
}

public sealed class Load : Entity<Load>
{
    public sealed record AssignCarrierInput(string CarrierId);

    public Load()
    {
        Id = WriteOnceField<string>(nameof(Id));
        Status = Field(nameof(Status), LoadStatus.Draft);
        CarrierId = Field<string?>(
            nameof(CarrierId),
            initialValue: null,
            configure: field => field.Optional());

        AssignCarrier = Transition<AssignCarrierInput>(
            nameof(AssignCarrier),
            transition => transition
                .Requires("CanAssignCarrier", (load, input) =>
                    load.Status == LoadStatus.Draft && input.CarrierId != "")
                .Set(load => load.CarrierId, (_, input) => input.CarrierId)
                .Set(load => load.Status, (_, _) => LoadStatus.Assigned)
                .EmitSnapshot("CarrierAssigned", (snapshot, input) => new
                {
                    loadId = snapshot.EntityId.Value,
                    carrierId = input.CarrierId
                }));
    }

    public Field<string> Id { get; }

    public Field<LoadStatus> Status { get; }

    public Field<string?> CarrierId { get; }

    public Transition<Load, AssignCarrierInput> AssignCarrier { get; }
}
```

## Related Packages

- `Cohesive.Processes` for workflows that invoke entity transitions.
- `Cohesive.Storage` for persistence adapters.
- `Cohesive.Analyzers` for source-generation support around authoring patterns.

## Expression Sites

Transition expressions use the shared, non-generic `Cohesive.Model.Expr` IR and expression
requirements analyzer. The transition model supplies a different scope for each semantic site:

- Preconditions see entity state and declared transition inputs and must produce a Boolean.
- Field updates see entity state and transition inputs and must satisfy the target field contract.
- Computed fields see entity state without transition inputs and must satisfy their field contract.
- Entity invariants see resulting entity state without transition inputs and must produce a
  Boolean.

These scopes are compiler-front-end descriptions, not serialized CLR evaluation contexts. The
transition runtime keeps its own state and input objects, while analysis exposes portable field,
parameter, function, operator, and ambient-capability requirements for validation, dependency
analysis, documentation, and reference or target interpreters.
