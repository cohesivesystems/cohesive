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
the selected identity and passes the request operation cancellation token through body binding and evaluation.

## Related Packages

- `Cohesive.Api` for semantic API declarations.
- `Cohesive.Identity` for identity context and scope resolution.
- `Cohesive.Processes`, `Cohesive.Relations`, and `Cohesive.Storage` for the runtime surfaces exposed by endpoints.
