# Cohesive

Core primitives, shape metadata, domain quantities, code generation abstractions, and prelude helpers shared by the Cohesive package family.

## Install

```bash
dotnet add package Cohesive
```

## Use When

- You need the common shape model used by Cohesive blocks and adapters.
- You want shared domain primitives such as typed quantities, codes, identifiers, paths, and observation values.
- You are building a new Cohesive block or adapter and need the base semantic contracts.

## Example

```csharp
using Cohesive.Model;

[ShapeDefinition("shape.shipment", ShapeRoles.Transport)]
public sealed record Shipment(string Id, IReadOnlyList<Stop> Stops);

[ShapeType("type.stop")]
public sealed record Stop(string City, string State);

var graph = new ClrShapeGraphBuilder()
    .AddShape<Shipment>()
    .Build(new("shipping"));
```

## Package Role

`Cohesive` is the foundation package. Higher-level blocks such as `Cohesive.Relations`, `Cohesive.Transitions`, `Cohesive.Processes`, `Cohesive.Presentation`, and `Cohesive.Api` depend on it.
