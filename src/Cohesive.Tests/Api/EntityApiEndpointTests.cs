using System.Text;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Relations.Queries;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Api;

public sealed class EntityApiEndpointTests
{
    const string TenantPartitionContextKey = "TestTenantPartition";

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

        var outboxMessage = Assert.Single(repository.OutboxMessages);
        Assert.Equal("api-transitions", outboxMessage.StreamName);
        Assert.Equal("note-1", outboxMessage.SubjectId);
    }

    [Fact]
    public async Task MapEntityApiDefinition_UsesPartitionKeyPolicyForPartitionedPointReads()
    {
        var entity = NoteEntity.Instance;
        var partitionKeyPolicy = new EntityPartitionKeyPolicy(
            description: "tenant field and request tenant item",
            writePartitionKeyResolver: static (_, observation) => observation.GetField(nameof(NoteState.Tenant)).GetRequiredString(),
            pointReadPartitionKeyResolver: static (context, _) =>
                context.TryGetItem<string>(TenantPartitionContextKey, out var tenant) ? tenant : null
            );
        var repository = new InMemoryEntityOutboxRepository(entity.Definition, partitionKeyPolicy);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await repository.Upsert(OperationContext.Create(), new(entity.Definition.CreateState(
            entityId: "note-1",
            new NoteState("note-1", "tenant-a", "alpha tenant", now)).Observation));
        await repository.Upsert(OperationContext.Create(), new(entity.Definition.CreateState(
            entityId: "note-1",
            new NoteState("note-1", "tenant-b", "beta tenant", now)).Observation));

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
            writePartitionKeyResolver: static (_, observation) => observation.GetField(nameof(NoteState.Tenant)).GetRequiredString(),
            pointReadPartitionKeyResolver: static (context, _) =>
                context.TryGetItem<string>(TenantPartitionContextKey, out var tenant) ? tenant : null
            );
        var provider = new DelegatingEntityPartitionKeyPolicyProvider(_ => partitionKeyPolicy);
        var repository = new InMemoryEntityOutboxRepository(entity.Definition, partitionKeyPolicy);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await repository.Upsert(OperationContext.Create(), new(entity.Definition.CreateState(
            entityId: "note-1",
            new NoteState("note-1", "tenant-a", "alpha tenant", now)).Observation));
        await repository.Upsert(OperationContext.Create(), new(entity.Definition.CreateState(
            entityId: "note-1",
            new NoteState("note-1", "tenant-b", "beta tenant", now)).Observation));

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
        Func<IServiceProvider, EntityDefinition, EntityPartitionKeyPolicy?>? partitionKeyPolicyResolver = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(operationContext ?? OperationContext.Create());
        builder.Services.RegisterEntityRepository(entity, (_, _) => repository);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapEntityApiDefinition(CreateApi(), new EntityApiEndpointOptions
        {
            Entity = entity.Definition,
            PartitionKeyPolicy = partitionKeyPolicy,
            PartitionKeyPolicyResolver = partitionKeyPolicyResolver
        }
            .Bind(EntityApiOperationBinding.Get("Get", static (_, snapshot) => Results.Ok(ToResource(snapshot))))
            .Bind(EntityApiOperationBinding.Query(
                "Query",
                static (_, request) =>
                {
                    var query = request as QueryNotesRequest;
                    var predicate = string.IsNullOrWhiteSpace(query?.Prefix)
                        ? new FieldPredicate(FieldPath.FromField(nameof(NoteState.Id)), new ExistsValuePredicate())
                        : new FieldPredicate(FieldPath.FromField(nameof(NoteState.Text)), new PrefixValuePredicate(query.Prefix));
                    return new EntityQuery(new(predicate));
                },
                static (_, snapshots) => Results.Ok(new QueryNotesResponse([.. snapshots.Select(ToResource)]))))
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
                static (_, snapshot) => Results.Ok(ToResource(snapshot))))
            .Bind(EntityApiOperationBinding.Transition(
                "Revise",
                nameof(NoteEntity.Revise),
                static (context, request) =>
                {
                    var revise = (ReviseNoteRequest)request!;
                    return new NoteEntity.ReviseInput(revise.Text, context.OperationContext.UtcNow);
                },
                static (_, snapshot) => Results.Ok(ToResource(snapshot)),
                createOutboxMessages: static context =>
                [
                    new(
                        MessageId: $"msg-{context.EntityId}-{context.NewState.Version}",
                        StreamName: "api-transitions",
                        SubjectType: context.Entity.Shape.Id.Value,
                        SubjectId: context.EntityId,
                        PartitionKey: "tenant-a",
                        Entity: context.NewState.Observation,
                        SubjectVersion: context.NewState.Version,
                        OccurredAtUtc: context.OperationContext.UtcNow)
                ]))
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

        return app;
    }

    static ApiDefinition CreateApi() => Cohesive.Api.Api.Define()
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
                .Transition(new(nameof(NoteEntity.Revise)))
                .Done()
            .Command("Inspect")
                .Route("POST", "/notes/{id}/inspect")
                .RouteParameter<string>("id")
                .Body<InspectNoteRequest>()
                .Returns<NoteInspectionResponse>()
                .Done()
            .Build();

    static NoteResource ToResource(EntitySnapshot snapshot) => new(
        Id: snapshot.Entity.GetField(nameof(NoteState.Id)).GetString() ?? throw new InvalidOperationException("Note id is required."),
        Tenant: snapshot.Entity.GetField(nameof(NoteState.Tenant)).GetString() ?? throw new InvalidOperationException("Note tenant is required."),
        Text: snapshot.Entity.GetField(nameof(NoteState.Text)).GetString() ?? throw new InvalidOperationException("Note text is required."),
        UpdatedAtUtc: snapshot.Entity.GetField(nameof(NoteState.UpdatedAtUtc)).GetDateTimeOffset());

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

    sealed class NoteEntity : Entity<NoteEntity>
    {
        public sealed record ReviseInput(string Text, DateTimeOffset UpdatedAtUtc);

        public NoteEntity()
        {
            Id = WriteOnceField<string>(nameof(Id));
            Tenant = WriteOnceField<string>(nameof(Tenant));
            Text = MutableField<string>(nameof(Text));
            UpdatedAtUtc = MutableField<DateTimeOffset>(nameof(UpdatedAtUtc));

            Revise = Transition<ReviseInput>(nameof(Revise), t => t
                .Set(x => x.Text, (_, input) => input.Text)
                .Set(x => x.UpdatedAtUtc, (_, input) => input.UpdatedAtUtc));
        }

        public Field<string> Id { get; }

        public Field<string> Tenant { get; }

        public Field<string> Text { get; }

        public Field<DateTimeOffset> UpdatedAtUtc { get; }

        public Transition<NoteEntity, ReviseInput> Revise { get; }
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
            context.Request.QueryString = new(queryString);

        if (routeValues is not null)
        {
            foreach (var (key, value) in routeValues)
                context.Request.RouteValues[key] = value;
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
