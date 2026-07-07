using System.Text;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Relations;
using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Relations.Model;
using Cohesive.Relations.Queries;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Api;

public sealed class RelationQueryApiEndpointTests
{
    static readonly QuerySource OrderSource = QuerySource.For<OrderState>("orders");
    static readonly QuerySource StopSource = QuerySource.For<StopState>("stops");

    [Fact]
    public async Task MapRelationQueryApiDefinition_BindsRequestToExecutableQueryBackedByEntityRepositoryAdapters()
    {
        var orderEntity = OrderEntity.Instance;
        var stopEntity = StopEntity.Instance;
        var orderRepository = new InMemoryEntityOutboxRepository(
            orderEntity.Definition,
            partitionKeyFieldName: nameof(OrderState.Tenant));
        var stopRepository = new InMemoryEntityOutboxRepository(
            stopEntity.Definition,
            partitionKeyFieldName: nameof(StopState.Tenant));

        await Upsert(orderEntity, orderRepository, entityId: "order-1", state: new OrderState(
            Id: "order-1",
            Tenant: "tenant-a",
            Status: "Tendered",
            OriginCity: "Chicago"));
        await Upsert(orderEntity, orderRepository, entityId: "order-2", state: new OrderState(
            Id: "order-2",
            Tenant: "tenant-a",
            Status: "Booked",
            OriginCity: "Atlanta"));
        await Upsert(stopEntity, stopRepository, entityId: "stop-1", state: new StopState(
            Id: "stop-1",
            Tenant: "tenant-a",
            OrderId: "order-1",
            City: "Chicago"));
        await Upsert(stopEntity, stopRepository, entityId: "stop-2", state: new StopState(
            Id: "stop-2",
            Tenant: "tenant-a",
            OrderId: "order-1",
            City: "Detroit"));
        await Upsert(stopEntity, stopRepository, entityId: "stop-3", state: new StopState(
            Id: "stop-3",
            Tenant: "tenant-a",
            OrderId: "order-2",
            City: "Atlanta"));

        var registry = new DispatchingReadRepositoryRegistry()
            .Register(OrderSource, new ObservationQueryReadRepositoryAdapter((IEntityQueryRepository)orderRepository))
            .Register(StopSource, new ObservationQueryReadRepositoryAdapter((IEntityQueryRepository)stopRepository));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IReadRepositoryRegistry>(registry);
        var app = builder.Build();
        var api = CreateOrderSummaryApi();
        var queryEndpoint = api.Endpoints.Single(static endpoint => endpoint.Name == "QueryOrderSummaries");

        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions()
            .Bind(queryEndpoint.RelationQuery<IReadOnlyList<OrderSummary>>(
                static (_, request) => CreateOrderSummaryQuery((QueryOrderSummariesRequest?)request),
                static (_, summaries) => Results.Ok(new QueryOrderSummariesResponse(Items: [.. summaries])))));

        var response = await InvokeAsync(
            app,
            route: "/order_summaries",
            method: "GET",
            queryString: "?status=Tendered");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        var json = ReadJson(response.Body);
        var item = Assert.Single(json.GetProperty(nameof(QueryOrderSummariesResponse.Items)).EnumerateArray());
        Assert.Equal("order-1", item.GetProperty(nameof(OrderSummary.Id)).GetString());
        Assert.Equal("Tendered", item.GetProperty(nameof(OrderSummary.Status)).GetString());
        Assert.Equal("Chicago", item.GetProperty(nameof(OrderSummary.OriginCity)).GetString());
        Assert.Equal(["Chicago", "Detroit"], item
            .GetProperty(nameof(OrderSummary.StopCities))
            .EnumerateArray()
            .Select(static city => city.GetString()!)
            .ToArray());
    }

    [Fact]
    public async Task MapRelationQueryApiDefinition_CanBindFixedExecutableQueryByOperationName()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IReadRepositoryRegistry>(new DispatchingReadRepositoryRegistry());
        var app = builder.Build();
        var query = new ExecutableQuery<IReadOnlyList<OrderSummary>>((_, _) => Task.FromResult<IReadOnlyList<OrderSummary>>(
        [
            new(
                Id: "order-1",
                Status: "Tendered",
                OriginCity: "Chicago",
                StopCities: ["Chicago", "Detroit"])
        ]));
        var api = Cohesive.Api.Api.Define("Relations")
            .Action("FixedOrderSummaries")
                .Route("GET", "/fixed_order_summaries")
                .Returns<QueryOrderSummariesResponse>()
                .Done()
            .Build();

        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions()
            .Bind(RelationQueryApiOperationBinding.Query(
                operationName: "FixedOrderSummaries",
                query,
                static (_, result) => Results.Ok(new QueryOrderSummariesResponse(
                    Items: [.. (IReadOnlyList<OrderSummary>)result!])))));

        var response = await InvokeAsync(
            app,
            route: "/fixed_order_summaries",
            method: "GET");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        var json = ReadJson(response.Body);
        var item = Assert.Single(json.GetProperty(nameof(QueryOrderSummariesResponse.Items)).EnumerateArray());
        Assert.Equal("order-1", item.GetProperty(nameof(OrderSummary.Id)).GetString());
    }

    static ApiDefinition CreateOrderSummaryApi() => Cohesive.Api.Api.Define("Transportation")
        .Action("QueryOrderSummaries")
            .Route("GET", "/order_summaries")
            .Query<QueryOrderSummariesRequest>()
            .Returns<QueryOrderSummariesResponse>()
            .Done()
        .Build();

    static ExecutableQuery<IReadOnlyList<OrderSummary>> CreateOrderSummaryQuery(QueryOrderSummariesRequest? request)
    {
        var fieldPredicate = string.IsNullOrWhiteSpace(request?.Status)
            ? new FieldPredicate(
                Field: FieldPath.FromField(nameof(OrderState.Id)),
                Predicate: new ExistsValuePredicate())
            : new FieldPredicate(
                Field: FieldPath.FromField(nameof(OrderState.Status)),
                Predicate: new ExactValuePredicate(request.Status));
        EntityPredicate predicate = new(fieldPredicate);

        return Query
            .From(OrderSource, predicate)
            .JoinMany<OrderState, StopState, string>(
                alias: "stops",
                source: StopSource,
                rootKey: static order => order.Id,
                foreignKey: static stop => stop.OrderId)
            .Select(static join =>
            {
                var root = join.Root;
                return new OrderSummary(
                    Id: root.GetField(nameof(OrderState.Id)).GetString() ?? throw new InvalidOperationException("Order id is required."),
                    Status: root.GetField(nameof(OrderState.Status)).GetString() ?? throw new InvalidOperationException("Order status is required."),
                    OriginCity: root.GetField(nameof(OrderState.OriginCity)).GetString() ?? throw new InvalidOperationException("Order origin city is required."),
                    StopCities: [.. join
                        .Many("stops")
                        .Select(static stop => stop.GetField(nameof(StopState.City)).GetString() ?? throw new InvalidOperationException("Stop city is required."))]);
            });
    }

    static async Task Upsert<TState>(
        Entity entity,
        InMemoryEntityOutboxRepository repository,
        string entityId,
        TState state
        ) where TState : notnull
    {
        var observation = entity.Definition.CreateState(
            entityId: entityId,
            state)
            .Observation;
        await repository.Upsert(OperationContext.Create(), new(Entity: observation));
    }

    static JsonElement ReadJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    sealed record InvocationResult(int StatusCode, string Body);

    sealed record QueryOrderSummariesRequest(string? Status);

    sealed record QueryOrderSummariesResponse(OrderSummary[] Items);

    sealed record OrderSummary(string Id, string Status, string OriginCity, string[] StopCities);

    sealed record OrderState(string Id, string Tenant, string Status, string OriginCity);

    sealed record StopState(string Id, string Tenant, string OrderId, string City);

    sealed class OrderEntity : Entity<OrderEntity>
    {
        public OrderEntity()
        {
            Id = WriteOnceField<string>(nameof(Id));
            Tenant = WriteOnceField<string>(nameof(Tenant));
            Status = MutableField<string>(nameof(Status));
            OriginCity = MutableField<string>(nameof(OriginCity));
        }

        public Field<string> Id { get; }

        public Field<string> Tenant { get; }

        public Field<string> Status { get; }

        public Field<string> OriginCity { get; }
    }

    sealed class StopEntity : Entity<StopEntity>
    {
        public StopEntity()
        {
            Id = WriteOnceField<string>(nameof(Id));
            Tenant = WriteOnceField<string>(nameof(Tenant));
            OrderId = MutableField<string>(nameof(OrderId));
            City = MutableField<string>(nameof(City));
        }

        public Field<string> Id { get; }

        public Field<string> Tenant { get; }

        public Field<string> OrderId { get; }

        public Field<string> City { get; }
    }

    static async Task<InvocationResult> InvokeAsync(
        WebApplication app,
        string route,
        string method,
        object? body = null,
        string? queryString = null
        )
    {
        var endpoint = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x =>
                string.Equals(x.RoutePattern.RawText, route, StringComparison.Ordinal)
                && x.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) == true);

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
                Path = route
            }
        };

        if (!string.IsNullOrWhiteSpace(queryString))
            context.Request.QueryString = new(queryString);

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
        return new(StatusCode: context.Response.StatusCode, Body: await reader.ReadToEndAsync());
    }
}
