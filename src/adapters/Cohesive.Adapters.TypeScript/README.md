# Cohesive.Adapters.TypeScript

TypeScript emitters for Cohesive shapes, API clients, constants, and Playwright API mocks.

## Install

```bash
dotnet add package Cohesive.Adapters.TypeScript
```

## Use When

- You want TypeScript declarations generated from Cohesive shape graphs.
- You need generated API client functions from Cohesive API definitions.
- You want frontend test mocks to come from the same semantic API model as runtime clients.

## Example

```csharp
using Cohesive.Adapters.TypeScript;
using Cohesive.Api;
using Cohesive.Model;

var graph = new ClrShapeGraphBuilder()
    .AddShape<ShipmentDto>()
    .Build(new("shipping"));

var api = Api.Define("Shipping")
    .Entity<ShipmentDto>()
    .Query("Get")
        .Route("GET", "/shipments/{id}")
        .RouteParameter<string>("id")
        .Returns<ShipmentDto>()
        .Done()
    .Build();

var contracts = new TypeScriptShapeEmitter(new()
{
    FileName = "contracts.generated.ts"
}).Emit(graph);

var client = new TypeScriptApiClientEmitter(new()
{
    ShapesImportPath = "./contracts.generated"
}).Emit(api);
```

HTTP parameter names remain wire authority. The client and Playwright emitters project route, query, and header names
such as `Ari-Process-Key` to valid local TypeScript identifiers such as `ariProcessKey`, while still reading or writing
the original wire name. Leading digits and reserved words are escaped, and normalized identifier collisions receive
stable numeric suffixes in declaration order.

## Related Packages

- `Cohesive` for shape graph metadata.
- `Cohesive.Api` for API definitions.
- `Cohesive.CodeGen.Cli` for build-facing command-line generation.
