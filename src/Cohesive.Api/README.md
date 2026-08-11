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

## Execution control and diagnostics

`ExecutionControlApiCatalog.Create()` declares the shared route-neutral Process surface used by HTTP, CLI,
generated-client, and in-memory bindings. Its `explain` query accepts the same trusted, read-only
`InspectProcessCommand` address as status inspection and returns the canonical `ExecutionExplainArtifact`; an
adapter must not translate that artifact into a second diagnostics model.

CLI and documentation projections can render the exact response as human-readable JSON while APIs and tests use
the same value directly:

```csharp
var catalog = ExecutionControlApiCatalog.Create();
var operation = catalog.Definition.GetOperation(catalog.Explain);
var output = ExecutionExplainJsonSerializer.Serialize(
    artifact,
    PortableDocumentJsonFormatting.Indented);
```

The catalog deliberately supplies no HTTP route or console formatting. A transport adapter binds the typed
operation handle, and presentation remains outside the execution semantic authorities. `ApiEndpoint.WithHttp`
projects a handle without redeclaring its semantic identity, contracts, policies, results, or authority references.
For conventional Process observation HTTP projections, use
`Cohesive.Adapters.AspNet.Processes.MapProcessExecutionInspectApi` and `MapProcessExecutionExplainApi`. Both accept
only the logical Process identity from the route and require a trusted server-side authority-scope resolver; neither
deserializes the authorization, issuance, or provenance fields of a client-authored `InspectProcessCommand`.

## Related Packages

- [Execution Kernel adoption and migration guide](../../docs/EXECUTION_KERNEL_GUIDE.md) for the common status, trace, explain, and telemetry projection contract.
- `Cohesive.Adapters.AspNet` for ASP.NET endpoint projection.
- `Cohesive.Adapters.OpenApi` for OpenAPI emission.
- `Cohesive.Adapters.GraphQL` for GraphQL schema emission.
- `Cohesive.Adapters.TypeScript` for TypeScript client generation.
