# Cohesive

Cohesive is a .NET library suite for semantic orchestration of existing infrastructure. The core idea is to model the semantics of configuration, relations, entities, processes, inference, training, and knowledge first, then attach concrete infrastructure through reusable adapters.

The reusable `Cohesive.*` libraries are intended to stand on their own and be adoptable incrementally across products and domains.

## Toolchain At A Glance

- `Cohesive.Configuration`: profile resolution, configuration overlays, and typed configuration projection.
- `Cohesive.Relations`: observation mapping, relation authoring, execution, query composition, hydration, and serialization.
- `Cohesive.Transitions` and `Cohesive.Processes`: declarative entity state, typed transitions, effect continuations, and process orchestration.
- `Cohesive.Presentation`: target-independent presentation modules, navigation, views, workspaces, fields, actions, flows, and design/accessibility semantics.
- `Cohesive.Api`: declarative API surfaces that connect semantic operations to HTTP bindings, endpoint metadata, and generated client/schema artifacts.
- `Cohesive.Host.Cli`: typed CLI command trees whose arguments, environment variables, and configuration providers flow through Cohesive configuration binding.
- `Cohesive.CodeGen.Cli`: build-facing code generation for shapes, APIs, constants, OpenAPI documents, and GraphQL schemas.
- `Cohesive.AI`: inference abstractions, training abstractions, vector storage contracts, numerics, text utilities, and semantic knowledge structures.
- `Cohesive.Adapters.*`: provider-specific interpretations for storage, messaging, runtimes, APIs, ML systems, code repositories, and other infrastructure.

## Building Blocks

### Cohesive.Configuration

`Cohesive.Configuration` handles environment and profile layering without pushing those concerns into application code. The current iteration includes:

- Active profile resolution with inheritance chains.
- Overlay composition on top of base configuration trees.
- Typed projection from command inputs or other source objects into hierarchical runtime settings.

Example:

```csharp
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var resolution = configuration.ResolveConfigurationProfile(new()
{
    ActiveProfileKey = "App:ActiveProfile",
    ProfilesSectionPath = "App:Profiles"
});

var profiled = configuration.CreateProfiledConfiguration(resolution);

var projection = new ConfigurationProjection<CommandOptions, RuntimeSettings>("TrainingRuntime");
projection.Set(false, x => x.EnableDemoDefinitions);
projection.Map(x => x.ProcessEngineMode, x => x.Modules.ProcessEngine.Mode);
projection.Map(x => x.ConnectionString, x => x.Infrastructure.AzureBlobStorage!["Default"].ConnectionString);

IReadOnlyDictionary<string, string?> overrides = projection.Build(commandOptions);
```

This makes it straightforward to keep runtime settings typed, composable, and environment-aware while still interoperating cleanly with the standard .NET configuration stack.

### Cohesive.Relations

`Cohesive.Relations` is the semantic layer for projection, mapping, joining, grouping, and querying. It works over observations rather than directly over storage-specific representations, which means the same authored relation can be executed in-memory, hydrated from repositories, or compiled to storage/query infrastructure via adapters.

Example:

```csharp
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;

var relation = Relation<LoadSearchDocument>
    .From<Load>()
    .Join<Carrier>((load, carrier) => load.CarrierId == carrier.Id)
    .Select((load, carrier) => new LoadSearchDocument
    {
        LoadId = load.Id,
        CarrierName = carrier.LegalName,
        Amount = load.TotalAmount,
        CarrierSafetyScore = carrier.SafetyScore
    });

var loadMapper = ObjectObservationMapper.For<Load>(new("Load")).MapAll().Build();
var carrierMapper = ObjectObservationMapper.For<Carrier>(new("Carrier")).MapAll().Build();

var outputs = await new RelationExecutor().ExecuteAsync(
    relation,
    [
        loadMapper.Map(load),
        carrierMapper.Map(carrier)
    ]);
```


The same toolchain also supports query composition over observation sources:

```csharp
using Cohesive.Relations.Queries;

var query = Query
    .From<CustomerReadModel>([customer], rootId: static root => root.CustomerId)
    .JoinOne<CustomerReadModel, string>(
        alias: "segment",
        source: SegmentSource,
        rootKeySelector: root => root.SegmentId
        )
    .JoinMany<CustomerReadModel, OrderReadModel, string>(
        alias: "orders",
        source: OrderSource,
        rootKey: root => root.CustomerId,
        foreignKey: order => order.CustomerId
        )
    .Select(ctx => new CustomerProfile(
        CustomerId: ctx.RootAs<CustomerReadModel>().CustomerId,
        Segment: ctx.RequireOne<SegmentReadModel>("segment"),
        Orders: ctx.Many<OrderReadModel>("orders")
        )
    );
```
This query can be executed against any observation source, including Cosmos, ElasticSearch, and SQL databases. When the underlying database supports relations, the relations will be executed directly in the database. Otherwise, an in-memory hydration engine will perform joins in the runtime.


### Cohesive.Transitions And Processes

`Cohesive.Transitions` models entity semantics: fields, invariants, transitions, emitted effects, and continuations. `Cohesive.Processes` orchestrates those transitions together with queries, pure computations, waits, and effect requests.

Entity transition example:

```csharp
using Cohesive.Transitions.Authoring;

public enum LoadStatus
{
    Draft = 0,
    Assigned = 1
}

public sealed class Load : Entity<Load>
{
    public sealed record AssignCarrierInput(string CarrierId);

    public Load()
    {
        Status = Field(
            FieldDefinition.Create(
                name: new(nameof(Status)),
                type: DomainTypes.Enum(nameof(LoadStatus), Enum.GetNames<LoadStatus>())),
            LoadStatus.Draft
            );

        CarrierId = Field<string?>(
            FieldDefinition.Create(
                name: new(nameof(CarrierId)),
                type: DomainTypes.String(),
                presence: FieldPresence.Optional),
            null);

        AssignCarrier = Transition<AssignCarrierInput>(
            nameof(AssignCarrier),
            t => t
                .Requires("CanAssignCarrier", (load, input) => load.Status == LoadStatus.Draft && input.CarrierId != "")
                .Set(load => load.CarrierId, (_, input) => input.CarrierId)
                .Set(load => load.Status, (_, _) => LoadStatus.Assigned)
                .EmitSnapshot("CarrierAssigned", (snapshot, input) => new
                {
                    loadId = snapshot.EntityId.Value,
                    input.CarrierId
                }));
    }

    public Field<LoadStatus> Status { get; }
    public Field<string?> CarrierId { get; }
    public Transition<Load, AssignCarrierInput> AssignCarrier { get; }
}
```

Process orchestration example:

```csharp
using Cohesive.Processes.Model;
using Cohesive.Processes.Runtime;

var process = new TypedProcessDefinition<string, string>(
    definition: new ProcessDefinitionBuilder("GreetingFlow")
        .AddEffectRequestNode(
            name: "greeting",
            requestExpression: ctx => new FormatGreetingRequest(ctx.RequireParameter<string>("name")),
            resultVariable: "greeting",
            nextNode: "end")
        .AddEndNode(
            name: "end",
            resultExpression: ctx => $"{ctx.RequireParameter<string>("name")}:{ctx.RequireVariable<string>("greeting")}")
        .Build(),
    inputParameterName: "name");

var run = await engine.ExecuteAsync(OperationContext.Create(), process, "alice");
```

For larger workflows, `Cohesive.Processes` also supports source-generated authoring via `IProcessDefinition<TInput, TOutput>` plus `[GenerateProcessDefinition(...)]`, which lowers authored `ProcessTask<T>` methods into `ProcessDefinition` instances at compile time.

### Cohesive.Presentation

`Cohesive.Presentation` captures frontend semantics as backend-declared modules rather than target-specific component trees. A presentation module can describe the shape of a user experience while leaving React, Blazor, native, or other render targets to interpret the same semantic surface.

The current iteration includes:

- `PresentationModuleDefinition` as the deployable boundary for navigation, views, workspaces, data sources, fields, actions, flows, expressions, design systems, target bindings, and annotations.
- `ViewDefinition`, `NavigationDefinition`, and `WorkspaceDefinition` for pages, panels, dashboards, forms, navigation shells, document workspaces, and coordinated child regions.
- `ActionDefinition`, `DataSourceDefinition`, `FlowDefinition`, and `FieldPresentationDefinition` for binding user-facing behavior to semantic endpoints, local state, relation projections, transitions, and process starts.
- Target-independent design intent, accessibility contracts, residency hints, and design-system bindings that adapters can interpret without owning the semantics.

This keeps the declaration of a product surface close to the semantic API and domain model, while still allowing different frontend runtimes to project that surface in their own idioms.

### Cohesive.Api

`Cohesive.Api` declares logical API operations once, then attaches HTTP, runtime, and generated-client interpretations to that declaration. It is the contract layer between semantic operations and concrete transport.

Example:

```csharp
using Cohesive.Api;

var definition = Api.Define("Shipping")
    .Entity<Shipment>()
    .Query("Get")
        .Route("GET", "/api/shipments/{id}")
        .RouteParameter<string>("id")
        .Returns<ShipmentDto>()
        .Done()
    .Command("Dispatch")
        .Route("POST", "/api/shipments/{id}/dispatch")
        .Body<DispatchShipmentRequest>()
        .Done()
    .Build();
```

The definition records stable endpoint ids, operation kind, request and response shapes, HTTP route/query/header/body metadata, tags, summaries, and optional transition metadata. The same `ApiDefinition` can be mapped onto ASP.NET Minimal APIs, emitted as TypeScript clients, or projected through OpenAPI and GraphQL adapters.

### Cohesive.Host.Cli

`Cohesive.Host.Cli` lives in `src/Cohesive.Host` and provides a typed command harness for console entrypoints that should share the same configuration model as services and jobs.

The CLI surface includes:

- Root commands and subcommands backed by typed configuration objects.
- Option and positional-argument mapping through `Cohesive.Configuration`.
- Configuration composition from shared providers, environment variables, and command-line values.
- Command middleware, validation, dynamic handler binding, JSON output helpers, and a test harness.
- `RunOrFallbackAsync` for executables that support both explicit CLI commands and ordinary ASP.NET or worker-service startup.

This makes command-line tools another projection of the same configuration and semantic operations, rather than a parallel argument-parsing model.

### Cohesive.CodeGen.Cli

`Cohesive.CodeGen.Cli` is the build-facing `cohesive-codegen` executable. It loads a compiled contracts assembly, discovers exported Cohesive contract surfaces, and writes generated artifacts into a target application.

Implemented emit kinds include:

- `shapes`: TypeScript declarations generated from Cohesive shape IR.
- `apis`: TypeScript client functions generated from exported `ApiDefinition` members.
- `constants`: TypeScript constant objects generated from exported contract constants.
- `openapi`: OpenAPI 3.1 JSON generated from exported API definitions.
- `graphql`: GraphQL SDL and introspection JSON generated from exported API definitions.

Writes are content-aware and atomic, which keeps frontend file watchers and incremental builds from churning when generated output is unchanged. See `src/Cohesive.CodeGen.Cli/README.md` for detailed CLI usage and MSBuild integration notes.

## Attaching Infrastructure Through Adapters

The `Cohesive.*` libraries define semantic contracts. The `Cohesive.Adapters.*` projects attach those contracts to concrete infrastructure.

| Semantic surface | Adapter | Example attachment |
| --- | --- | --- |
| Configuration bootstrap | `Cohesive.Adapters.AzureAppConfiguration` | Pull remote configuration overlays and Key Vault-backed values into profiled startup |
| Relations query compilation | `Cohesive.Adapters.Cosmos` | Compile observation predicates into parameterized Cosmos SQL |
| Durable orchestration | `Cohesive.Adapters.DurableTask` + `Cohesive.Adapters.AzureStorage` | Run authored processes on an Azure Storage-backed durable runtime |
| Vector persistence | `Cohesive.Adapters.Cosmos` | Store/query vector documents through `IVectorStore` |
| Local inference runtimes | `Cohesive.Adapters.ONNX`, `Cohesive.Adapters.MicrosoftML` | Implement `IEmbeddingModel`, `IPairScoringModel`, and related inference abstractions |
| Training runtimes | `Cohesive.Adapters.AzureML` | Implement `IModelTrainer` against Azure Machine Learning command jobs |
| Code and artifact infrastructure | `Cohesive.Adapters.GitHub`, `Cohesive.Adapters.AzureStorage`, `Cohesive.Adapters.Parquet` | Resolve code, package training assets, and persist datasets/artifacts |
| API and client surfaces | `Cohesive.Adapters.AspNet`, `Cohesive.Adapters.TypeScript` | Expose Cohesive processes and contracts over HTTP and generated clients |

### Adapter Catalog

- `Cohesive.Adapters.AspNet`: ASP.NET bindings for request operation context, declarative APIs, process management endpoints, etc.
- `Cohesive.Adapters.AzureAppConfiguration`: Azure App Configuration integration for profile-aware configuration overlays and hosted application startup.
- `Cohesive.Adapters.AzureML`: Azure Machine Learning implementations for model training and dataset registry operations.
- `Cohesive.Adapters.AzureStorage`: Azure Blob Storage helpers for named clients, target resolution, code packaging, and dataset output streams.
- `Cohesive.Adapters.Cosmos`: Cosmos DB support for container setup, query compilation, observation repository implementations, process storage, and vector storage.
- `Cohesive.Adapters.DurableTask`: Durable Task adapter for `Cohesive.Processes`, including process engine, host, orchestration, activities, and execution repository.
- `Cohesive.Adapters.GitHub`: GitHub App authentication and code repository access for workflows that need source artifacts from GitHub.
- `Cohesive.Adapters.GraphQL`: GraphQL schema, SDL, and introspection emission from `ApiDefinition` plus endpoint helpers for exposing generated schemas.
- `Cohesive.Adapters.Json`: JSON Schema builders, validators, and schema exports for Cohesive shape graph documents.
- `Cohesive.Adapters.MicrosoftML`: Microsoft ML tokenizer integration for reusable AI text-processing components.
- `Cohesive.Adapters.ONNX`: ONNX Runtime inference adapters for tokenization, embeddings, pair scoring, feature-vector scoring, validation, and score calibration.
- `Cohesive.Adapters.OpenApi`: OpenAPI document emission from `ApiDefinition` plus endpoint helpers for publishing generated OpenAPI documents.
- `Cohesive.Adapters.Parquet`: ParquetSharp-based column and row writers for efficient tabular dataset/artifact output.
- `Cohesive.Adapters.TypeScript`: TypeScript emitters for shape declarations, API clients, contract constants, choice types, and expression/code syntax.

Example: attaching a durable process engine (`IProcessEngine`) backed by Azure Storage:

```csharp
using Cohesive.Adapters.DurableTask;
using Cohesive.Adapters.DurableTask.AzureStorage;
using Cohesive.Processes.Runtime;
using DurableTask.AzureStorage;

services.AddAzureStorageDurableTaskEngine("training", worker =>
{
    worker
        .WithHostName("training")
        .WithTaskHubName("training-hub")
        .WithDefinitions(new DurableTaskProcessDefinitionRegistry().Register(new TrainingProcess().Define().Definition))
        .ConfigureAzureStorage((sp, settings) =>
        {
            settings.StorageAccountClientProvider = new StorageAccountClientProvider(
                connectionString: sp.GetRequiredService<IConfiguration>()["Infrastructure:AzureStorage:ConnectionString"]);
        });
});
```

The pattern is consistent across the repo: author semantics in the core libraries, then attach concrete infrastructure through adapters instead of mixing provider concerns into the semantic layer itself.

## Quick Cohesive.AI Demo

`Cohesive.AI` provides abstractions for inference, training, semantics, vector storage, numerics, and text. The point is to keep product code dependent on semantic contracts such as `IEmbeddingModel`, `IModelTrainer`, `IVectorStore`, and ontology APIs, while swapping the underlying runtime through adapters.

Inference, semantics, vector storage, and training can be composed like this:

```csharp
using System.Text;
using Azure.Identity;
using Cohesive.AI.Inference;
using Cohesive.AI.Semantics;
using Cohesive.AI.Training;
using Cohesive.AI.Vectors;
using Cohesive.Adapters.AzureML;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.ONNX;

IEmbeddingModel embeddings = OnnxBiEncoderEmbeddingModel.CreateFromSentenceTransformerExport(
    exportDirectory: "/models/all-MiniLM-L6-v2",
    modelName: "semantic-encoder" 
    );

var batch = await embeddings.EmbedAsync(new EmbeddingBatchRequest(
[
    Encoding.UTF8.GetBytes("ship to").AsMemory(),
    Encoding.UTF8.GetBytes("bill to").AsMemory()
]));

var ontology = new OntologyBuilder()
    .AddConcept(new("party.role", "Party Role"))
    .AddConcept(new("party.ship_to", "Ship To"))
    .AddConcept(new("party.bill_to", "Bill To"))
    .AddParent("party.ship_to", "party.role")
    .AddParent("party.bill_to", "party.role")
    .AddScopedMeaning("edi.204.n101", "ST", "party.ship_to")
    .AddScopedMeaning("edi.204.n101", "BT", "party.bill_to")
    .Build();

var closure = OntologyClosure.Create(ontology);
var shipToIsPartyRole = closure.IsSubConceptOf("party.ship_to", "party.role");

IVectorStore vectors = new CosmosVectorStore(container);
await vectors.UpsertAsync(new VectorDocument(
    Id: "party.ship_to",
    PartitionKey: "ontology",
    Embedding: batch.Embeddings[0],
    Metadata: Encoding.UTF8.GetBytes("{\"conceptId\":\"party.ship_to\"}")
    )
);

IModelTrainer trainer = new AzureMLModelTrainer(
    credential: new DefaultAzureCredential(),
    options: new AzureMLModelTrainerOptions(
        SubscriptionId: "...",
        ResourceGroupName: "rg-ml",
        WorkspaceName: "cohesive-ml" 
        )
    );

var job = await trainer.StartAsync(new TrainingRequest(
    ModelName: "relation-inference",
    BaseVersion: null,
    Datasets:
    [
        new TrainingDatasetArtifact(
            Name: "examples",
            Location: new Uri("https://storage.example/datasets/examples.parquet"),
            Kind: TrainingDatasetArtifactKind.File,
            Format: "parquet",
            SchemaHash: null,
            RowCount: null
            )
    ],
    Code: new TrainingCodeArtifact(
        BlobUri: "https://storage.example/code/relation-inference.zip",
        Version: "sha256:abc123"
        ),
    OutputModelName: "relation-inference-v2",
    ExperimentName: "relation-inference",
    ComputeTarget: "gpu-cluster",
    ConfigJson: """
    {
      "command": "python train.py --data ${{inputs.examples}} --output ${{outputs.trained_model}}",
      "environmentId": "/subscriptions/.../resourceGroups/.../providers/Microsoft.MachineLearningServices/workspaces/.../environments/pytorch/versions/1"
    }
    """));

var status = await trainer.GetStatusAsync(job.JobId);
```

That example demonstrates the current intent of `Cohesive.AI`:

- Semantic meaning is expressed explicitly through ontologies and closures.
- Inference depends on contracts such as `IEmbeddingModel`, not directly on ONNX sessions.
- Training depends on `IModelTrainer`, not directly on Azure ML job APIs.
- Vector storage depends on `IVectorStore`, not directly on Cosmos container logic.
- Low-level performance-sensitive math still lives in reusable primitives such as `Cohesive.AI.Numerics.VectorMath`.

## Solution Layout

- `src/Cohesive`: core primitives, prelude, shapes, domain helpers.
- `src/Cohesive.Configuration`: profile resolution, overlays, configuration projections, dependency selectors.
- `src/Cohesive.Relations`: relation authoring, execution, hydration, mapping, queries, and serialization.
- `src/Cohesive.Transitions`: entity/transition modeling and declarative runtime support.
- `src/Cohesive.Processes`: process definition, authoring, runtime execution, and typed engine helpers.
- `src/Cohesive.Presentation`: target-independent presentation, navigation, workspace, data-source, action, and flow definitions.
- `src/Cohesive.Api`: logical API operation definitions, endpoint metadata, HTTP bindings, and code generation contracts.
- `src/Cohesive.Host`: host helpers, including the `Cohesive.Host.Cli` typed command infrastructure.
- `src/Cohesive.CodeGen.Cli`: build-facing `cohesive-codegen` executable for TypeScript, OpenAPI, and GraphQL artifacts.
- `src/Cohesive.AI`: inference, training, vectors, numerics, text, semantics, and model registry contracts.
- `src/adapters/Cohesive.Adapters.*`: infrastructure-specific adapters.
- `src/Cohesive.Analyzers`: source generators and analyzers, including process authoring support.
- `src/Cohesive.Examples`: example domain code using the current transitions surface.

## Prerequisites

- .NET SDK 10 (`net10.0`).

## Build And Test

Build and test the reusable toolchain:

```bash
dotnet restore Cohesive.sln
dotnet build Cohesive.sln -c Release
dotnet test Cohesive.sln
```

Create local NuGet packages for downstream sample applications:

```bash
bash ./eng/pack-local.sh 0.1.0-dev.local
```

Point the downstream app at the local NuGet feed with:

```bash
dotnet nuget add source /Users/eulerfx/code/.feeds/nuget/cohesive-local --name cohesive-local
```

Create local npm packages for downstream applications. Start the local feed in one terminal:

```bash
corepack pnpm npm:feed
```

Then publish dev packages to it from another terminal:

```bash
corepack pnpm npm:publish-local 0.1.0-dev.local
```

Point the downstream app at the local feed with:

```ini
@cohesivesystems:registry=http://localhost:4873/
```

The public package repository itself should keep `.npmrc` pointed at `https://registry.npmjs.org/`. Local feed routing belongs in consuming app repositories such as Ari or sample applications.

## Public Package Publishing

The `release-packages` workflow publishes packages from a tag such as `v0.1.0-alpha.1` or from a manual workflow run with a SemVer version.

- NuGet packages publish with NuGet trusted publishing through `NuGet/login`.
- npm packages are packed with pnpm, then published to npmjs.org with npm trusted publishing.
- Prerelease versions map to npm dist-tags: `alpha.*` to `alpha`, `preview.*` to `preview`, `rc.*` to `rc`, and other prereleases to `next`.
- Stable versions publish with the `latest` npm dist-tag.

Before the first npm trusted-publishing release, each `@cohesivesystems/*` package must already exist on npmjs.com. Bootstrap each package once with a temporary/manual publish, then configure its trusted publisher:

```bash
npm install --global npm@^11.15.0
npm login
npm trust github @cohesivesystems/presentation-contracts --repo cohesivesystems/cohesive --file release-packages.yml --allow-publish
```

Repeat the `npm trust github` command for each frontend package under `src/frontend/*`. On npmjs.com, the equivalent trusted publisher settings are GitHub Actions, organization/user `cohesivesystems`, repository `cohesive`, workflow filename `release-packages.yml`, no environment, and allowed action `npm publish`.

Useful focused runs:

```bash
dotnet test src/Cohesive.Configuration.Tests/Cohesive.Configuration.Tests.csproj
dotnet test src/Cohesive.Relations.Tests/Cohesive.Relations.Tests.csproj
dotnet test src/Cohesive.AI.Tests/Cohesive.AI.Tests.csproj
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj
dotnet test src/Cohesive.Examples/Cohesive.Examples.csproj
```

## Start Here

- Configuration profiles and projections:
  - `src/Cohesive.Configuration.Tests/ConfigurationProfileConfigurationTests.cs`
  - `src/Cohesive.Configuration.Tests/ConfigurationProjectionTests.cs`
- Relations authoring and execution:
  - `src/Cohesive.Relations.Tests/RelationSyntaxTests.cs`
  - `src/Cohesive.Relations.Tests/QueryProjectionTests.cs`
- Transition and process orchestration:
  - `src/Cohesive.Examples/Transportation/Load.cs`
  - `src/Cohesive.Tests/Model/ProcessEngineRuntimeTests.cs`
  - `src/Cohesive.Tests/Model/ProcessNativeEntityInteractionTests.cs`
- Presentation and API contracts:
  - `src/Cohesive.Tests/Presentation/ViewDefinitionExtensionsTests.cs`
  - `src/Cohesive.Tests/Api/ApiDefinitionTests.cs`
- CLI and code generation:
  - `src/Cohesive.Host.Tests/Cli/CliApplicationTests.cs`
  - `src/Cohesive.CodeGen.Cli/README.md`
  - `src/Cohesive.Tests/CodeGen/CodeGenCliParserTests.cs`
- AI semantics, inference, and training contracts:
  - `src/Cohesive.AI.Tests/Semantics/OntologyClosureTests.cs`
  - `src/Cohesive.AI/Inference`
  - `src/Cohesive.AI/Training`
  - `src/Cohesive.AI/Semantics`

## Notes

- The root `src/Cohesive` project remains a convenience aggregation point over the main libraries.
- The adapters are intentionally separated from the semantic libraries so the core abstractions stay reusable across infrastructure choices.
