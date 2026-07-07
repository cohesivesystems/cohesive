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

var api = Api.Define("Shipping")
    .Entity<Shipment>()
    .Query("GetById")
        .Route("GET", "/api/shipments/{id}")
        .Returns<ShipmentDto>()
        .Done()
    .Command("Dispatch")
        .Route("POST", "/api/shipments/{id}/dispatch")
        .Accepts<DispatchShipmentRequest>()
        .Transition(new(name: "Dispatch"))
        .Done()
    .Build();
```

## Related Packages

- `Cohesive.Adapters.AspNet` for ASP.NET endpoint projection.
- `Cohesive.Adapters.OpenApi` for OpenAPI emission.
- `Cohesive.Adapters.GraphQL` for GraphQL schema emission.
- `Cohesive.Adapters.TypeScript` for TypeScript client generation.
