using System.Text;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Adapters.AspNet.Relations;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Tests.Api;

public sealed class EntityApiEndpointTests
{
    const string TenantPartitionContextKey = "TestTenantPartition";
    static readonly QualifiedShapeId NoteQueryShape = new(
        new GraphId("tests/aspnet/entities"),
        new ShapeId("Note"));
    static readonly QueryParameterId NotePrefixParameter = new("prefix");
    static readonly QueryResultId NoteRowsResult = new("rows");
    static readonly ExecutionDefinitionDocument NoteRevisedEvent = InteractionContractDocuments.Create(
        new("tests/aspnet/entities/note/revised"),
        new("revision/1"),
        new DomainEventContractDefinition(new(
            new(new ScalarTypeRef(ScalarTypeKind.String)),
            new("note-revised/v1"))),
        TestProvenance("tests/api/entity-api-endpoint/note-revised"));
    static readonly InteractionContractCatalog NoteInteractionContracts = CreateInteractionCatalog(NoteRevisedEvent);

    [Fact]
    public void MapEntityApiDefinition_CanFilterSharedOperationNamesAndCustomizeEndpointNames()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        var app = builder.Build();
        var entity = NoteEntity.Instance;
        var definition = Cohesive.Api.Api.Define()
            .Entity<NoteResource>()
                .Query("Get")
                    .Route("GET", "/notes/{id}")
                    .RouteParameter<string>("id")
                    .Returns<NoteResource>()
                    .Done()
            .Entity<OtherNoteResource>()
                .Query("Get")
                    .Route("GET", "/other_notes/{id}")
                    .RouteParameter<string>("id")
                    .Returns<OtherNoteResource>()
                    .Done()
            .Build();

        app.MapEntityApiDefinition(definition, new EntityApiEndpointOptions
        {
            Entity = entity.Definition,
            OperationFilter = static operation => operation.Entity?.Value == nameof(NoteResource),
            EndpointNameSelector = static operation => $"Note_{operation.Name}"
        }
            .Bind(EntityApiOperationBinding.Get("Get", static (_, snapshot) => Results.Ok(ToResource(snapshot)))));

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>());

        Assert.Equal("/notes/{id}", endpoint.RoutePattern.RawText);
        var name = Assert.Single(endpoint.Metadata.OfType<IEndpointNameMetadata>());
        Assert.Equal("Note_Get", name.EndpointName);
    }

    [Fact]
    public void MapEntityApiDefinition_CanBindSharedOperationNamesByEndpointExtension()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        var app = builder.Build();
        var entity = NoteEntity.Instance;
        var api = Cohesive.Api.Api.Define("Notes");
        var noteGet = api.Entity<NoteResource>()
            .Query("Get")
            .Route("GET", "/notes/{id}")
            .RouteParameter<string>("id")
            .Returns<NoteResource>()
            .Build();
        _ = api.Entity<OtherNoteResource>()
            .Query("Get")
            .Route("GET", "/other_notes/{id}")
            .RouteParameter<string>("id")
            .Returns<OtherNoteResource>()
            .Build();
        var definition = api.Build();

        app.MapEntityApiDefinition(definition, new EntityApiEndpointOptions
        {
            Entity = entity.Definition,
            EndpointNameSelector = static operation => $"Note_{operation.Name}"
        }
            .Bind(noteGet.Get(static (_, snapshot) => Results.Ok(ToResource(snapshot)))));

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>());

        Assert.Equal("/notes/{id}", endpoint.RoutePattern.RawText);
        var name = Assert.Single(endpoint.Metadata.OfType<IEndpointNameMetadata>());
        Assert.Equal("Note_Get", name.EndpointName);
    }

    [Fact]
    public async Task MapEntityApiDefinition_BindsCrudQueryTransitionAndOutbox()
    {
        var entity = NoteEntity.Instance;
        var repository = new InMemoryEntityOutboxRepository(entity.Definition, partitionKeyFieldName: nameof(NoteState.Tenant));

        var app = CreateApp(entity, repository);

        var create = await InvokeAsync(
            app,
            route: "/notes",
            method: "POST",
            body: new CreateNoteRequest("note-1", "alpha one")
            );
        Assert.Equal(StatusCodes.Status200OK, create.StatusCode);
        Assert.Equal("alpha one", ReadJson(create.Body).GetProperty(nameof(NoteResource.Text)).GetString());

        var query = await InvokeAsync(
            app,
            route: "/notes",
            method: "GET",
            queryString: "?prefix=alpha");
        Assert.Equal(StatusCodes.Status200OK, query.StatusCode);
        Assert.Single(ReadJson(query.Body).GetProperty(nameof(QueryNotesResponse.Items)).EnumerateArray());
        var evaluator = Assert.IsType<CanonicalQueryRecordingEvaluator>(
            app.Services.GetRequiredService<IRelationQueryEvaluator>());
        var evaluation = Assert.IsType<RelationQueryEvaluation>(evaluator.Evaluation);
        var prefixEvidence = Assert.Single(
            evaluation.Parameters,
            parameter => parameter.Input == RelationQueryInputIds.ForParameter(NotePrefixParameter));
        Assert.Equal(RelationQueryParameterEvidenceState.Provided, prefixEvidence.State);
        Assert.Equal(ObservationValue.FromString("alpha"), prefixEvidence.Value);

        var mappedRoutes = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Single(mappedRoutes, endpoint =>
            endpoint.RoutePattern.RawText == "/notes"
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);

        var revise = await InvokeAsync(
            app,
            route: "/notes/{id}",
            method: "POST",
            routeValues: new() { ["id"] = "note-1" },
            body: new ReviseNoteRequest("beta one"));
        Assert.Equal(StatusCodes.Status200OK, revise.StatusCode);
        Assert.Equal("beta one", ReadJson(revise.Body).GetProperty(nameof(NoteResource.Text)).GetString());

        var loaded = await InvokeAsync(
            app,
            route: "/notes/{id}",
            method: "GET",
            routeValues: new() { ["id"] = "note-1" });
        Assert.Equal(StatusCodes.Status200OK, loaded.StatusCode);
        Assert.Equal("beta one", ReadJson(loaded.Body).GetProperty(nameof(NoteResource.Text)).GetString());

        var inspected = await InvokeAsync(
            app,
            route: "/notes/{id}/inspect",
            method: "POST",
            routeValues: new() { ["id"] = "note-1" },
            body: new InspectNoteRequest("beta")
            );
        Assert.Equal(StatusCodes.Status200OK, inspected.StatusCode);
        Assert.True(ReadJson(inspected.Body).GetProperty(nameof(NoteInspectionResponse.Matches)).GetBoolean());

        var envelope = Assert.IsType<DomainEventEnvelope>(Assert.Single(repository.OutboxEnvelopes));
        Assert.Equal(Reference(NoteRevisedEvent), envelope.Contract.Definition);
        Assert.Equal("beta one", envelope.Payload.Value?.GetString());
        var origin = Assert.IsType<TransitionInteractionOrigin>(envelope.Context.Origin);
        Assert.Equal(new EntityTypeName(entity.Definition.Shape.Id.Value), origin.Entity.EntityType);
        Assert.Equal(new EntityId("note-1"), origin.Entity.EntityId);
    }

    [Fact]
    public void TransitionStateProjector_VerifiesDecisionEvidenceBeforeProjection()
    {
        var entity = NoteEntity.Instance;
        var plan = CompileReviseTransition(entity);
        var state = ObservationValue.FromObject(new NoteState(
            Id: "note-1",
            Tenant: "tenant-a",
            Text: "before",
            UpdatedAtUtc: new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var input = ObservationValue.FromObject(new NoteEntity.ReviseInput(
            Text: "after",
            UpdatedAtUtc: new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));
        var decision = TransitionReferenceInterpreter.DecideFullState(
            plan,
            new("tests/aspnet/entities/note/revise/projection"),
            PortableValue.Concrete(plan.Definition.Input, input),
            PortableValue.Concrete(plan.Definition.Observation, state));

        var projected = TransitionStateProjector.Apply(state, decision);

        Assert.Equal("after", projected.GetProperty(nameof(NoteState.Text)).GetString());
        var mismatched = state.WithField(
            FieldPath.FromField(nameof(NoteState.Text)),
            ObservationValue.FromString("changed-concurrently"));
        Assert.Throws<InvalidOperationException>(() => TransitionStateProjector.Apply(mismatched, decision));
    }

    [Fact]
    public void EntityApiEndpointOptions_ConventionalActivationIdPinsRequestAndOperation()
    {
        var plan = CompileReviseTransition(NoteEntity.Instance);
        var operation = Assert.Single(
            CreateApi(plan.DefinitionReference).Operations,
            static candidate => candidate.Name == "Revise");
        var httpContext = new DefaultHttpContext { TraceIdentifier = "request/42" };

        var activation = EntityApiEndpointOptions.CreateConventionalActivationId(httpContext, operation);

        Assert.Equal(
            "aspnet/request/request%2F42/operation/NoteResource.Revise",
            activation.Value);
    }

    [Fact]
    public async Task MapEntityApiDefinition_EmittingTransitionWithoutCanonicalLoweringFailsBeforeMutation()
    {
        var entity = NoteEntity.Instance;
        var repository = new InMemoryEntityOutboxRepository(
            entity.Definition,
            partitionKeyFieldName: nameof(NoteState.Tenant));
        var app = CreateApp(
            entity,
            repository,
            configureTransitionEmissions: false);

        await InvokeAsync(
            app,
            route: "/notes",
            method: "POST",
            body: new CreateNoteRequest("note-1", "before"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(
            app,
            route: "/notes/{id}",
            method: "POST",
            routeValues: new() { ["id"] = "note-1" },
            body: new ReviseNoteRequest("after")));

        Assert.Contains("no canonical interaction catalog", error.Message, StringComparison.Ordinal);
        Assert.Empty(repository.OutboxEnvelopes);
        var retained = await repository.TryGet(OperationContext.Create(), id: "note-1", options: EntityReadOptions.Full);
        Assert.NotNull(retained);
        Assert.Equal("before", retained.Entity.Observation.GetField(nameof(NoteState.Text)).GetString());
    }

    [Fact]
    public void MapEntityApiDefinition_RejectsTransitionPlanThatDoesNotMatchExactApiReference()
    {
        var entity = NoteEntity.Instance;
        var plan = CompileReviseTransition(entity);
        var wrongReference = new ExecutionDefinitionReference(
            plan.DefinitionReference.DefinitionId,
            plan.DefinitionReference.RevisionId,
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string('b', 64)));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => app.MapEntityApiDefinition(
            CreateApi(wrongReference),
            new EntityApiEndpointOptions { Entity = entity.Definition }
                .Bind(EntityApiOperationBinding.Transition(
                    "Revise",
                    plan,
                    createTransitionInput: null,
                    createResult: static (_, _) => Results.Ok()))));

        Assert.Contains(plan.DefinitionReference.DefinitionId.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapEntityApiDefinition_UsesPartitionKeyPolicyForPartitionedPointReads()
    {
        var entity = NoteEntity.Instance;
        var partitionKeyPolicy = new EntityPartitionKeyPolicy(
            description: "tenant field and request tenant item",
            writePartitionKeyResolver: static (_, snapshot) => snapshot.Observation.GetField(nameof(NoteState.Tenant)).GetRequiredString(),
            pointReadPartitionKeyResolver: static (context, _) =>
                context.TryGetItem<string>(TenantPartitionContextKey, out var tenant) ? tenant : null
            );
        var repository = new InMemoryEntityOutboxRepository(entity.Definition, partitionKeyPolicy);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await repository.Upsert(OperationContext.Create(), new(entity.Definition.CreateState(
            entityId: "note-1",
            new NoteState("note-1", "tenant-a", "alpha tenant", now)).Snapshot));
        await repository.Upsert(OperationContext.Create(), new(entity.Definition.CreateState(
            entityId: "note-1",
            new NoteState("note-1", "tenant-b", "beta tenant", now)).Snapshot));

        var app = CreateApp(
            entity,
            repository,
            operationContext: OperationContext.Create().WithItem(TenantPartitionContextKey, "tenant-b"),
            partitionKeyPolicy: partitionKeyPolicy);

        var loaded = await InvokeAsync(
            app,
            route: "/notes/{id}",
            method: "GET",
            routeValues: new() { ["id"] = "note-1" });

        Assert.Equal(StatusCodes.Status200OK, loaded.StatusCode);
        var resource = ReadJson(loaded.Body);
        Assert.Equal("tenant-b", resource.GetProperty(nameof(NoteResource.Tenant)).GetString());
        Assert.Equal("beta tenant", resource.GetProperty(nameof(NoteResource.Text)).GetString());
    }

    [Fact]
    public async Task MapEntityApiDefinition_UsesPartitionKeyPolicyProviderForPartitionedPointReads()
    {
        var entity = NoteEntity.Instance;
        var partitionKeyPolicy = new EntityPartitionKeyPolicy(
            description: "tenant field and request tenant item",
            writePartitionKeyResolver: static (_, snapshot) => snapshot.Observation.GetField(nameof(NoteState.Tenant)).GetRequiredString(),
            pointReadPartitionKeyResolver: static (context, _) =>
                context.TryGetItem<string>(TenantPartitionContextKey, out var tenant) ? tenant : null
            );
        var provider = new DelegatingEntityPartitionKeyPolicyProvider(_ => partitionKeyPolicy);
        var repository = new InMemoryEntityOutboxRepository(entity.Definition, partitionKeyPolicy);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await repository.Upsert(OperationContext.Create(), new(entity.Definition.CreateState(
            entityId: "note-1",
            new NoteState("note-1", "tenant-a", "alpha tenant", now)).Snapshot));
        await repository.Upsert(OperationContext.Create(), new(entity.Definition.CreateState(
            entityId: "note-1",
            new NoteState("note-1", "tenant-b", "beta tenant", now)).Snapshot));

        var app = CreateApp(
            entity,
            repository,
            operationContext: OperationContext.Create().WithItem(TenantPartitionContextKey, "tenant-b"),
            configureServices: services => services.AddEntityPartitionKeyPolicyProvider(provider),
            partitionKeyPolicyResolver: static (sp, entityDefinition) => sp
                .GetRequiredService<IEntityPartitionKeyPolicyProvider>()
                .GetPartitionKeyPolicy(entityDefinition));

        var loaded = await InvokeAsync(
            app,
            route: "/notes/{id}",
            method: "GET",
            routeValues: new() { ["id"] = "note-1" });

        Assert.Equal(StatusCodes.Status200OK, loaded.StatusCode);
        Assert.Same(partitionKeyPolicy, provider.GetPartitionKeyPolicy(entity.Definition));
        var resource = ReadJson(loaded.Body);
        Assert.Equal("tenant-b", resource.GetProperty(nameof(NoteResource.Tenant)).GetString());
        Assert.Equal("beta tenant", resource.GetProperty(nameof(NoteResource.Text)).GetString());
    }

    static WebApplication CreateApp(
        NoteEntity entity,
        InMemoryEntityOutboxRepository repository,
        OperationContext? operationContext = null,
        EntityPartitionKeyPolicy? partitionKeyPolicy = null,
        Action<IServiceCollection>? configureServices = null,
        Func<IServiceProvider, EntityDefinition, EntityPartitionKeyPolicy?>? partitionKeyPolicyResolver = null,
        Func<EntityApiCommitContext, TransitionEmissionLoweringPolicy>? transitionEmissionPolicy = null,
        bool configureTransitionEmissions = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(operationContext ?? OperationContext.Create());
        builder.Services.RegisterEntityRepository(entity, (_, _) => repository);
        builder.Services.AddSingleton<IRelationQueryEvaluator, CanonicalQueryRecordingEvaluator>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        var revisePlan = CompileReviseTransition(entity);
        var interactionContracts = configureTransitionEmissions ? NoteInteractionContracts : null;
        Func<EntityApiCommitContext, TransitionEmissionLoweringPolicy>? emissionPolicy = configureTransitionEmissions
            ? transitionEmissionPolicy ?? CreateDirectEmissionPolicy
            : null;
        var api = CreateApi(revisePlan.DefinitionReference);
        var queryEndpoint = Assert.Single(api.Endpoints, static endpoint => endpoint.Name == "Query");
        List<NoteResource> queryDocuments = [];
        app.MapEntityApiDefinition(api, new EntityApiEndpointOptions
        {
            Entity = entity.Definition,
            PartitionKeyPolicy = partitionKeyPolicy,
            PartitionKeyPolicyResolver = partitionKeyPolicyResolver
        }
            .Bind(EntityApiOperationBinding.Get("Get", static (_, snapshot) => Results.Ok(ToResource(snapshot))))
            .Bind(EntityApiOperationBinding.Create(
                "Create",
                static (context, request) =>
                {
                    var create = (CreateNoteRequest)request!;
                    return context.Entity.CreateState(create.Id, new NoteState(
                        Id: create.Id,
                        Tenant: "tenant-a",
                        Text: create.Text,
                        UpdatedAtUtc: context.OperationContext.UtcNow));
                },
                (_, snapshot) =>
                {
                    var resource = ToResource(snapshot);
                    queryDocuments.RemoveAll(document =>
                        string.Equals(document.Id, resource.Id, StringComparison.Ordinal));
                    queryDocuments.Add(resource);
                    return Results.Ok(resource);
                }))
            .Bind(EntityApiOperationBinding.Transition(
                "Revise",
                revisePlan,
                static (context, request) =>
                {
                    var revise = (ReviseNoteRequest)request!;
                    return new NoteEntity.ReviseInput(revise.Text, context.OperationContext.UtcNow);
                },
                static (_, snapshot) => Results.Ok(ToResource(snapshot)),
                interactionContracts: interactionContracts,
                createEmissionPolicy: emissionPolicy))
            .Bind(EntityApiOperationBinding.Load(
                "Inspect",
                static (_, snapshot, request) =>
                {
                    var inspect = (InspectNoteRequest)request!;
                    var resource = ToResource(snapshot);
                    return Results.Ok(new NoteInspectionResponse(
                        resource.Id,
                        resource.Text.Contains(inspect.Contains, StringComparison.Ordinal)));
                })));
        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions()
            .Bind(queryEndpoint.RelationQuery(
                static (context, request) => CreateNoteQueryEvaluation(
                    context.EvaluationId,
                    Assert.IsType<QueryNotesRequest>(request).Prefix),
                (context, _) =>
                {
                    var query = Assert.IsType<QueryNotesRequest>(context.Request);
                    var documents = string.IsNullOrWhiteSpace(query.Prefix)
                        ? queryDocuments
                        : queryDocuments
                            .Where(document => document.Text.StartsWith(
                                query.Prefix,
                                StringComparison.Ordinal))
                            .ToList();
                    return Results.Ok(new QueryNotesResponse([.. documents]));
                })));

        return app;
    }

    static RelationQueryEvaluation CreateNoteQueryEvaluation(
        RelationQueryEvaluationId evaluationId,
        string? prefix)
    {
        var author = RelationQuery.Structural();
        var prefixParameter = author.Parameter(
            new ScalarTypeRef(ScalarTypeKind.String),
            presence: FieldPresence.Required,
            id: NotePrefixParameter);
        var notes = author.Source(
            NoteQueryShape,
            nodeId: new QueryNodeId("notes"),
            bindingId: new ValueBindingId("note"));
        var filtered = author.Filter(
            notes.Node,
            Expr.StartsWith(
                notes.Binding.Field(nameof(NoteState.Text)),
                prefixParameter.Expression),
            nodeId: new QueryNodeId("notes-by-prefix"));
        var rows = author.Rows(filtered, id: NoteRowsResult);
        var evaluation = author.BuildQuery(
                new QueryId("notes"),
                new QueryName("Notes"),
                [rows])
            .CreateDocument()
            .Evaluate(evaluationId)
            .Select(rows.Id);
        return evaluation
            .Set(
                prefixParameter.Id,
                ObservationValue.FromString(prefix ?? string.Empty),
                evidenceReference: "aspnet/query/prefix")
            .Build();
    }

    static CompiledTransitionPlan CompileReviseTransition(NoteEntity entity)
    {
        var authored = TransitionAuthoring.Create<NoteEntity, NoteEntity.ReviseInput, bool>(
            entity.Definition.Shape,
            new(
                definitionId: new("tests/aspnet/entities/note/revise"),
                revisionId: new("revision/1"),
                bodyId: new("revise/body"),
                provenance: new(
                    new(TransitionAuthoring.Producer),
                    new("tests/api/entity-api-endpoint/revise"),
                    DocumentOrigin.Generated),
                displayName: "Revise note"),
            transition => transition
                .Set(new("revise/set-text"), note => note.Text, (_, input) => input.Text)
                .Set(new("revise/set-updated-at"), note => note.UpdatedAtUtc, (_, input) => input.UpdatedAtUtc)
                .Emit(new("revise/note-revised"), Reference(NoteRevisedEvent), (_, input) => input.Text)
                .Return(new("revise/applied"), TransitionOutcomeDisposition.Applied, true));
        var compilation = authored.Compile();
        Assert.True(
            compilation.IsSuccessful,
            string.Join(
                Environment.NewLine,
                compilation.Validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        return Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
    }

    static TransitionEmissionLoweringPolicy CreateDirectEmissionPolicy(EntityApiCommitContext context) =>
        new((intent, index) =>
        {
            var activation = context.Decision?.Evidence.Activation
                ?? throw new InvalidOperationException("A direct Transition emission requires activation evidence.");
            var identity = $"{activation.Value}/emission/{index}";
            return new(
                new(identity),
                new TransitionInteractionOrigin(
                    context.Decision.Evidence.Definition,
                    intent.Node,
                    new(new(context.Entity.Shape.Id.Value), new(context.EntityId)),
                    new("revise/applied")),
                new(activation.Value),
                causationId: null,
                new(
                    "tests/aspnet/entity-api",
                    context.NewState.Observation.GetField(nameof(NoteState.Tenant)).GetString()),
                new(identity),
                ordering: null,
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
                TestProvenance("tests/api/entity-api-endpoint/direct-transition"));
        });

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static InteractionContractCatalog CreateInteractionCatalog(params ExecutionDefinitionDocument[] documents)
    {
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        if (!validation.IsValid || catalog is null)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        }
        return catalog;
    }

    static ExecutionProvenance TestProvenance(string source) => new(
        new("entity-api-endpoint-tests", "1"),
        new(source),
        DocumentOrigin.Generated);

    static ApiDefinition CreateApi(ExecutionDefinitionReference reviseTransition) => Cohesive.Api.Api.Define()
        .Entity<NoteResource>()
            .Query("Query")
                .Route("GET", "/notes")
                .Query<QueryNotesRequest>()
                .Returns<QueryNotesResponse>()
                .Done()
            .Query("Get")
                .Route("GET", "/notes/{id}")
                .RouteParameter<string>("id")
                .Returns<NoteResource>()
                .Done()
            .Command("Create")
                .Route("POST", "/notes")
                .Body<CreateNoteRequest>()
                .Returns<NoteResource>()
                .Done()
            .Command("Revise")
                .Route("POST", "/notes/{id}")
                .RouteParameter<string>("id")
                .Body<ReviseNoteRequest>()
                .Returns<NoteResource>()
                .Transition(reviseTransition)
                .Done()
            .Command("Inspect")
                .Route("POST", "/notes/{id}/inspect")
                .RouteParameter<string>("id")
                .Body<InspectNoteRequest>()
                .Returns<NoteInspectionResponse>()
                .Done()
            .Build();

    static NoteResource ToResource(EntitySnapshot snapshot) => new(
        Id: snapshot.Entity.Observation.GetField(nameof(NoteState.Id)).GetString() ?? throw new InvalidOperationException("Note id is required."),
        Tenant: snapshot.Entity.Observation.GetField(nameof(NoteState.Tenant)).GetString() ?? throw new InvalidOperationException("Note tenant is required."),
        Text: snapshot.Entity.Observation.GetField(nameof(NoteState.Text)).GetString() ?? throw new InvalidOperationException("Note text is required."),
        UpdatedAtUtc: snapshot.Entity.Observation.GetField(nameof(NoteState.UpdatedAtUtc)).GetDateTimeOffset());

    static JsonElement ReadJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    sealed record InvocationResult(int StatusCode, string Body);

    sealed record NoteState(string Id, string Tenant, string Text, DateTimeOffset UpdatedAtUtc);

    sealed record NoteResource(string Id, string Tenant, string Text, DateTimeOffset UpdatedAtUtc);

    sealed record OtherNoteResource(string Id);

    sealed record CreateNoteRequest(string Id, string Text);

    sealed record ReviseNoteRequest(string Text);

    sealed record InspectNoteRequest(string Contains);

    sealed record NoteInspectionResponse(string Id, bool Matches);

    sealed record QueryNotesRequest(string? Prefix);

    sealed record QueryNotesResponse(NoteResource[] Items);

    sealed class CanonicalQueryRecordingEvaluator : IRelationQueryEvaluator
    {
        public RelationQueryEvaluation? Evaluation { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
            RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(evaluation);
            cancellationToken.ThrowIfCancellationRequested();
            Evaluation = evaluation;
            CancellationToken = cancellationToken;
            var compilation = RelationQueryStaticCompiler.Compile(evaluation.Compilation);
            if (compilation.IsSuccessful)
            {
                throw new InvalidOperationException(
                    "The ASP.NET entity-query fixture intentionally omits source shape documents so the canonical " +
                    "outcome stops at structured compilation diagnostics.");
            }

            return ValueTask.FromResult(new RelationQueryEvaluationOutcome(evaluation, compilation));
        }
    }

    sealed class NoteEntity : Entity<NoteEntity>
    {
        public sealed record ReviseInput(string Text, DateTimeOffset UpdatedAtUtc);

        public NoteEntity()
        {
            Id = WriteOnceField<string>(nameof(Id));
            Tenant = WriteOnceField<string>(nameof(Tenant));
            Text = MutableField<string>(nameof(Text));
            UpdatedAtUtc = MutableField<DateTimeOffset>(nameof(UpdatedAtUtc));

        }

        public Field<string> Id { get; }

        public Field<string> Tenant { get; }

        public Field<string> Text { get; }

        public Field<DateTimeOffset> UpdatedAtUtc { get; }

    }

    static async Task<InvocationResult> InvokeAsync(
        WebApplication app,
        string route,
        string method,
        Dictionary<string, object?>? routeValues = null,
        object? body = null,
        string? queryString = null
    )
    {
        var endpoint = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => string.Equals(x.RoutePattern.RawText, route, StringComparison.Ordinal) && x.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) == true);

        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Response =
            {
                Body = new MemoryStream()
            },
            Request =
            {
                Method = method,
                Path = route.Replace("{id}", routeValues?["id"]?.ToString() ?? "")
            }
        };

        if (!string.IsNullOrWhiteSpace(queryString))
        {
            context.Request.QueryString = new(queryString);
        }

        if (routeValues is not null)
        {
            foreach (var (key, value) in routeValues)
            {
                context.Request.RouteValues[key] = value;
            }
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return new(context.Response.StatusCode, await reader.ReadToEndAsync());
    }
}
