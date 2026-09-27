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

Production runtimes can supply authoritative asynchronous dispatchers for Process start, the five lifecycle
mutations, and Control-limit updates. When one is supplied, the adapter authorizes against the canonical endpoint,
does not consult its local registry for that operation, and projects only the authoritative result. A lifecycle
dispatcher receives the untrusted portable command separately from `ExecutionApiInvocationContext`; it resolves the
Process using the trusted authority scope, rebinds first-occurrence evidence, restores retained occurrence evidence
for exact replay, and returns only after the safe canonical result is recoverable. Provider message admission alone
is not lifecycle success. `ExecutionProcessControlCommandAdmission` provides the shared trusted rebinding rule.

The `explain` and `traces` queries return `ExecutionExplainArtifact` and `ProcessExecutionTraceArtifact` directly.
Adapters must not translate them into parallel response models. Opaque `ExecutionApiProblem` values intentionally
exclude physical identities, authorization evidence, payloads, and provider history.

## Invariants and Failure Boundaries

- Endpoint handles, operation order, authorization requirements, result variants, and semantic references come only
  from `ExecutionControlApiCatalog`.
- Trusted authorization, issuance, provenance, and tenant evidence is supplied by an adapter, never deserialized
  from an API caller.
- Authoritative lifecycle lookup uses trusted authority scope plus canonical logical Process identity. Missing and
  unauthorized targets remain opaque, and exact command or idempotency replay restores the retained occurrence.
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

## Declared service invocation

`Services.ServiceRuntime` binds a validated `Cohesive.Api.Services.ServiceDefinition` document
to exact Transition/entity/repository, canonical query/evaluator and Process/start-or-control-dispatcher associations. Direct calls and
`Cohesive.Adapters.AspNet.Services.MapServiceTransition` share authoritative loading, mandatory authority,
decision and conditional commit. API CLR types are checked projections of the Transition contracts.

`IdentityServiceInvocationAuthorization` interprets declared requirements as explicit capabilities in
normalized identity grants and checks logical entity ownership independently of physical placement.
It adds the existing `Cohesive.Identity` dependency to this optional composition package. Generic API
consumers still do not acquire storage, identity or Process runtime dependencies through `Cohesive.Api`.

Transition invocation supports existing subjects without emissions; unsupported creation, emission and
extension requirements fail binding. `EvaluateAsync` binds the declared query scope parameter from
trusted identity, and `StartAsync` delegates admission/replay to the existing Process dispatcher.
`ControlAsync` derives its request type and result semantics from the native control API catalog; it binds
trusted authority and an exact Process-definition restriction before native admission or receipt replay.
The lifecycle profile supports Pause, Continue, RestartAttempt, Cancel and Terminate; inspection and Signal
ingress require distinct qualified bindings. The
service itself adds no durable receipt store or stronger permission-revocation guarantee. Returned service and Transition traces are payload-free inspection evidence; internal snapshots and
decision inputs/observations are not invocation results. See [the design and qualification boundary](../../docs/decisions/declarative-service-runtime.md)
and `ServiceRuntimeTests`, `ServiceQueryRuntimeTests` and `ServiceProcessRuntimeTests` for executable
examples. `ServiceCompositionTests` qualifies acquisition/computation followed by durable multi-entity
recovery. Query source qualification currently covers native in-memory acquisition; remote-provider
filtering and new-family HTTP projections remain in progress.
