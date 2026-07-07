# Cohesive.Adapters.GraphQL

GraphQL schema emission and endpoint helpers for Cohesive API declarations.

## Install

```bash
dotnet add package Cohesive.Adapters.GraphQL
```

## Use When

- You want GraphQL schema artifacts generated from Cohesive API declarations.
- You need GraphQL output to stay aligned with the same semantic API model used for OpenAPI or TypeScript clients.
- You are integrating Cohesive code generation into a frontend or gateway workflow.

## Example

```csharp
using Cohesive.Adapters.GraphQL;
using Cohesive.Api;

var api = Api.Define("Shipping")
    .Entity<ShipmentDto>()
    .Query("Get")
        .Route("GET", "/shipments/{id}")
        .RouteParameter<string>("id")
        .Returns<ShipmentDto>()
        .Done()
    .Build();

var schema = new GraphQLSchemaEmitter().EmitSchema(api);
app.MapCohesiveGraphQLSchema(api);
```

## Related Packages

- `Cohesive.Api` for API definitions.
- `Cohesive.CodeGen.Cli` for build-time artifact emission.
