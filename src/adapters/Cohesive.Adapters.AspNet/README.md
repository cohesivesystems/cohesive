# Cohesive.Adapters.AspNet

ASP.NET Core endpoint and request-binding adapters for Cohesive APIs, entities, relations, processes, and identity context.

## Install

```bash
dotnet add package Cohesive.Adapters.AspNet
```

## Use When

- You want to expose Cohesive API declarations through ASP.NET Core endpoints.
- You need route builders for entity operations, relation queries, process execution, or process status.
- You want ASP.NET request identity and scope policy enforcement to flow into Cohesive operation context.

## Example

```csharp
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Microsoft.AspNetCore.Http;

var api = Api.Define("Notes");
var getNote = api.Entity<NoteResource>()
    .Query("Get")
    .Route("GET", "/notes/{id}")
    .RouteParameter<string>("id")
    .Returns<NoteResource>()
    .Build();

app.MapEntityApiDefinition(api.Build(), new EntityApiEndpointOptions
{
    Entity = NoteEntity.Instance.Definition
}
    .Bind(getNote.Get(static (_, snapshot) =>
        Results.Ok(ToResource(snapshot)))));
```

## Generic semantic API projection

The root `Cohesive.Adapters.AspNet` namespace owns the direct Minimal API interpretation of `ApiDefinition` and
`ApiEndpoint`. Mapping preserves each original `ApiOperation`, `HttpBinding`, authorization requirement, scope
policy, and semantic reference as endpoint metadata. Definitions are mapped in stable declaration order, and the
optional callback can attach additional ASP.NET metadata without changing semantic authority.

```csharp
using Cohesive.Adapters.AspNet;
using Cohesive.Api;

var definition = Api.Define("Shipping")
    .Query("Health")
        .Route("GET", "/health")
        .Returns<HealthResponse>()
        .Done()
    .Build();

app.MapApiDefinition(
    definition,
    operation => operation.Name switch
    {
        "Health" => () => Results.Ok(new HealthResponse("ready")),
        _ => throw new InvalidOperationException($"No handler is bound for '{operation.Id}'.")
    });
```

For secured operations, supply an `AspNetAuthorizationPolicyResolver`. Mapping fails closed before registering any
route when a definition contains an authorization requirement without a valid ASP.NET policy projection.

This surface previously lived in the `Cohesive.Api` assembly and namespace. Existing ASP.NET hosts must reference
this adapter and add `using Cohesive.Adapters.AspNet;`; no compatibility shim remains in the host-neutral package.

## Canonical relation/query evaluation

Relation/query endpoints author a new `RelationQueryEvaluation` for each HTTP request and delegate the complete
compile-realize-plan-execute pipeline to `IRelationQueryEvaluator`. The request context supplies the evaluation
identity so runtime evidence, diagnostics, and traces remain correlated with the HTTP request. The result mapper is
required: the in-process evaluation outcome deliberately is not treated as a default wire contract.

```csharp
using Cohesive.Adapters.AspNet.Relations;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;

// Configure an evaluator with the application's placement policy and source readers.
builder.Services.AddSingleton<IRelationQueryEvaluator>(relationQueryEvaluator);

var api = Api.Define("Transportation");
var loads = api.Action("SearchLoads")
    .Route("GET", "/loads")
    .Query<SearchLoadsRequest>()
    .Returns<SearchLoadsResponse>()
    .Build();

app.MapRelationQueryApiDefinition(api.Build(), new RelationQueryApiEndpointOptions()
    .Bind(loads.RelationQuery(
        (context, request) =>
        {
            var search = (SearchLoadsRequest)request!;
            return loadsByCustomerDocument
                .Evaluate(context.EvaluationId, loadShapeDocuments, relationshipCatalog)
                .Set(customerNameParameterId, ObservationValue.FromString(search.CustomerName))
                .Select(loadRowsId)
                .Build();
        },
        (_, outcome) =>
        {
            if (outcome.Result is not { IsSuccessful: true } result)
                return Results.UnprocessableEntity(outcome.Compilation.Diagnostics);

            var rows = result.QueryResults.Single(branch => branch.Result == loadRowsId).Rows;
            return Results.Ok(new SearchLoadsResponse(rows));
        })));
```

`EvaluationIdSelector` can override the default `aspnet/request/.../operation/...` convention when an application
already has a stable correlation identity. The endpoint verifies that the per-request factory and evaluator preserve
the selected identity and passes one effective token, linking operation cancellation with
`HttpContext.RequestAborted`, through request binding, evaluation authoring, execution, and result mapping.

Entity-declared query endpoints use this same canonical binding rather than a repository-specific Entity query path.
Map point reads and writes with the Entity adapter, then map the query endpoint from the same API definition with the
Relations adapter. Each mapper emits only its bound endpoints, so the route is created exactly once:

```csharp
var definition = api.Build();

app.MapEntityApiDefinition(definition, new EntityApiEndpointOptions
{
    Entity = NoteEntity.Instance.Definition
}.Bind(noteGet.Get(static (_, snapshot) => Results.Ok(ToResource(snapshot)))));

app.MapRelationQueryApiDefinition(definition, new RelationQueryApiEndpointOptions()
    .Bind(noteSearch.RelationQuery(
        (context, request) => NoteQueries.Search(
            context.EvaluationId,
            (SearchNotesRequest)request!),
        static (_, outcome) => MapSearchResponse(outcome))));
```

The required result mapper receives the complete canonical outcome, including rows, aggregations, requirement gaps,
diagnostics, and provenance, and remains responsible for the endpoint's HTTP status policy.

## Canonical Process observation reads

`MapProcessExecutionInspectApi`, `MapProcessExecutionExplainApi`, and `MapProcessExecutionTracesApi` project the
existing route-neutral `ExecutionControlApiCatalog` handles as HTTP GETs without adding status, explanation, or trace
DTOs. Each route carries only the logical Process identity. Their shared `ProcessExecutionAuthorityScopeResolver`
must derive authority and tenant from authenticated server-side identity and scope evidence; it must not copy them
from caller data. The required authorization-policy resolver maps each catalog semantic authorization requirement to
ASP.NET authorization metadata.

```csharp
using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Api.Execution;

var executionControl = ExecutionControlApiCatalog.Create();

app.MapProcessExecutionInspectApi(
    executionControl.Inspect,
    "/api/processes/{processInstanceId}",
    (operationContext, httpContext, processInstanceId) =>
        ResolveAuthorizedProcessScope(operationContext, httpContext, processInstanceId),
    (operation, requirement) => ResolveAuthorizationPolicy(requirement));

app.MapProcessExecutionExplainApi(
    executionControl.Explain,
    "/api/processes/{processInstanceId}/explain",
    (operationContext, httpContext, processInstanceId) =>
        ResolveAuthorizedProcessScope(operationContext, httpContext, processInstanceId),
    (operation, requirement) => ResolveAuthorizationPolicy(requirement));

app.MapProcessExecutionTracesApi(
    executionControl.Traces,
    "/api/processes/{processInstanceId}/traces",
    (operationContext, httpContext, processInstanceId) =>
        ResolveAuthorizedProcessScope(operationContext, httpContext, processInstanceId),
    (operation, requirement) => ResolveAuthorizationPolicy(requirement));
```

The inspect binding resolves `IProcessExecutionRepository`, performs its provider-neutral logical read, and returns
only a retained canonical `ExecutionStatus` inside the catalog's existing `ExecutionControlResult` with exact
`Inspected` disposition. Missing executions and pending admissions without canonical status produce the same opaque
not-found problem; provider lifecycle values are never promoted into semantic status. The explain binding resolves
`IProcessExecutionExplainRepository` and writes the successful `ExecutionExplainArtifact` as exact canonical bytes
from `ExecutionExplainJsonSerializer`. Missing or malformed explanation targets use the catalog's opaque problem
variants. Conflicting status, runtime, or trace affinity fails closed. The original catalog remains route-neutral and
unchanged. The trace binding resolves `IProcessExecutionTraceRepository`; available artifacts are written as exact
canonical bytes from `ProcessExecutionTraceJsonSerializer`, while missing, active, and terminal-without-artifact
states map to the catalog's opaque not-found, conflict, and precondition-failed results.

## Related Packages

- `Cohesive.Api` for semantic API declarations.
- `Cohesive.Api.Execution` for the canonical execution-control catalog and safe result projections.
- `Cohesive.Identity` for identity context and scope resolution.
- `Cohesive.Processes`, `Cohesive.Relations`, and `Cohesive.Storage` for the runtime surfaces exposed by endpoints.

## Declared service operations

For an admitted `ServiceRuntime`, `MapServiceTransition<TInput,TOutcome>` projects the operation
without per-route repository loading or commit callbacks:

```csharp
using Cohesive.Adapters.AspNet.Services;

app.MapServiceTransition<ReviseNote, bool>(
    runtime, "revise", "/notes/{id}/revise",
    authorizationPolicyResolver: (_, requirement) => requirement.Id);
```

The CLR types must match the referenced Transition contracts. The caller sends the opaque reviewed token
in `X-Expected-Concurrency-Token` and receives the next token in the same response header. The response
body is the Transition outcome. Routes retain standard API metadata and native ASP.NET authorization
policy associations, while direct and HTTP calls both pass the runtime's mandatory authority binding.
Repository factories are not resolved during mapping. This initial profile supports existing subjects
without emissions; see [its guarantees and qualification](../../../docs/decisions/declarative-service-runtime.md).

Process entries and lifecycle controls project their native command contracts through the same service runtime:

```csharp
app.MapServiceProcessStart(runtime, "publish", "/notes/publish",
    authorizationPolicyResolver: (_, requirement) => requirement.Id);
app.MapServiceProcessControl<PauseProcessCommand>(runtime, "pause", "/notes/pause",
    authorizationPolicyResolver: (_, requirement) => requirement.Id);
```

`PauseProcessCommand` is the native `Cohesive.Execution` contract. A mismatched command CLR type fails mapping.
Native API result definitions determine statuses and response bodies. Service admission failures use the existing
`ExecutionApiProblem`; native Process decisions retain their own result. Start/control authority and exact-target
admission run in the shared service runtime, while ASP.NET policy metadata remains an additional host integration.
