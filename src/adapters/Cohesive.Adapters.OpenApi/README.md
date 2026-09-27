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

## Public JSON contracts

Supply `OpenApiEmitterOptions.JsonSerializerOptions` to project a public document contract from the
same serializer metadata used on the wire. This supports recursive records, explicit polymorphic
cases and converter-backed scalar identifiers while keeping portable document storage admission separate.
The CLI selects this profile with `--shape-projection canonical-json` for OpenAPI as well as shapes.

The profile rejects property-specific converters/number-handling overrides and polymorphic cases
without explicit discriminators. Unknown converter output stays opaque; CLR implementation members
are not substituted for a converter schema. Native document semantic validation remains authoritative.
See [typed portable JSON contracts](../../../docs/decisions/typed-portable-json-values.md) for the
projection boundary and qualification.
