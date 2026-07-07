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

## Example

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
