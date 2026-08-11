# Cohesive.Api.Execution

Transport-neutral execution-control API composition for canonical Cohesive execution semantics.

## Install

```bash
dotnet add package Cohesive.Api.Execution
```

## Use When

- You need the canonical Process start, inspection, explanation, retained-trace, lifecycle-control, or limit-update
  API catalog.
- You want an in-memory reference binding for the same declared operation and result contracts.
- You are building an HTTP, CLI, generated-client, test, or presentation interpretation of execution control.

Applications that only declare general semantic APIs should reference `Cohesive.Api` instead.

## Ownership Boundary

`Cohesive.Api.Execution` is a composition package, not another execution semantic authority:

- `Cohesive.Api` owns generic operation, endpoint, authorization, result, and semantic-reference declarations.
- `Cohesive`, `Cohesive.Processes`, and `Cohesive.Storage` own execution identity, commands, status, trace,
  explanation, Control, and Process runtime semantics.
- This package binds those authorities into one stable execution-control catalog and safe reference result
  projections.
- Concrete transports such as ASP.NET remain in `Cohesive.Adapters.*`.

The dependency direction is acyclic: `Cohesive.Api.Execution` depends on `Cohesive.Api`, `Cohesive.Processes`,
`Cohesive.Storage`, and the `Cohesive` foundation; the complete generic `Cohesive.Api` project-reference closure
does not acquire `Cohesive.Processes`.

## Entry Points

`ExecutionControlApiCatalog.Create()` returns the complete immutable route-neutral operation inventory in stable
order. `InMemoryExecutionControlApiAdapter` is a linearizable reference integration for tests and local composition;
canonical reducers remain responsible for lifecycle, replay, fencing, and admission semantics.

```csharp
using Cohesive.Api.Execution;

var catalog = ExecutionControlApiCatalog.Create();
var traces = catalog.Definition.GetOperation(catalog.Traces);
```

The `explain` and `traces` queries return `ExecutionExplainArtifact` and `ProcessExecutionTraceArtifact` directly.
Adapters must not translate them into parallel response models. Opaque `ExecutionApiProblem` values intentionally
exclude physical identities, authorization evidence, payloads, and provider history.

## Invariants and Failure Boundaries

- Endpoint handles, operation order, authorization requirements, result variants, and semantic references come only
  from `ExecutionControlApiCatalog`.
- Trusted authorization, issuance, provenance, and tenant evidence is supplied by an adapter, never deserialized
  from an API caller.
- Repository availability and lifecycle dispositions map to declared result variants; unspecified or incoherent
  states fail closed.
- The in-memory adapter accepts only handles owned by its exact catalog and checks returned evidence affinity before
  projection.
- The complete `Cohesive.Api` project-reference closure must remain free of `Cohesive.Processes`.

## Transport Interpretation

`Cohesive.Adapters.AspNet` maps the catalog's Process observation handles through
`MapProcessExecutionInspectApi`, `MapProcessExecutionExplainApi`, and `MapProcessExecutionTracesApi`. OpenAPI,
GraphQL, TypeScript, CLI, and future hosts consume the same generic `ApiDefinition` without acquiring transport
behavior from this package.

## Migration from Cohesive.Api

Execution-control types moved from the `Cohesive.Api` assembly into this package. Add a package or project reference
to `Cohesive.Api.Execution` and import `Cohesive.Api.Execution`. Wire authorities, schema version v4, operation order,
endpoint identities, contracts, and canonical JSON are unchanged.

## Testing

Catalog, result, in-memory integration, generated-client, and ASP.NET conformance tests live under
`src/Cohesive.Tests/Api`. `ExecutionApiPackageBoundaryTests` guards the assembly dependency direction and ownership
of the execution-specific public surface.

## Related Packages

- `Cohesive.Api` for generic semantic API declarations.
- `Cohesive.Processes` for canonical Process semantics and execution evidence.
- `Cohesive.Storage` for canonical Control contracts used by the execution-control surface.
- `Cohesive.Adapters.AspNet` for ASP.NET endpoint projection.
- `Cohesive.Adapters.OpenApi`, `Cohesive.Adapters.GraphQL`, and `Cohesive.Adapters.TypeScript` for derived API artifacts.
