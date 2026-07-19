using System.Text;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Relations;
using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Api;

public sealed class RelationQueryApiEndpointTests
{
    static readonly QualifiedShapeId OrderShape = new(
        new GraphId("tests/transportation"),
        new ShapeId("Order"));

    [Fact]
    public async Task MapRelationQueryApiDefinition_BindsRequestToFreshCanonicalEvaluationAndExplicitOutcomeMapper()
    {
        using var cancellation = new CancellationTokenSource();
        var operationContext = OperationContext.Create(cancellationToken: cancellation.Token);
        var evaluator = new RecordingEvaluator();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(operationContext);
        builder.Services.AddSingleton<IRelationQueryEvaluator>(evaluator);
        var app = builder.Build();
        var api = CreateOrderSummaryApi();
        var queryEndpoint = api.Endpoints.Single(static endpoint => endpoint.Name == "QueryOrderSummaries");
        RelationQueryApiRequestContext? observedRequestContext = null;
        RelationQueryApiResultContext? observedResultContext = null;
        RelationQueryEvaluationOutcome? observedOutcome = null;

        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions()
            .Bind(queryEndpoint.RelationQuery(
                (context, request) =>
                {
                    observedRequestContext = context;
                    var query = Assert.IsType<QueryOrderSummariesRequest>(request);
                    return CreateOrderSummaryEvaluation(context.EvaluationId, query.Status);
                },
                (context, outcome) =>
                {
                    observedResultContext = context;
                    observedOutcome = outcome;
                    var query = Assert.IsType<QueryOrderSummariesRequest>(context.Request);
                    return Results.Ok(new QueryOrderSummariesResponse(
                        EvaluationId: outcome.Evaluation.Evaluation.Value,
                        RequestedStatus: query.Status,
                        CompilationSucceeded: outcome.Compilation.IsSuccessful));
                })));

        const string traceIdentifier = "request/42";
        var response = await InvokeAsync(
            app,
            route: "/order_summaries",
            method: "GET",
            traceIdentifier: traceIdentifier,
            queryString: "?status=Tendered",
            requestAborted: cancellation.Token);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        var json = ReadJson(response.Body);
        var expectedEvaluationId = RelationQueryApiEndpointOptions.CreateConventionalEvaluationId(
            observedRequestContext!.HttpContext,
            queryEndpoint.Operation);
        Assert.Equal(
            "aspnet/request/request%2F42/operation/Transportation.QueryOrderSummaries",
            expectedEvaluationId.Value);
        Assert.Equal(expectedEvaluationId.Value, json.GetProperty(nameof(QueryOrderSummariesResponse.EvaluationId)).GetString());
        Assert.Equal("Tendered", json.GetProperty(nameof(QueryOrderSummariesResponse.RequestedStatus)).GetString());
        Assert.False(json.GetProperty(nameof(QueryOrderSummariesResponse.CompilationSucceeded)).GetBoolean());
        Assert.Equal(expectedEvaluationId, evaluator.Evaluation!.Evaluation);
        Assert.Equal(cancellation.Token, evaluator.CancellationToken);
        Assert.Same(evaluator.Evaluation, observedResultContext!.Evaluation);
        Assert.Same(evaluator.Outcome, observedOutcome);
        Assert.Equal(operationContext, observedRequestContext.OperationContext);
    }

    [Fact]
    public async Task OperationNameBinding_UsesConfiguredEvaluationIdentityConvention()
    {
        var evaluator = new RecordingEvaluator();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(OperationContext.Create());
        var app = builder.Build();
        var api = Cohesive.Api.Api.Define("Relations")
            .Action("FixedOrderSummaries")
                .Route("GET", "/fixed_order_summaries")
                .Returns<QueryOrderSummariesResponse>()
                .Done()
            .Build();
        var expectedEvaluation = new RelationQueryEvaluationId("host/custom-evaluation");

        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions
        {
            EvaluatorResolver = _ => evaluator,
            EvaluationIdSelector = (_, _) => expectedEvaluation
        }.Bind(RelationQueryApiOperationBinding.Evaluate(
            operationName: "FixedOrderSummaries",
            static (context, _) => ValueTask.FromResult(
                CreateOrderSummaryEvaluation(context.EvaluationId, status: null)),
            static (_, outcome) => ValueTask.FromResult<IResult>(Results.Ok(
                new QueryOrderSummariesResponse(
                    EvaluationId: outcome.Evaluation.Evaluation.Value,
                    RequestedStatus: null,
                    CompilationSucceeded: outcome.Compilation.IsSuccessful))))));

        var response = await InvokeAsync(
            app,
            route: "/fixed_order_summaries",
            method: "GET",
            traceIdentifier: "ignored-by-custom-policy");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(
            expectedEvaluation.Value,
            ReadJson(response.Body).GetProperty(nameof(QueryOrderSummariesResponse.EvaluationId)).GetString());
        Assert.Equal(expectedEvaluation, evaluator.Evaluation!.Evaluation);
    }

    [Fact]
    public async Task EvaluationFactory_RejectsIdentityThatDoesNotBelongToRequest()
    {
        var evaluator = new RecordingEvaluator();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IRelationQueryEvaluator>(evaluator);
        var app = builder.Build();
        var api = CreateOrderSummaryApi();
        var queryEndpoint = Assert.Single(api.Endpoints);

        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions()
            .Bind(queryEndpoint.RelationQuery(
                static (_, _) => CreateOrderSummaryEvaluation(
                    new RelationQueryEvaluationId("foreign/evaluation"),
                    status: null),
                static (_, _) => Results.Ok())));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(
            app,
            route: "/order_summaries",
            method: "GET",
            traceIdentifier: "request/foreign"));

        Assert.Contains("instead of the request-scoped identity", exception.Message, StringComparison.Ordinal);
        Assert.Null(evaluator.Evaluation);
    }

    [Fact]
    public async Task HostAcceptsSemanticallyEquivalentReconstructedEvaluatorOutcome()
    {
        var evaluator = new ReconstructingEvaluator(changeParameter: false);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IRelationQueryEvaluator>(evaluator);
        var app = builder.Build();
        var api = CreateOrderSummaryApi();
        var endpoint = Assert.Single(api.Endpoints);
        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions()
            .Bind(endpoint.RelationQuery(
                static (context, request) => CreateOrderSummaryEvaluation(
                    context.EvaluationId,
                    Assert.IsType<QueryOrderSummariesRequest>(request).Status),
                static (_, _) => Results.Ok())));

        var response = await InvokeAsync(
            app,
            "/order_summaries",
            "GET",
            "request/reconstructed",
            queryString: "?status=Tendered");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.NotSame(evaluator.Request, evaluator.Outcome!.Evaluation);
        Assert.True(evaluator.Request!.HasSameSemantics(evaluator.Outcome.Evaluation));
    }

    [Fact]
    public async Task HostRejectsEvaluatorOutcomeForSemanticallyDifferentEvaluation()
    {
        var evaluator = new ReconstructingEvaluator(changeParameter: true);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IRelationQueryEvaluator>(evaluator);
        var app = builder.Build();
        var api = CreateOrderSummaryApi();
        var endpoint = Assert.Single(api.Endpoints);
        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions()
            .Bind(endpoint.RelationQuery(
                static (context, request) => CreateOrderSummaryEvaluation(
                    context.EvaluationId,
                    Assert.IsType<QueryOrderSummariesRequest>(request).Status),
                static (_, _) => Results.Ok())));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(
            app,
            "/order_summaries",
            "GET",
            "request/foreign-outcome",
            queryString: "?status=Tendered"));

        Assert.Contains("different evaluation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledRequestStopsBeforeEvaluationFactoryAndEvaluator()
    {
        var evaluator = new RecordingEvaluator();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IRelationQueryEvaluator>(evaluator);
        var app = builder.Build();
        var api = CreateOrderSummaryApi();
        var endpoint = Assert.Single(api.Endpoints);
        var factoryCalled = false;
        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions()
            .Bind(endpoint.RelationQuery(
                (context, _) =>
                {
                    factoryCalled = true;
                    return CreateOrderSummaryEvaluation(context.EvaluationId, status: null);
                },
                static (_, _) => Results.Ok())));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(
            app,
            "/order_summaries",
            "GET",
            "request/canceled",
            requestAborted: cancellation.Token));

        Assert.False(factoryCalled);
        Assert.Null(evaluator.Evaluation);
    }

    [Fact]
    public async Task RequestCancellationIsLinkedIntoEvaluationFactoryAndStopsBeforeEvaluator()
    {
        using var operationCancellation = new CancellationTokenSource();
        using var requestCancellation = new CancellationTokenSource();
        var evaluator = new RecordingEvaluator();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create(
            cancellationToken: operationCancellation.Token));
        builder.Services.AddSingleton<IRelationQueryEvaluator>(evaluator);
        var app = builder.Build();
        var api = CreateOrderSummaryApi();
        var endpoint = Assert.Single(api.Endpoints);
        CancellationToken factoryCancellation = default;
        var resultMapperCalled = false;
        app.MapRelationQueryApiDefinition(
            api,
            new RelationQueryApiEndpointOptions()
                .Bind(endpoint.RelationQuery(
                    (context, _) =>
                    {
                        factoryCancellation = context.OperationContext.CancellationToken;
                        requestCancellation.Cancel();
                        Assert.True(factoryCancellation.IsCancellationRequested);
                        return CreateOrderSummaryEvaluation(context.EvaluationId, status: null);
                    },
                    (_, _) =>
                    {
                        resultMapperCalled = true;
                        return Results.Ok();
                    })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(
            app,
            "/order_summaries",
            "GET",
            "request/linked-cancellation",
            requestAborted: requestCancellation.Token));

        Assert.True(factoryCancellation.CanBeCanceled);
        Assert.NotEqual(operationCancellation.Token, factoryCancellation);
        Assert.NotEqual(requestCancellation.Token, factoryCancellation);
        Assert.False(operationCancellation.IsCancellationRequested);
        Assert.Null(evaluator.Evaluation);
        Assert.False(resultMapperCalled);
    }

    [Fact]
    public async Task DistinctOperationAndRequestTokensShareOneEffectiveTokenAcrossHostPhases()
    {
        using var operationCancellation = new CancellationTokenSource();
        using var requestCancellation = new CancellationTokenSource();
        var evaluator = new RecordingEvaluator();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create(
            cancellationToken: operationCancellation.Token));
        builder.Services.AddSingleton<IRelationQueryEvaluator>(evaluator);
        var app = builder.Build();
        var api = CreateOrderSummaryApi();
        var endpoint = Assert.Single(api.Endpoints);
        CancellationToken factoryCancellation = default;
        CancellationToken resultCancellation = default;
        app.MapRelationQueryApiDefinition(api, new RelationQueryApiEndpointOptions()
            .Bind(endpoint.RelationQuery(
                (context, _) =>
                {
                    factoryCancellation = context.OperationContext.CancellationToken;
                    return CreateOrderSummaryEvaluation(context.EvaluationId, status: null);
                },
                (context, _) =>
                {
                    resultCancellation = context.OperationContext.CancellationToken;
                    return Results.Ok();
                })));

        var response = await InvokeAsync(
            app,
            "/order_summaries",
            "GET",
            "request/effective-cancellation",
            requestAborted: requestCancellation.Token);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.NotEqual(operationCancellation.Token, factoryCancellation);
        Assert.NotEqual(requestCancellation.Token, factoryCancellation);
        Assert.Equal(factoryCancellation, evaluator.CancellationToken);
        Assert.Equal(factoryCancellation, resultCancellation);
    }

    static ApiDefinition CreateOrderSummaryApi() => Cohesive.Api.Api.Define("Transportation")
        .Action("QueryOrderSummaries")
            .Route("GET", "/order_summaries")
            .Query<QueryOrderSummariesRequest>()
            .Returns<QueryOrderSummariesResponse>()
            .Done()
        .Build();

    static RelationQueryEvaluation CreateOrderSummaryEvaluation(
        RelationQueryEvaluationId evaluationId,
        string? status)
    {
        var author = RelationQuery.Structural();
        var statusParameter = author.Parameter(
            new ScalarTypeRef(ScalarTypeKind.String),
            presence: FieldPresence.Optional,
            id: new QueryParameterId("status"));
        var orders = author.Source(
            OrderShape,
            nodeId: new QueryNodeId("orders"),
            bindingId: new ValueBindingId("order"));
        var filtered = author.Filter(
            orders.Node,
            Expr.Eq(orders.Binding.Field("Status"), statusParameter.Expression),
            nodeId: new QueryNodeId("orders-by-status"));
        var rows = author.Rows(filtered, id: new QueryResultId("rows"));
        var query = author.BuildQuery(
            new QueryId("order-summaries"),
            new QueryName("OrderSummaries"),
            [rows]);
        var evaluation = query.CreateDocument()
            .Evaluate(evaluationId)
            .Select(rows.Id);
        return status is null
            ? evaluation.Omit(statusParameter.Id).Build()
            : evaluation.Set(statusParameter.Id, ObservationValue.FromString(status)).Build();
    }

    static JsonElement ReadJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    sealed record InvocationResult(int StatusCode, string Body);

    sealed record QueryOrderSummariesRequest(string? Status);

    sealed record QueryOrderSummariesResponse(
        string EvaluationId,
        string? RequestedStatus,
        bool CompilationSucceeded);

    sealed class RecordingEvaluator : IRelationQueryEvaluator
    {
        public RelationQueryEvaluation? Evaluation { get; private set; }

        public RelationQueryEvaluationOutcome? Outcome { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
            RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default)
        {
            Evaluation = evaluation;
            CancellationToken = cancellationToken;
            var compilation = RelationQueryStaticCompiler.Compile(evaluation.Compilation);
            if (compilation.IsSuccessful)
            {
                throw new InvalidOperationException(
                    "The API boundary fixture intentionally omits source shape documents so it can retain a " +
                    "structured compilation-failure outcome without constructing physical test infrastructure.");
            }

            Outcome = new(evaluation, compilation);
            return ValueTask.FromResult(Outcome);
        }
    }

    sealed class ReconstructingEvaluator(bool changeParameter) : IRelationQueryEvaluator
    {
        public RelationQueryEvaluation? Request { get; private set; }

        public RelationQueryEvaluationOutcome? Outcome { get; private set; }

        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
            RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = evaluation;
            var reconstructed = changeParameter
                ? CreateOrderSummaryEvaluation(evaluation.Evaluation, "ChangedByEvaluator")
                : Cohesive.Relations.Serialization.RelationQueryEvaluationJsonSerializer.Deserialize(
                    Cohesive.Relations.Serialization.RelationQueryEvaluationJsonSerializer.Serialize(evaluation));
            var compilation = RelationQueryStaticCompiler.Compile(reconstructed.Compilation);
            Outcome = new(reconstructed, compilation);
            return ValueTask.FromResult(Outcome);
        }
    }

    static async Task<InvocationResult> InvokeAsync(
        WebApplication app,
        string route,
        string method,
        string traceIdentifier,
        object? body = null,
        string? queryString = null,
        CancellationToken requestAborted = default)
    {
        var endpoint = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x =>
                string.Equals(x.RoutePattern.RawText, route, StringComparison.Ordinal)
                && x.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(
                    method,
                    StringComparer.OrdinalIgnoreCase) == true);

        var context = new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier,
            RequestServices = app.Services,
            RequestAborted = requestAborted,
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
