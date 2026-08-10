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

public sealed class ProcessExecutionExplainApiEndpointRouteBuilderExtensionsTests
{
    const string ExplainRoute = "/execution-control/processes/{processInstanceId}/explain";
    const string ExplainPolicy = "cohesive.execution.explain";

    [Fact]
    public async Task MapProcessExecutionExplainApi_UsesTrustedLogicalAddressAndReturnsCanonicalJson()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var artifact = Artifact(state);
        var repository = new RecordingExplainRepository(artifact);
        var operationContext = OperationContext.Create();
        var authorityScope = new InteractionAuthorityScope("authority/trusted", "tenant/trusted");
        OperationContext? resolvedContext = null;
        HttpContext? resolvedHttpContext = null;
        ProcessInstanceId resolvedInstance = default;
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(operationContext);
        builder.Services.AddSingleton<IProcessExecutionExplainRepository>(repository);
        await using var app = builder.Build();

        app.MapProcessExecutionExplainApi(
            catalog.Explain,
            ExplainRoute,
            (context, httpContext, processInstanceId) =>
            {
                resolvedContext = context;
                resolvedHttpContext = httpContext;
                resolvedInstance = processInstanceId;
                return authorityScope;
            },
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);
        var response = await InvokeAsync(app, endpoint, state.ProcessInstanceId.Value, "not-json");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal(
            ExecutionExplainJsonSerializer.GetCanonicalBytes(artifact),
            response.Body);
        Assert.Same(operationContext, resolvedContext);
        Assert.Same(response.HttpContext, resolvedHttpContext);
        Assert.Equal(state.ProcessInstanceId, resolvedInstance);
        Assert.Same(operationContext, repository.OperationContext);
        Assert.Equal(authorityScope, repository.AuthorityScope);
        Assert.Equal(state.ProcessInstanceId, repository.ProcessInstanceId);
        Assert.Equal(0, repository.PhysicalReadCount);

        var operation = Assert.Single(endpoint.Metadata.GetOrderedMetadata<ApiOperation>());
        Assert.Equal(typeof(InspectProcessCommand), operation.RequestType);
        Assert.Equal(typeof(ExecutionExplainArtifact), operation.ResponseType);
        Assert.Equal(HttpMethods.Get, operation.Http?.Method);
        Assert.Null(operation.Http?.Body);
        Assert.Equal(
            ProcessExecutionExplainApiEndpointRouteBuilderExtensions.ProcessInstanceIdRouteParameter,
            Assert.Single(operation.Http!.Parameters).Name);
        Assert.Same(
            catalog.Explain.Operation.AuthorizationRequirements[0],
            Assert.Single(operation.AuthorizationRequirements));
        Assert.Equal(
            ExplainPolicy,
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        Assert.Equal(catalog.Explain.Operation.SemanticReferences, operation.SemanticReferences);
        Assert.Null(catalog.Explain.Operation.Http);
    }

    [Fact]
    public async Task MapProcessExecutionExplainApi_MissingAndMalformedTargetsReturnOpaqueDeclaredProblems()
    {
        var repository = new RecordingExplainRepository(artifact: null);
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IProcessExecutionExplainRepository>(repository);
        await using var app = builder.Build();
        var resolverCalls = 0;
        app.MapProcessExecutionExplainApi(
            catalog.Explain,
            ExplainRoute,
            (_, _, _) =>
            {
                resolverCalls++;
                return new("authority/trusted", "tenant/trusted");
            },
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);

        var missing = await InvokeAsync(app, endpoint, "process/not-present");
        var malformed = await InvokeAsync(app, endpoint, processInstanceId: null);

        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
        Assert.Equal(ExecutionApiProblemCodes.NotFound, ProblemCode(missing.Body));
        Assert.DoesNotContain("process/not-present", Encoding.UTF8.GetString(missing.Body), StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status400BadRequest, malformed.StatusCode);
        Assert.Equal(ExecutionApiProblemCodes.InvalidRequest, ProblemCode(malformed.Body));
        Assert.Equal(1, resolverCalls);
        Assert.Equal(1, repository.LogicalReadCount);
    }

    [Fact]
    public async Task MapProcessExecutionExplainApi_ConflictingArtifactAffinityFailsClosed()
    {
        var fixture = ProcessControlTestFixture.Create();
        var repository = new RecordingExplainRepository(Artifact(fixture.State()));
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IProcessExecutionExplainRepository>(repository);
        await using var app = builder.Build();
        app.MapProcessExecutionExplainApi(
            catalog.Explain,
            ExplainRoute,
            static (_, _, _) => new("authority/trusted", "tenant/trusted"),
            ResolvePolicy);
        var endpoint = GetRouteEndpoint(app);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(app, endpoint, "process/another"));

        Assert.Contains("another logical Process instance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapProcessExecutionExplainApi_RejectsNoncanonicalContractAndMissingRouteAddress()
    {
        var catalog = ExecutionControlApiCatalog.Create();
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        var contractError = Assert.Throws<InvalidOperationException>(() =>
            app.MapProcessExecutionExplainApi(
                catalog.Inspect,
                ExplainRoute,
                static (_, _, _) => new("authority/trusted"),
                ResolvePolicy));
        var routeError = Assert.Throws<ArgumentException>(() =>
            app.MapProcessExecutionExplainApi(
                catalog.Explain,
                "/execution-control/processes/explain",
                static (_, _, _) => new("authority/trusted"),
                ResolvePolicy));

        Assert.Contains("not the canonical execution-control explain contract", contractError.Message, StringComparison.Ordinal);
        Assert.Contains("processInstanceId", routeError.Message, StringComparison.Ordinal);
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints));
    }

    static ExecutionExplainArtifact Artifact(ProcessControlState state)
    {
        var provenance = ProcessControlTestFixture.Provenance();
        var kind = new ExecutionDefinitionKind("tests.process");
        var schema = ExecutionDefinitionDocument.CurrentSchemaVersion;
        var status = ExecutionStatusProjector.Project(state, ExecutionRuntimeStatusDetails.Unknown);
        var interpreter = new ExecutionInterpreterProfileReference(
            "tests.process.interpreter",
            "v1",
            new([schema]),
            [kind],
            provenance);
        return new(
            ExecutionExplainArtifact.CurrentSchemaVersion,
            new(kind, schema, state.Definition, provenance, ExecutionSourceMap.Empty),
            interpreter,
            [
                new(
                    ExecutionExplainStageNames.Definition,
                    kind.Value,
                    state.Definition.DefinitionId.Value,
                    ExecutionExplainEvidenceAuthority.Declared,
                    "Available",
                    sourceReferences: [provenance.Source.Reference])
            ],
            runtimeStatus: status);
    }

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
                ProcessExecutionExplainApiEndpointRouteBuilderExtensions.ProcessInstanceIdRouteParameter] =
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
        Assert.Equal(ExecutionExplainWireNames.Explain, operation.Name);
        Assert.Equal(
            ExecutionControlApiWireNames.AuthorizationRequirement(ExecutionExplainWireNames.Explain),
            requirement.Id);
        return ExplainPolicy;
    }

    sealed record CapturedResponse(
        int StatusCode,
        string? ContentType,
        byte[] Body,
        HttpContext HttpContext);

    sealed class RecordingExplainRepository(ExecutionExplainArtifact? artifact)
        : IProcessExecutionExplainRepository
    {
        public OperationContext? OperationContext { get; private set; }

        public InteractionAuthorityScope? AuthorityScope { get; private set; }

        public ProcessInstanceId? ProcessInstanceId { get; private set; }

        public int LogicalReadCount { get; private set; }

        public int PhysicalReadCount { get; private set; }

        public ValueTask<ExecutionExplainArtifact?> GetExplainAsync(
            OperationContext context,
            InteractionAuthorityScope authorityScope,
            ProcessInstanceId processInstanceId)
        {
            OperationContext = context;
            AuthorityScope = authorityScope;
            ProcessInstanceId = processInstanceId;
            LogicalReadCount++;
            return ValueTask.FromResult(artifact);
        }

        public ValueTask<ExecutionExplainArtifact?> GetExplainAsync(
            OperationContext context,
            string processId)
        {
            PhysicalReadCount++;
            throw new InvalidOperationException("The HTTP explain binding must not use a physical Process key.");
        }
    }
}
