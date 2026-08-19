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

public sealed class ProcessExecutionInspectApiEndpointRouteBuilderExtensionsTests
{
    const string InspectRoute = "/execution-control/processes/{processInstanceId}";
    const string InspectPolicy = "cohesive.execution.inspect";

    [Fact]
    public async Task MapProcessExecutionInspectApi_UsesTrustedLogicalAddressAndReturnsCanonicalStatus()
    {
        var fixture = ProcessControlTestFixture.Create();
        var status = ExecutionStatusProjector.Project(
            fixture.State(),
            ExecutionRuntimeStatusDetails.Unknown);
        var repository = new RecordingExecutionRepository(_ => Record(status));
        var operationContext = OperationContext.Create();
        var authorityScope = new InteractionAuthorityScope("authority/trusted", "tenant/trusted");
        OperationContext? resolvedContext = null;
        HttpContext? resolvedHttpContext = null;
        ProcessInstanceId resolvedInstance = default;
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(operationContext);
        builder.Services.AddSingleton<IProcessExecutionRepository>(repository);
        await using var app = builder.Build();

        app.MapProcessExecutionInspectApi(
            catalog.Inspect,
            InspectRoute,
            (context, httpContext, processInstanceId) =>
            {
                resolvedContext = context;
                resolvedHttpContext = httpContext;
                resolvedInstance = processInstanceId;
                return authorityScope;
            },
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);
        var response = await InvokeAsync(
            app,
            endpoint,
            Uri.EscapeDataString(status.ProcessInstanceId.Value),
            "not-json");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.DoesNotContain("physical/private", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var result = JsonSerializer.Deserialize<ExecutionControlResult>(response.Body, jsonOptions);
        Assert.NotNull(result);
        Assert.Equal(ProcessControlDecisionDisposition.Inspected, result.Disposition);
        Assert.Equal(
            JsonSerializer.Serialize(status, jsonOptions),
            JsonSerializer.Serialize(result.Status, jsonOptions));
        Assert.Null(result.Receipt);
        Assert.Empty(result.DiagnosticCodes);
        Assert.Same(operationContext, resolvedContext);
        Assert.Same(response.HttpContext, resolvedHttpContext);
        Assert.Equal(status.ProcessInstanceId, resolvedInstance);
        Assert.Same(operationContext, repository.OperationContext);
        Assert.Equal(authorityScope, repository.AuthorityScope);
        Assert.Equal(status.ProcessInstanceId, repository.ProcessInstanceId);
        Assert.Equal(0, repository.PhysicalReadCount);

        var operation = Assert.Single(endpoint.Metadata.GetOrderedMetadata<ApiOperation>());
        Assert.Equal(typeof(InspectProcessCommand), operation.RequestType);
        Assert.Equal(typeof(ExecutionControlResult), operation.ResponseType);
        Assert.Equal(HttpMethods.Get, operation.Http?.Method);
        Assert.Null(operation.Http?.Body);
        Assert.Equal(
            ProcessExecutionReadApiEndpointRouteBuilderExtensions.ProcessInstanceIdRouteParameter,
            Assert.Single(operation.Http!.Parameters).Name);
        Assert.Same(
            catalog.Inspect.Operation.AuthorizationRequirements[0],
            Assert.Single(operation.AuthorizationRequirements));
        Assert.Equal(
            InspectPolicy,
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        Assert.Equal(catalog.Inspect.Operation.SemanticReferences, operation.SemanticReferences);
        Assert.Null(catalog.Inspect.Operation.Http);
    }

    [Fact]
    public async Task MapProcessExecutionInspectApi_ConcealsMissingPendingAndMalformedTargets()
    {
        var fixture = ProcessControlTestFixture.Create();
        var pending = new ProcessExecutionRecord(
            "physical/pending",
            "process/pending",
            ProcessExecutionStatus.Pending,
            StartedAtUtc: null,
            UpdatedAtUtc: null,
            CompletedAtUtc: null,
            RuntimeStatus: null,
            Definition: fixture.State().Definition);
        var repository = new RecordingExecutionRepository(processInstanceId =>
            processInstanceId.Value == "process/pending" ? pending : null);
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IProcessExecutionRepository>(repository);
        await using var app = builder.Build();
        var resolverCalls = 0;
        app.MapProcessExecutionInspectApi(
            catalog.Inspect,
            InspectRoute,
            (_, _, _) =>
            {
                resolverCalls++;
                return new("authority/trusted", "tenant/trusted");
            },
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);

        var missing = await InvokeAsync(app, endpoint, "process/not-present");
        var pendingResponse = await InvokeAsync(app, endpoint, "process/pending");
        var malformed = await InvokeAsync(app, endpoint, processInstanceId: null);

        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
        Assert.Equal(ExecutionApiProblemCodes.NotFound, ProblemCode(missing.Body));
        Assert.DoesNotContain("process/not-present", Encoding.UTF8.GetString(missing.Body), StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status404NotFound, pendingResponse.StatusCode);
        Assert.Equal(ExecutionApiProblemCodes.NotFound, ProblemCode(pendingResponse.Body));
        Assert.Equal(StatusCodes.Status404NotFound, malformed.StatusCode);
        Assert.Equal(ExecutionApiProblemCodes.NotFound, ProblemCode(malformed.Body));
        Assert.Equal(2, resolverCalls);
        Assert.Equal(2, repository.LogicalReadCount);
    }

    [Fact]
    public async Task MapProcessExecutionInspectApi_ConflictingStatusAffinityFailsClosed()
    {
        var fixture = ProcessControlTestFixture.Create();
        var status = ExecutionStatusProjector.Project(
            fixture.State(),
            ExecutionRuntimeStatusDetails.Unknown);
        var repository = new RecordingExecutionRepository(_ => Record(status));
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IProcessExecutionRepository>(repository);
        await using var app = builder.Build();
        app.MapProcessExecutionInspectApi(
            catalog.Inspect,
            InspectRoute,
            static (_, _, _) => new("authority/trusted", "tenant/trusted"),
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(app, endpoint, "process/another"));

        Assert.Contains("another logical Process instance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapProcessExecutionInspectApi_RejectsNoncanonicalContractAndMissingRouteAddress()
    {
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        var contractError = Assert.Throws<InvalidOperationException>(() =>
            app.MapProcessExecutionInspectApi(
                catalog.Explain,
                InspectRoute,
                static (_, _, _) => new("authority/trusted"),
                ResolvePolicy));
        var routeError = Assert.Throws<ArgumentException>(() =>
            app.MapProcessExecutionInspectApi(
                catalog.Inspect,
                "/execution-control/processes",
                static (_, _, _) => new("authority/trusted"),
                ResolvePolicy));

        Assert.Contains("not the canonical execution-control inspect contract", contractError.Message, StringComparison.Ordinal);
        Assert.Contains("processInstanceId", routeError.Message, StringComparison.Ordinal);
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints));
    }

    static ProcessExecutionRecord Record(ExecutionStatus status) => new(
        "physical/private",
        status.Definition.DefinitionId.Value,
        ProcessExecutionStatus.Waiting,
        status.CreatedAtUtc,
        status.UpdatedAtUtc,
        CompletedAtUtc: null,
        RuntimeStatus: status,
        Definition: status.Definition);

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
        Assert.Equal(ExecutionControlWireNames.Inspect, operation.Name);
        Assert.Equal(
            ExecutionControlApiWireNames.AuthorizationRequirement(ExecutionControlWireNames.Inspect),
            requirement.Id);
        return InspectPolicy;
    }

    sealed record CapturedResponse(
        int StatusCode,
        string? ContentType,
        byte[] Body,
        HttpContext HttpContext);

    sealed class RecordingExecutionRepository(
        Func<ProcessInstanceId, ProcessExecutionRecord?> read)
        : IProcessExecutionRepository
    {
        public OperationContext? OperationContext { get; private set; }

        public InteractionAuthorityScope? AuthorityScope { get; private set; }

        public ProcessInstanceId? ProcessInstanceId { get; private set; }

        public int LogicalReadCount { get; private set; }

        public int PhysicalReadCount { get; private set; }

        public ValueTask<ProcessExecutionRecord?> GetAsync(
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

        public ValueTask<ProcessExecutionRecord?> GetAsync(
            OperationContext context,
            string processId)
        {
            PhysicalReadCount++;
            throw new InvalidOperationException("The HTTP inspect binding must not use a physical Process key.");
        }

        public ValueTask<ProcessExecutionQueryResult> QueryAsync(
            OperationContext context,
            ProcessExecutionQuery query) =>
            throw new InvalidOperationException("The HTTP inspect binding must not query Process pages.");
    }
}
