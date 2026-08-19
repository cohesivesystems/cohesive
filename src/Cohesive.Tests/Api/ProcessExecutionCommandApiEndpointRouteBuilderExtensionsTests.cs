using System.Text;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Tests.ExecutionKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Tests.Api;

public sealed class ProcessExecutionCommandApiEndpointRouteBuilderExtensionsTests
{
    const string PauseRoute = "/execution-control/processes/pause";
    const string PausePolicy = "cohesive.execution.pause";

    [Fact]
    public async Task MapProcessExecutionCommandApi_DispatchesCanonicalBodyThroughSharedSdkBoundary()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var command = fixture.Pause(state);
        var catalog = ExecutionControlApiCatalog.Create();
        var status = ExecutionStatusProjector.Project(state, ExecutionRuntimeStatusDetails.Unknown);
        var body = new ExecutionControlResult(
            ProcessControlDecisionDisposition.Inspected,
            status);
        var dispatcher = new RecordingDispatcher(catalog, body);
        var operationContext = OperationContext.Create();
        var invocation = new ExecutionApiInvocationContext(
            authorization: command.Context.Authorization,
            provenance: command.Context.Provenance,
            issuedAtUtc: command.Context.IssuedAtUtc,
            observedAtUtc: command.Context.IssuedAtUtc,
            grantedRequirements: [
                ExecutionControlApiWireNames.AuthorizationRequirement(ExecutionControlWireNames.Pause)
            ]);
        OperationContext? resolvedContext = null;
        HttpContext? resolvedHttpContext = null;
        ApiOperation? resolvedOperation = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(operationContext);
        builder.Services.AddSingleton<IExecutionControlApiDispatcher>(dispatcher);
        await using var app = builder.Build();

        app.MapProcessExecutionCommandApi<PauseProcessCommand>(
            catalog,
            catalog.Pause,
            PauseRoute,
            (context, httpContext, operation) =>
            {
                resolvedContext = context;
                resolvedHttpContext = httpContext;
                resolvedOperation = operation;
                return ValueTask.FromResult(invocation);
            },
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);
        var response = await InvokeAsync(app, endpoint, command);

        Assert.True(
            response.StatusCode == StatusCodes.Status200OK,
            Encoding.UTF8.GetString(response.Body));
        Assert.Equal("application/json", response.ContentType);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<ExecutionControlResult>(response.Body, jsonOptions);
        Assert.NotNull(restored);
        Assert.Equal(JsonSerializer.Serialize(body, jsonOptions), JsonSerializer.Serialize(restored, jsonOptions));
        Assert.Same(operationContext, resolvedContext);
        Assert.Same(response.HttpContext, resolvedHttpContext);
        Assert.NotNull(resolvedOperation);
        Assert.Equal(HttpMethods.Post, resolvedOperation.Http?.Method);
        Assert.Equal(typeof(PauseProcessCommand), resolvedOperation.Http?.Body?.BodyType);
        Assert.Same(operationContext, dispatcher.Context);
        Assert.Same(catalog.Pause, dispatcher.Endpoint);
        Assert.Equal(command, dispatcher.Request);
        Assert.Equal(invocation, dispatcher.Invocation);
        Assert.Equal(
            PausePolicy,
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        Assert.Null(catalog.Pause.Operation.Http);
    }

    [Fact]
    public void MapProcessExecutionCommandApi_RejectsForeignQueryAndRequestTypeMismatch()
    {
        var catalog = ExecutionControlApiCatalog.Create();
        var foreign = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        var foreignError = Assert.Throws<InvalidOperationException>(() =>
            app.MapProcessExecutionCommandApi<PauseProcessCommand>(
                catalog,
                foreign.Pause,
                PauseRoute,
                static (_, _, _) => ValueTask.FromResult<ExecutionApiInvocationContext>(null!),
                ResolvePolicy));
        var queryError = Assert.Throws<InvalidOperationException>(() =>
            app.MapProcessExecutionCommandApi<InspectProcessCommand>(
                catalog,
                catalog.Inspect,
                PauseRoute,
                static (_, _, _) => ValueTask.FromResult<ExecutionApiInvocationContext>(null!),
                ResolvePolicy));
        var requestError = Assert.Throws<InvalidOperationException>(() =>
            app.MapProcessExecutionCommandApi<ContinueProcessCommand>(
                catalog,
                catalog.Pause,
                PauseRoute,
                static (_, _, _) => ValueTask.FromResult<ExecutionApiInvocationContext>(null!),
                ResolvePolicy));

        Assert.Contains("not owned", foreignError.Message, StringComparison.Ordinal);
        Assert.Contains("does not declare command", queryError.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ContinueProcessCommand), requestError.Message, StringComparison.Ordinal);
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints));
    }

    static RouteEndpoint GetRouteEndpoint(WebApplication app) =>
        Assert.Single(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>());

    static async Task<CapturedResponse> InvokeAsync<TRequest>(
        WebApplication app,
        RouteEndpoint endpoint,
        TRequest request)
        where TRequest : class
    {
        await using var scope = app.Services.CreateAsyncScope();
        var body = JsonSerializer.SerializeToUtf8Bytes(
            request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(JsonSerializer.Deserialize<TRequest>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);

        await endpoint.RequestDelegate!(context);
        return new(
            context.Response.StatusCode,
            context.Response.ContentType,
            ((MemoryStream)context.Response.Body).ToArray(),
            context);
    }

    static string ResolvePolicy(ApiOperation operation, ApiAuthorizationRequirement requirement)
    {
        Assert.Equal(ExecutionControlWireNames.Pause, operation.Name);
        Assert.Equal(
            ExecutionControlApiWireNames.AuthorizationRequirement(ExecutionControlWireNames.Pause),
            requirement.Id);
        return PausePolicy;
    }

    sealed record CapturedResponse(
        int StatusCode,
        string? ContentType,
        byte[] Body,
        HttpContext HttpContext);

    sealed class RecordingDispatcher(
        ExecutionControlApiCatalog catalog,
        ExecutionControlResult body) : IExecutionControlApiDispatcher
    {
        public ExecutionControlApiCatalog Catalog { get; } = catalog;

        public OperationContext? Context { get; private set; }

        public ApiEndpoint? Endpoint { get; private set; }

        public object? Request { get; private set; }

        public ExecutionApiInvocationContext? Invocation { get; private set; }

        public ValueTask<ExecutionApiDispatchResult> DispatchAsync(
            OperationContext context,
            ApiEndpoint endpoint,
            object request,
            ExecutionApiInvocationContext invocation)
        {
            Context = context;
            Endpoint = endpoint;
            Request = request;
            Invocation = invocation;
            return ValueTask.FromResult(new ExecutionApiDispatchResult(
                endpoint,
                Catalog.GetResult(endpoint, ApiResultKind.Success),
                body));
        }
    }
}
