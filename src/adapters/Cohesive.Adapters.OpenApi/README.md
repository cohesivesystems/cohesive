# Cohesive.Adapters.OpenApi

OpenAPI document emission and endpoint helpers for Cohesive API declarations.

## Install

```bash
dotnet add package Cohesive.Adapters.OpenApi
```

## Use When

- You want OpenAPI artifacts generated from Cohesive API declarations.
- You need endpoint helpers for publishing generated OpenAPI documents.
- You want OpenAPI, GraphQL, and TypeScript clients to share one semantic API source.

## Example

```csharp
using Cohesive.Adapters.OpenApi;
using Cohesive.Api;

var api = Api.Define("Shipping")
    .Entity<ShipmentDto>()
    .Query("Get")
        .Route("GET", "/shipments/{id}")
        .RouteParameter<string>("id")
        .Returns<ShipmentDto>()
        .Done()
    .Build();

var document = new OpenApiEmitter().Emit(api).Documents.Single();
app.MapCohesiveOpenApi(api);
```

## Related Packages

- `Cohesive.Api` for API declarations.
- `Cohesive.CodeGen.Cli` for build-time artifact generation.
