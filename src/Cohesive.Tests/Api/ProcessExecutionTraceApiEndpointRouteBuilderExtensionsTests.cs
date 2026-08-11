using System.Text;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Processes.Runtime;
using Cohesive.Tests.ExecutionKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Tests.Api;

public sealed class ProcessExecutionTraceApiEndpointRouteBuilderExtensionsTests
{
    const string TraceRoute = "/execution-control/processes/{processInstanceId}/traces";
    const string TracePolicy = "cohesive.execution.traces";

    [Fact]
    public async Task MapProcessExecutionTracesApi_UsesTrustedLogicalAddressAndReturnsCanonicalBytes()
    {
        var fixture = ProcessControlTestFixture.Create();
        var artifact = Artifact(fixture.State());
        var repository = new RecordingTraceRepository(_ => ProcessExecutionTraceReadResult.Available(artifact));
        var operationContext = OperationContext.Create();
        var authorityScope = new InteractionAuthorityScope("authority/trusted", "tenant/trusted");
        OperationContext? resolvedContext = null;
        HttpContext? resolvedHttpContext = null;
        ProcessInstanceId resolvedInstance = default;
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(operationContext);
        builder.Services.AddSingleton<IProcessExecutionTraceRepository>(repository);
        await using var app = builder.Build();

        app.MapProcessExecutionTracesApi(
            catalog.Traces,
            TraceRoute,
            (context, httpContext, processInstanceId) =>
            {
                resolvedContext = context;
                resolvedHttpContext = httpContext;
                resolvedInstance = processInstanceId;
                return authorityScope;
            },
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);
        var response = await InvokeAsync(app, endpoint, artifact.ProcessInstanceId.Value, "not-json");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal(ProcessExecutionTraceJsonSerializer.GetCanonicalBytes(artifact), response.Body);
        Assert.DoesNotContain("physical/", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        Assert.Same(operationContext, resolvedContext);
        Assert.Same(response.HttpContext, resolvedHttpContext);
        Assert.Equal(artifact.ProcessInstanceId, resolvedInstance);
        Assert.Same(operationContext, repository.OperationContext);
        Assert.Equal(authorityScope, repository.AuthorityScope);
        Assert.Equal(artifact.ProcessInstanceId, repository.ProcessInstanceId);
        Assert.Equal(0, repository.PhysicalReadCount);

        var operation = Assert.Single(endpoint.Metadata.GetOrderedMetadata<ApiOperation>());
        Assert.Equal(typeof(InspectProcessCommand), operation.RequestType);
        Assert.Equal(typeof(ProcessExecutionTraceArtifact), operation.ResponseType);
        Assert.Equal(HttpMethods.Get, operation.Http?.Method);
        Assert.Null(operation.Http?.Body);
        Assert.Equal(
            ProcessExecutionReadApiEndpointRouteBuilderExtensions.ProcessInstanceIdRouteParameter,
            Assert.Single(operation.Http!.Parameters).Name);
        Assert.Same(
            catalog.Traces.Operation.AuthorizationRequirements[0],
            Assert.Single(operation.AuthorizationRequirements));
        Assert.Equal(
            TracePolicy,
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        Assert.Equal(catalog.Traces.Operation.SemanticReferences, operation.SemanticReferences);
        Assert.Null(catalog.Traces.Operation.Http);
    }

    [Fact]
    public async Task MapProcessExecutionTracesApi_MapsEveryUnavailableStateToDeclaredOpaqueProblem()
    {
        var repository = new RecordingTraceRepository(processInstanceId => processInstanceId.Value switch
        {
            "process/active" => ProcessExecutionTraceReadResult.InProgress(),
            "process/terminal-without-artifact" => ProcessExecutionTraceReadResult.TerminalArtifactUnavailable(),
            _ => ProcessExecutionTraceReadResult.NotFound()
        });
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IProcessExecutionTraceRepository>(repository);
        await using var app = builder.Build();
        var resolverCalls = 0;
        app.MapProcessExecutionTracesApi(
            catalog.Traces,
            TraceRoute,
            (_, _, _) =>
            {
                resolverCalls++;
                return new("authority/trusted", "tenant/trusted");
            },
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);

        var missing = await InvokeAsync(app, endpoint, "process/not-present");
        var active = await InvokeAsync(app, endpoint, "process/active");
        var terminal = await InvokeAsync(app, endpoint, "process/terminal-without-artifact");
        var malformed = await InvokeAsync(app, endpoint, processInstanceId: null);

        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
        Assert.Equal(ExecutionApiProblemCodes.NotFound, ProblemCode(missing.Body));
        Assert.DoesNotContain("process/not-present", Encoding.UTF8.GetString(missing.Body), StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status409Conflict, active.StatusCode);
        Assert.Equal(ExecutionApiProblemCodes.TraceInProgress, ProblemCode(active.Body));
        Assert.Equal(StatusCodes.Status412PreconditionFailed, terminal.StatusCode);
        Assert.Equal(ExecutionApiProblemCodes.TraceArtifactUnavailable, ProblemCode(terminal.Body));
        Assert.Equal(StatusCodes.Status400BadRequest, malformed.StatusCode);
        Assert.Equal(ExecutionApiProblemCodes.InvalidRequest, ProblemCode(malformed.Body));
        Assert.Equal(3, resolverCalls);
        Assert.Equal(3, repository.LogicalReadCount);
        Assert.Equal(0, repository.PhysicalReadCount);
    }

    [Fact]
    public async Task MapProcessExecutionTracesApi_ConflictingArtifactAffinityFailsClosed()
    {
        var fixture = ProcessControlTestFixture.Create();
        var repository = new RecordingTraceRepository(_ =>
            ProcessExecutionTraceReadResult.Available(Artifact(fixture.State())));
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IProcessExecutionTraceRepository>(repository);
        await using var app = builder.Build();
        app.MapProcessExecutionTracesApi(
            catalog.Traces,
            TraceRoute,
            static (_, _, _) => new("authority/trusted", "tenant/trusted"),
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(app, endpoint, "process/another"));

        Assert.Contains("another logical Process instance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapProcessExecutionTracesApi_RejectsNoncanonicalContractRouteAndTrustedScope()
    {
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IProcessExecutionTraceRepository>(
            new RecordingTraceRepository(_ => ProcessExecutionTraceReadResult.NotFound()));
        await using var app = builder.Build();

        var contractError = Assert.Throws<InvalidOperationException>(() =>
            app.MapProcessExecutionTracesApi(
                catalog.Explain,
                TraceRoute,
                static (_, _, _) => new("authority/trusted"),
                ResolvePolicy));
        var routeError = Assert.Throws<ArgumentException>(() =>
            app.MapProcessExecutionTracesApi(
                catalog.Traces,
                "/execution-control/processes/traces",
                static (_, _, _) => new("authority/trusted"),
                ResolvePolicy));

        Assert.Contains("not the canonical execution-control retained-trace contract", contractError.Message, StringComparison.Ordinal);
        Assert.Contains("processInstanceId", routeError.Message, StringComparison.Ordinal);
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints));

        app.MapProcessExecutionTracesApi(
            catalog.Traces,
            TraceRoute,
            static (_, _, _) => null!,
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);
        var scopeError = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(app, endpoint, "process/target"));
        Assert.Contains("no trusted address", scopeError.Message, StringComparison.Ordinal);
    }

    static ProcessExecutionTraceArtifact Artifact(ProcessControlState state) => new(
        ProcessExecutionTraceArtifact.CurrentSchemaVersion,
        state.Definition,
        state.ProcessInstanceId,
        missingTracePrefixCount: 1,
        traces: []);

    static RouteEndpoint GetRouteEndpoint(WebApplication app) =>
        Assert.Single(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>());

    static async Task<CapturedResponse> InvokeAsync(
        WebApplication app,
        RouteEndpoint endpoint,
        string? processInstanceId,
        string? requestBody = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };
        if (processInstanceId is not null)
        {
            context.Request.RouteValues[
                ProcessExecutionReadApiEndpointRouteBuilderExtensions.ProcessInstanceIdRouteParameter] =
                processInstanceId;
        }
        if (requestBody is not null)
        {
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody));
            context.Request.ContentType = "application/json";
        }
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);

        await endpoint.RequestDelegate!(context);
        var body = ((MemoryStream)context.Response.Body).ToArray();
        return new(context.Response.StatusCode, context.Response.ContentType, body, context);
    }

    static string ProblemCode(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("code").GetString()!;
    }

    static string ResolvePolicy(ApiOperation operation, ApiAuthorizationRequirement requirement)
    {
        Assert.Equal(ProcessExecutionTraceWireNames.Read, operation.Name);
        Assert.Equal(
            ExecutionControlApiWireNames.AuthorizationRequirement(ProcessExecutionTraceWireNames.Read),
            requirement.Id);
        return TracePolicy;
    }

    sealed record CapturedResponse(
        int StatusCode,
        string? ContentType,
        byte[] Body,
        HttpContext HttpContext);

    sealed class RecordingTraceRepository(
        Func<ProcessInstanceId, ProcessExecutionTraceReadResult> read)
        : IProcessExecutionTraceRepository
    {
        public OperationContext? OperationContext { get; private set; }

        public InteractionAuthorityScope? AuthorityScope { get; private set; }

        public ProcessInstanceId? ProcessInstanceId { get; private set; }

        public int LogicalReadCount { get; private set; }

        public int PhysicalReadCount { get; private set; }

        public ValueTask<ProcessExecutionTraceReadResult> GetTracesAsync(
            OperationContext context,
            InteractionAuthorityScope authorityScope,
            ProcessInstanceId processInstanceId)
        {
            OperationContext = context;
            AuthorityScope = authorityScope;
            ProcessInstanceId = processInstanceId;
            LogicalReadCount++;
            return ValueTask.FromResult(read(processInstanceId));
        }

        public ValueTask<ProcessExecutionTraceReadResult> GetTracesAsync(
            OperationContext context,
            string processId)
        {
            PhysicalReadCount++;
            throw new InvalidOperationException("The HTTP trace binding must not use a physical Process key.");
        }
    }
}
