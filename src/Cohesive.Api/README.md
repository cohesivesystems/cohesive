# Cohesive.Api

Semantic API declaration primitives for describing operations, endpoints, pagination, scope policies, and generated API artifacts.

## Install

```bash
dotnet add package Cohesive.Api
```

## Use When

- You want an API surface to be declared as semantic operations before projecting it to HTTP, OpenAPI, GraphQL, or TypeScript clients.
- You need shared endpoint metadata that can be interpreted by multiple adapters.
- You want API declarations to stay close to Cohesive shapes, presentation modules, relations, or process definitions.

## Example

```csharp
using Cohesive.Api;

var dispatch = compiledDispatchPlan.DefinitionReference;
var api = Api.Define("Shipping")
    .Entity<Shipment>()
    .Query("GetById")
        .Route("GET", "/api/shipments/{id}")
        .Returns<ShipmentDto>()
        .Done()
    .Command("Dispatch")
        .Route("POST", "/api/shipments/{id}/dispatch")
        .Accepts<DispatchShipmentRequest>()
        .Transition(dispatch)
        .Done()
    .Build();
```

## Optional execution control

The canonical Process execution-control catalog intentionally lives in `Cohesive.Api.Execution`. That optional
composition package binds these generic declarations to `Cohesive.Processes` contracts without making every
`Cohesive.Api` consumer acquire the Process language and runtime.

## Related Packages

- [Execution Kernel adoption and migration guide](../../docs/EXECUTION_KERNEL_GUIDE.md) for the common status, trace, explain, and telemetry projection contract.
- `Cohesive.Api.Execution` for the canonical route-neutral Process execution-control catalog and reference integration.
- `Cohesive.Adapters.AspNet` for ASP.NET endpoint projection.
- `Cohesive.Adapters.OpenApi` for OpenAPI emission.
- `Cohesive.Adapters.GraphQL` for GraphQL schema emission.
- `Cohesive.Adapters.TypeScript` for TypeScript client generation.
