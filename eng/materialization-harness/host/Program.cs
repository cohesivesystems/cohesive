using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Host;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

return await ProgramEntry.RunAsync(args);

static class ProgramEntry
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var options = HarnessHostOptions.FromEnvironment();
        var catalog = ExecutionControlApiCatalog.Create();
        await using var dataSource = NpgsqlDataSource.Create(options.PostgresConnectionString);
        var controller = new MaterializationHarnessExecutionController(
            dataSource: dataSource,
            options: options,
            catalog: catalog);
        await controller.EnsureCreatedAsync(OperationContext.Create());

        if (args is ["--start"])
        {
            var now = DateTimeOffset.UtcNow;
            var request = controller.CreateStartRequest(
                attemptId: new($"attempt/{now:yyyyMMddHHmmssfffffff}"),
                issuedAtUtc: now);
            var result = await controller.DispatchAsync(
                context: OperationContext.Create(),
                endpoint: catalog.Start,
                request: request,
                invocation: Invocation(catalog.Start, options, now));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result.Body));
            return result.Result.Kind == ApiResultKind.Success ? 0 : 1;
        }
        if (args is [var command] && TryResolveOperatorEndpoint(command, catalog, out var endpoint))
        {
            var result = await controller.DispatchOperatorAsync(endpoint, DateTimeOffset.UtcNow);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result.Body));
            return result.Result.Kind is ApiResultKind.Success or ApiResultKind.Accepted ? 0 : 1;
        }
        if (args.Length != 0)
        {
            throw new ArgumentException(
                "The materialization host accepts --start, --inspect, --explain, --traces, --pause, --continue, --restart-attempt, or --cancel.",
                nameof(args));
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(options.Url);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton(controller);
        builder.Services.AddSingleton<IExecutionControlApiDispatcher>(controller);
        builder.Services.AddSingleton<IProcessExecutionRepository>(controller);
        builder.Services.AddSingleton<IProcessExecutionExplainRepository>(controller);
        builder.Services.AddSingleton<IProcessExecutionTraceRepository>(controller);
        builder.Services.AddSingleton(OperationContext.Create());
        builder.Services.AddHostedService<MaterializationHarnessWorker>();
        builder.Services.AddAuthorization(configuration =>
        {
            foreach (var operation in catalog.Definition.Operations)
            {
                foreach (var requirement in operation.AuthorizationRequirements)
                {
                    configuration.AddPolicy(
                        requirement.Id,
                        policy => policy.RequireAssertion(static _ => true));
                }
            }
        });

        await using var app = builder.Build();
        app.UseAuthorization();
        MapCommands(app, catalog, options);
        app.MapProcessExecutionInspectApi(
            catalog.Inspect,
            "/execution-control/processes/{processInstanceId}",
            (_, _, _) => options.AuthorityScope,
            ResolvePolicy);
        app.MapProcessExecutionExplainApi(
            catalog.Explain,
            "/execution-control/processes/{processInstanceId}/explain",
            (_, _, _) => options.AuthorityScope,
            ResolvePolicy);
        app.MapProcessExecutionTracesApi(
            catalog.Traces,
            "/execution-control/processes/{processInstanceId}/traces",
            (_, _, _) => options.AuthorityScope,
            ResolvePolicy);
        await app.RunAsync();
        return 0;
    }

    static void MapCommands(
        WebApplication app,
        ExecutionControlApiCatalog catalog,
        HarnessHostOptions options)
    {
        app.MapProcessExecutionCommandApi<ProcessStartRequest>(
            catalog,
            catalog.Start,
            "/execution-control/processes/start",
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<PauseProcessCommand>(
            catalog,
            catalog.Pause,
            "/execution-control/processes/pause",
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<ContinueProcessCommand>(
            catalog,
            catalog.Continue,
            "/execution-control/processes/continue",
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<RestartProcessAttemptCommand>(
            catalog,
            catalog.RestartAttempt,
            "/execution-control/processes/restart-attempt",
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<CancelProcessCommand>(
            catalog,
            catalog.Cancel,
            "/execution-control/processes/cancel",
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<ControlLimitUpdateCommand>(
            catalog,
            catalog.UpdateLimits,
            "/execution-control/processes/update-limits",
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
    }

    static bool TryResolveOperatorEndpoint(
        string command,
        ExecutionControlApiCatalog catalog,
        out ApiEndpoint endpoint)
    {
        endpoint = command switch
        {
            "--inspect" => catalog.Inspect,
            "--explain" => catalog.Explain,
            "--traces" => catalog.Traces,
            "--pause" => catalog.Pause,
            "--continue" => catalog.Continue,
            "--restart-attempt" => catalog.RestartAttempt,
            "--cancel" => catalog.Cancel,
            _ => null!
        };
        return endpoint is not null;
    }

    static ValueTask<ExecutionApiInvocationContext> ResolveInvocation(
        OperationContext context,
        HttpContext httpContext,
        ApiOperation operation,
        HarnessHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(httpContext);
        var now = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(Invocation(
            operation.AuthorizationRequirements.Select(static requirement => requirement.Id),
            options,
            now));
    }

    static ExecutionApiInvocationContext Invocation(
        ApiEndpoint endpoint,
        HarnessHostOptions options,
        DateTimeOffset now) =>
        Invocation(
            endpoint.Operation.AuthorizationRequirements.Select(static requirement => requirement.Id),
            options,
            now);

    static ExecutionApiInvocationContext Invocation(
        IEnumerable<string> grants,
        HarnessHostOptions options,
        DateTimeOffset now) => new(
        authorization: new(
            actor: "operator/materialization-harness",
            authorityScope: options.AuthorityScope,
            evidenceReference: "policy/materialization-harness/local-allow"),
        provenance: new(
            new("cohesive-materialization-harness", "1"),
            new("eng/materialization-harness/host/http"),
            DocumentOrigin.Generated),
        issuedAtUtc: now,
        observedAtUtc: now,
        grantedRequirements: [.. grants]);

    static string ResolvePolicy(ApiOperation operation, ApiAuthorizationRequirement requirement) =>
        requirement.Id;
}
