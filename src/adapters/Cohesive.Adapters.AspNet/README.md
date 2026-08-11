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
