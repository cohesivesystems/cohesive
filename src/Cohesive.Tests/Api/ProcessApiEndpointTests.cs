using System.Text;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Tests.Api;

public sealed class ProcessApiEndpointTests
{
    [Fact]
    public async Task MapProcessApiDefinition_BindsStartAndStatusOperations()
    {
        var storage = new InMemoryProcessStorageAdapter();
        var engine = new ProcessEngine(new(
            transitionHost: new DeclarativeTransitionHost(),
            entityRepository: storage,
            checkpointRepository: storage,
            transactionGateway: storage,
            waitAdapter: new InMemoryProcessWaitAdapter(),
            deadLetterSink: new InMemoryProcessDeadLetterSink()));
        var app = CreateApp(engine);

        var start = await InvokeAsync(
            app,
            route: "/processes/echo",
            method: "POST",
            body: new StartEchoRequest("process-echo-001", "hello"));
        Assert.Equal(StatusCodes.Status200OK, start.StatusCode);
        var processId = ReadJson(start.Body).GetProperty(nameof(StartEchoResponse.ProcessId)).GetString();
        Assert.Equal("process-echo-001", processId);

        await engine.WaitForCompletionAsync<ApiEchoResult>(OperationContext.Create(), processId!);

        var status = await InvokeAsync(
            app,
            route: "/processes/echo/{processId}",
            method: "GET",
            routeValues: new() { ["processId"] = processId });
        Assert.Equal(StatusCodes.Status200OK, status.StatusCode);
        var statusJson = ReadJson(status.Body);
        Assert.Equal(ProcessExecutionStatus.Completed.ToString(), statusJson.GetProperty(nameof(EchoStatusResponse.Status)).GetString());
        Assert.Equal("hello:done", statusJson.GetProperty(nameof(EchoStatusResponse.Message)).GetString());
    }

    [Fact]
    public async Task MapProcessApiDefinition_ReusesProcessDefinitionAcrossStarts()
    {
        var engine = new CapturingProcessEngine();
        var app = CreateApp(engine);

        var first = await InvokeAsync(
            app,
            route: "/processes/echo",
            method: "POST",
            body: new StartEchoRequest("process-echo-001", "hello"));
        var second = await InvokeAsync(
            app,
            route: "/processes/echo",
            method: "POST",
            body: new StartEchoRequest("process-echo-002", "again"));

        Assert.Equal(StatusCodes.Status200OK, first.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, second.StatusCode);
        Assert.Equal(2, engine.StartedDefinitions.Count);
        Assert.Same(engine.StartedDefinitions[0], engine.StartedDefinitions[1]);
    }

    [Fact]
    public async Task MapProcessApiDefinition_UsesConfiguredProcessDefinitionInstance()
    {
        var engine = new CapturingProcessEngine();
        var processDefinition = new ApiEchoProcess().Define();
        var app = CreateApp(engine, processDefinition);

        var response = await InvokeAsync(
            app,
            route: "/processes/echo",
            method: "POST",
            body: new StartEchoRequest("process-echo-001", "hello"));

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        var started = Assert.Single(engine.StartedDefinitions);
        Assert.Same(processDefinition.Definition, started);
    }

    [Fact]
    public async Task MapProcessEndpoints_BindsRawStartAndStatusRoutes()
    {
        var storage = new InMemoryProcessStorageAdapter();
        var engine = new ProcessEngine(new(
            transitionHost: new DeclarativeTransitionHost(),
            entityRepository: storage,
            checkpointRepository: storage,
            transactionGateway: storage,
            waitAdapter: new InMemoryProcessWaitAdapter(),
            deadLetterSink: new InMemoryProcessDeadLetterSink()));
        var app = CreateRawApp(engine);

        var start = await InvokeAsync(
            app,
            route: "/raw/processes/echo",
            method: "POST",
            body: new StartEchoRequest("raw-process-echo-001", "raw"));
        Assert.Equal(StatusCodes.Status200OK, start.StatusCode);
        var processId = ReadJson(start.Body).GetProperty(nameof(StartEchoResponse.ProcessId)).GetString();
        Assert.Equal("raw-process-echo-001", processId);

        await engine.WaitForCompletionAsync<ApiEchoResult>(OperationContext.Create(), processId!);

        var status = await InvokeAsync(
            app,
            route: "/raw/processes/echo/{processId}",
            method: "GET",
            routeValues: new() { ["processId"] = processId });
        Assert.Equal(StatusCodes.Status200OK, status.StatusCode);
        var statusJson = ReadJson(status.Body);
        Assert.Equal(ProcessExecutionStatus.Completed.ToString(), statusJson.GetProperty(nameof(EchoStatusResponse.Status)).GetString());
        Assert.Equal("raw:done", statusJson.GetProperty(nameof(EchoStatusResponse.Message)).GetString());
    }

    [Fact]
    public async Task MapProcessExecutionQueryApiDefinition_BindsQueryRequestAndRepository()
    {
        var repository = new CapturingProcessExecutionRepository(new([
            new(
                ProcessId: "process-001",
                ProcessName: "Echo",
                Status: ProcessExecutionStatus.Running,
                StartedAtUtc: null,
                UpdatedAtUtc: null,
                CompletedAtUtc: null)
        ], ContinuationToken: "next"));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton<IProcessExecutionRepository>(repository);
        var app = builder.Build();
        var queryApi = CreateQueryApi();
        app.MapProcessExecutionQueryApiDefinition(
            queryApi.Endpoints.Single(),
            new ProcessExecutionApiEndpointOptions<QueryRunsRequest>
            {
                CreateQuery = static context => new()
                {
                    ProcessIdPrefix = context.Request.Prefix,
                    Statuses = ProcessExecutionApiQuerySupport.ResolveStatuses(
                        context.Request.Status,
                        [ProcessExecutionStatus.Pending]),
                    Limit = context.Request.Limit
                },
                CreateResult = static context => Results.Ok(new QueryRunsResponse(
                    context.Result.Items.Select(static item => item.ProcessId).ToArray(),
                    context.Result.ContinuationToken))
            });

        var response = await InvokeAsync(
            app,
            route: "/process_runs",
            method: "GET",
            queryString: "?prefix=process-&status=Running&limit=5");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.NotNull(repository.LastQuery);
        Assert.Equal("process-", repository.LastQuery.ProcessIdPrefix);
        Assert.Equal(5, repository.LastQuery.Limit);
        Assert.Contains(ProcessExecutionStatus.Running, repository.LastQuery.Statuses!);
        var json = ReadJson(response.Body);
        Assert.Equal("process-001", json.GetProperty(nameof(QueryRunsResponse.Items))[0].GetString());
        Assert.Equal("next", json.GetProperty(nameof(QueryRunsResponse.ContinuationToken)).GetString());
    }

    static WebApplication CreateApp(
        IProcessEngine engine,
        TypedProcessDefinition<ApiEchoTrigger, ApiEchoResult>? processDefinition = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton(engine);

        var app = builder.Build();
        var api = CreateApi();
        app.MapProcessApiDefinition<ApiEchoProcess, StartEchoRequest, ApiEchoTrigger, ApiEchoResult>(
            api.Endpoints.Single(static endpoint => endpoint.Name == "StartEcho"),
            api.Endpoints.Single(static endpoint => endpoint.Name == "GetEchoStatus"),
            new()
            {
                ProcessDefinition = processDefinition,
                CreateProcessId = static context => context.Request.ProcessId,
                CreateInput = static context => new(context.Request.Message),
                LoadCompletedRunAsync = static async (engine, context, processId, _) =>
                {
                    var completed = await engine.WaitForCompletionAsync<ApiEchoResult>(context, processId);
                    return completed;
                },
                CreateStartResult = static context => Results.Ok(new StartEchoResponse(
                    context.ProcessId,
                    context.Started.ProcessName)),
                CreateStatusResult = static context => Results.Ok(new EchoStatusResponse(
                    context.ProcessId,
                    context.Status.Status.ToString(),
                    context.CompletedRun?.Result.Message))
            });

        return app;
    }

    static WebApplication CreateRawApp(IProcessEngine engine)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddSingleton(engine);

        var app = builder.Build();
        app.MapProcessEndpoints<ApiEchoProcess, StartEchoRequest, ApiEchoTrigger, ApiEchoResult>(new()
        {
            StartPattern = "/raw/processes/echo",
            StatusPattern = "/raw/processes/echo/{processId}",
            CreateProcessId = static (_, request) => request.ProcessId,
            CreateInput = static (_, request, _) => new(request.Message),
            LoadCompletedRunAsync = static async (engine, context, processId, _) =>
            {
                var completed = await engine.WaitForCompletionAsync<ApiEchoResult>(context, processId);
                return completed;
            },
            CreateStartResult = static context => Results.Ok(new StartEchoResponse(
                context.ProcessId,
                context.Started.ProcessName)),
            CreateStatusResult = static context => Results.Ok(new EchoStatusResponse(
                context.ProcessId,
                context.Status.Status.ToString(),
                context.CompletedRun?.Result.Message))
        });

        return app;
    }

    static ApiDefinition CreateApi() => Cohesive.Api.Api.Define()
        .Action("StartEcho")
            .Route("POST", "/processes/echo")
            .Body<StartEchoRequest>()
            .Returns<StartEchoResponse>()
            .Done()
        .Action("GetEchoStatus")
            .Route("GET", "/processes/echo/{processId}")
            .RouteParameter<string>("processId")
            .Returns<EchoStatusResponse>()
            .Done()
        .Build();

    static ApiDefinition CreateQueryApi() => Cohesive.Api.Api.Define()
        .Action("QueryRuns")
            .Route("GET", "/process_runs")
            .Query<QueryRunsRequest>()
            .Returns<QueryRunsResponse>()
            .Done()
        .Build();

    static async Task<InvocationResult> InvokeAsync(
        WebApplication app,
        string route,
        string method,
        Dictionary<string, object?>? routeValues = null,
        object? body = null,
        string? queryString = null)
    {
        var endpoint = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => string.Equals(x.RoutePattern.RawText, route, StringComparison.Ordinal)
                         && x.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) == true);

        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Response =
            {
                Body = new MemoryStream()
            }
        };
        context.Request.Method = method;
        context.Request.Path = route;
        if (routeValues is not null)
        {
            foreach (var (key, value) in routeValues)
            {
                context.Request.RouteValues[key] = value;
                context.Request.Path = context.Request.Path.Value?.Replace($"{{{key}}}", value?.ToString() ?? "");
            }
        }

        if (!string.IsNullOrWhiteSpace(queryString))
            context.Request.QueryString = new QueryString(queryString);

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature(true));
        }

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return new(context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    static JsonElement ReadJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    sealed record InvocationResult(int StatusCode, string Body);

    sealed record StartEchoRequest(string ProcessId, string Message);

    sealed record StartEchoResponse(string ProcessId, string ProcessName);

    sealed record EchoStatusResponse(string ProcessId, string Status, string? Message);

    sealed record QueryRunsRequest(string? Prefix = null, string[]? Status = null, int? Limit = null);

    sealed record QueryRunsResponse(string[] Items, string? ContinuationToken);

    sealed record RequestBodyDetectionFeature(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;

    sealed class CapturingProcessExecutionRepository(ProcessExecutionQueryResult result) : IProcessExecutionRepository
    {
        public ProcessExecutionQuery? LastQuery { get; private set; }

        public ValueTask<ProcessExecutionRecord?> GetAsync(OperationContext context, string processId) =>
            new(result.Items.FirstOrDefault(item => string.Equals(item.ProcessId, processId, StringComparison.Ordinal)));

        public ValueTask<ProcessExecutionQueryResult> QueryAsync(OperationContext context, ProcessExecutionQuery query)
        {
            LastQuery = query;
            return new(result);
        }
    }

    sealed class CapturingProcessEngine : IProcessEngine
    {
        readonly List<ProcessDefinition> startedDefinitions = [];

        public IReadOnlyList<ProcessDefinition> StartedDefinitions => startedDefinitions;

        public Task<ProcessStartResult> StartAsync(
            OperationContext context,
            ProcessDefinition process,
            IReadOnlyDictionary<string, object?>? parameters = null,
            ProcessRunOptions? runOptions = null)
        {
            startedDefinitions.Add(process);
            return Task.FromResult(new ProcessStartResult(
                ProcessId: runOptions?.ProcessId ?? Guid.NewGuid().ToString("N"),
                ProcessName: process.Name,
                StartedAtUtc: context.UtcNow));
        }

        public Task<ProcessExecutionState?> GetStatusAsync(OperationContext context, string processId) =>
            Task.FromResult<ProcessExecutionState?>(null);

        public Task SignalAsync(OperationContext context, string processId, string signalKey, object? payload = null) =>
            Task.CompletedTask;

        public Task<ProcessRunResult> WaitForCompletionAsync(OperationContext context, string processId) =>
            Task.FromException<ProcessRunResult>(new NotSupportedException());
    }
}

public sealed record ApiEchoTrigger(string Message);

public sealed record ApiEchoResult(string Message);

[GenerateProcessDefinition(nameof(Build))]
public partial class ApiEchoProcess : IProcessDefinition<ApiEchoTrigger, ApiEchoResult>
{
    async ProcessTask<ApiEchoResult> Build(ProcessAuthoringContext<ApiEchoTrigger, ApiEchoResult> process, ApiEchoTrigger trigger)
    {
        var message = await process.Compute(trigger.Message + ":done");
        return process.Return(new(message));
    }
}
