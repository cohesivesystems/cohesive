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

## Related Packages

- `Cohesive.Api` for semantic API declarations.
- `Cohesive.Identity` for identity context and scope resolution.
- `Cohesive.Processes`, `Cohesive.Relations`, and `Cohesive.Storage` for the runtime surfaces exposed by endpoints.
