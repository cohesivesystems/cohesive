# Cohesive.Transitions

`Cohesive.Transitions` models one entity change as portable, inspectable semantics: admission rules, branching,
sparse updates, invariants, interactions, machine movements, and typed outcomes.

## Install

```bash
dotnet add package Cohesive.Transitions
```

## Start with the entity

The C# entity surface discovers fields and produces the canonical observation shape. Ordinary field declarations do
not require node IDs:

```csharp
public enum LoadStatus { Draft, Assigned }
```

<!-- docs-sync:transitions-entity:start -->
```csharp
public sealed class Load : Entity<Load>
{
    public Load()
    {
        Status = Field(nameof(Status), LoadStatus.Draft);
        CarrierId = Field<string?>(
            nameof(CarrierId),
            initialValue: null,
            configure: field => field.Optional());

        Invariant(
            "AssignedLoadsHaveACarrier",
            load => load.Status != LoadStatus.Assigned ||
                    load.CarrierId != null);
    }

    public Field<LoadStatus> Status { get; }

    public Field<string?> CarrierId { get; }
}
```
<!-- docs-sync:transitions-entity:end -->

Transition authoring then uses restricted typed expressions over the current entity observation and a typed input.
Those expressions lower immediately into canonical IR; arbitrary method calls, hidden I/O, captured runtime state,
loops, and mutation are rejected.

## What a Transition produces

- A canonical, persistable `ExecutionDefinitionDocument`.
- Exact input, entity-observation, and outcome contracts.
- Admission rules and deterministic branch structure.
- An algebraic sparse patch rather than in-place entity mutation.
- Interaction intents and machine movements when declared.
- Candidate-state invariant checks and typed outcome evidence.
- Structured source maps, validation diagnostics, and a semantic fingerprint.

The document is the semantic authority. The typed handle, static plan, reference interpreter decision, repository
commit, and emitted interaction envelopes are projections or interpretations of that exact document.

## Current authoring boundary

Entity shape discovery and common field defaults are convention-driven. The current Transition builder keeps stable
identities explicit for durable rules, branches, updates, and outcomes; advanced examples and the canonical IR are
therefore documented separately. Additional identity conventions should be added to the authoring API before they are
presented as ordinary syntax.

## Use it when

- Entity invariants and legal changes should be explicit and reusable across hosts.
- A decision must be testable without a database, message broker, or HTTP endpoint.
- Storage needs a sparse patch and exact concurrency evidence rather than an opaque callback.
- Processes, APIs, presentation actions, and tooling should reference the same domain behavior.

## Continue

- [Internals](INTERNALS.md) contains the complete explicit authoring example, canonical IR, persistence, compilation,
  reference interpretation, and expression-site details.
- [Execution kernel guide](../../docs/EXECUTION_KERNEL_GUIDE.md) explains Transition linking and execution boundaries.
- [`Cohesive.Storage`](../Cohesive.Storage/README.md) attaches repository and commit interpretations.
- [`Cohesive.Processes`](../Cohesive.Processes/README.md) coordinates Transition invocations with other semantic work.
