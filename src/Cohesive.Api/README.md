# Cohesive.Api

Semantic API declaration primitives for describing operations, endpoints, pagination, scope policies, and generated API artifacts.

This package is host-neutral. HTTP bindings describe portable API intent; they do not reference ASP.NET Core or map
runtime routes. Concrete host projection belongs to an adapter.

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

## ASP.NET projection

ASP.NET endpoint mapping lives in `Cohesive.Adapters.AspNet`. Hosts add that package and import its namespace:

```csharp
using Cohesive.Adapters.AspNet;

app.MapApiDefinition(api, operation => CreateHandler(operation));
```

`MapApiDefinition`, `MapApiEndpoint`, and `AspNetAuthorizationPolicyResolver` previously lived in the
`Cohesive.Api` assembly and namespace. They moved without a forwarding shim so this package can remain independent
of `Microsoft.AspNetCore.App`.

## Related Packages

- [Execution Kernel adoption and migration guide](../../docs/EXECUTION_KERNEL_GUIDE.md) for the common status, trace, explain, and telemetry projection contract.
- `Cohesive.Api.Execution` for the canonical route-neutral Process execution-control catalog and reference integration.
- `Cohesive.Adapters.AspNet` for ASP.NET endpoint projection.
- `Cohesive.Adapters.OpenApi` for OpenAPI emission.
- `Cohesive.Adapters.GraphQL` for GraphQL schema emission.
- `Cohesive.Adapters.TypeScript` for TypeScript client generation.

## Service declarations

`Services.ServiceDefinition` groups exact Transition, query, Process-entry and Process-control operations
into a portable authority using the existing execution-definition document, provenance and fingerprints.
Referenced definitions own input/output and behavior. Control actions use the native Process-control
vocabulary; requirements are declared per exposed operation. Native repositories, dispatchers and HTTP
routes are runtime/projection bindings, never stored callbacks. The optional `Cohesive.Api.Execution`
package provides shared invocation and normalized authorization.
See [declarative service runtime](../../docs/decisions/declarative-service-runtime.md).

### Response-typed endpoint handles

`declaration.Result(ApiResultKind.NotFound).Build<OrderDetails>()` produces an
`ApiEndpoint<OrderDetails>`. The tag describes the primary body; 404 and other alternatives keep their
own contracts. The handle shares the exact `ApiOperation` registered in the definition, and `WithHttp`
retains the tag. A mismatched tag fails before registering an operation. Typed query bindings accept
`ApiEndpoint<TResult>` alongside `IRelationQueryReader<TInput,TResult>`, so unrelated response types
cannot be paired by type inference. Untyped handles remain available for heterogeneous API catalogs.

`Build<OrderCreated>(ApiResultKind.Created)` declares a typed 201 response in one call. Omitting the kind
defaults to Success, or retains an existing declaration. Explicit Returns declarations remain supported;
typed Build validates them instead of silently replacing their type or result kind.
