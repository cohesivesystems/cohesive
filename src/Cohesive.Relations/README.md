# Cohesive.Relations

Semantic relation, projection, mapping, hydration, query, and aggregation primitives.

## Install

```bash
dotnet add package Cohesive.Relations
```

## Use When

- You need relation definitions that can map, join, group, and materialize observations.
- You want object-to-observation mapping and observation-to-object projection without binding to a specific storage engine.
- You need query and aggregation plans that can be interpreted in-memory or compiled by storage adapters.

## Example

```csharp
using Cohesive.Relations.Authoring;

var relation = Relation<LoadSearchDocument>
    .From<Load>()
    .Join<Carrier>(static (load, carrier) => load.CarrierId == carrier.Id)
    .Select(static (load, carrier) => new LoadSearchDocument
    {
        LoadId = load.Id,
        CarrierName = carrier.LegalName,
        Amount = load.TotalAmount,
        CarrierSafetyScore = carrier.SafetyScore
    });
```

## Related Packages

- `Cohesive.Relations.Contracts` for generated relation contract surfaces.
- `Cohesive.Storage` for repository adapters over observations.
- `Cohesive.Adapters.Cosmos` and `Cohesive.Adapters.Elastic` for concrete query compilers.
