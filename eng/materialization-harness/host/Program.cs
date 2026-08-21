using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Control;
using Cohesive.MaterializationHarness.Host;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Runtime;
using Cohesive.Storage.Materialization;
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

        if (args is ["--control-scenario-sdk", var scenarioProvider])
        {
            var result = await MaterializationHarnessSdkControlScenario.RunAsync(
                controller: controller,
                provider: scenarioProvider,
                timeout: TimeSpan.FromMinutes(15));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                result,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                }));
            await controller.DisposeAsync();
            return 0;
        }

        if (args is ["--start"] or ["--start", _])
        {
            var now = DateTimeOffset.UtcNow;
            var providers = ResolveProviders(args.ElementAtOrDefault(1), controller);
            var results = new List<object?>();
            var successful = true;
            foreach (var provider in providers)
            {
                var request = controller.CreateStartRequest(
                    provider: provider,
                    attemptId: new($"attempt/{provider}/{now:yyyyMMddHHmmssfffffff}"),
                    issuedAtUtc: now);
                var result = await controller.DispatchAsync(
                    context: OperationContext.Create(),
                    endpoint: catalog.Start,
                    request: request,
                    invocation: Invocation(catalog.Start, options, now));
                results.Add(result.Body);
                successful &= result.Result.Kind == ApiResultKind.Success;
            }
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results));
            await controller.DisposeAsync();
            return successful ? 0 : 1;
        }
        if (args is ["--update-limits", var requestedProvider, var maximumBatchItems]
            && long.TryParse(
                maximumBatchItems,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedMaximumBatchItems))
        {
            var result = await controller.DispatchLimitUpdateAsync(
                provider: requestedProvider,
                maximumBatchItems: parsedMaximumBatchItems,
                issuedAtUtc: DateTimeOffset.UtcNow);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result.Body));
            await controller.DisposeAsync();
            return result.Result.Kind is ApiResultKind.Success or ApiResultKind.Accepted ? 0 : 1;
        }
        if (args.Length is 2 or 3 && string.Equals(args[0], "--failure-evidence", StringComparison.Ordinal))
        {
            var evidenceProvider = args[1];
            var selectedGeneration = args.ElementAtOrDefault(2) is { } generation
                ? new MaterializationGenerationId(generation)
                : (MaterializationGenerationId?)null;
            var evidence = await controller.CaptureFailureEvidenceAsync(
                provider: evidenceProvider,
                selectedGeneration: selectedGeneration,
                context: OperationContext.Create());
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(evidence));
            await controller.DisposeAsync();
            return 0;
        }
        if (args.Length is 1 or 2
            && TryResolveOperatorEndpoint(args[0], catalog, out var endpoint))
        {
            var providers = ResolveProviders(args.ElementAtOrDefault(1), controller);
            var results = new List<object?>();
            var successful = true;
            foreach (var provider in providers)
            {
                var result = await controller.DispatchOperatorAsync(
                    provider: provider,
                    endpoint: endpoint,
                    issuedAtUtc: DateTimeOffset.UtcNow);
                results.Add(result.Body);
                successful &= result.Result.Kind is ApiResultKind.Success or ApiResultKind.Accepted;
            }
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results));
            await controller.DisposeAsync();
            return successful ? 0 : 1;
        }
        if (args.Length != 0)
        {
            throw new ArgumentException(
                "The materialization host accepts --start, --inspect, --explain, --traces, --pause, --continue, --restart-attempt, --cancel, or --failure-evidence, followed by an optional provider; --failure-evidence also accepts a generation, --update-limits requires a provider and maximum batch items, and --control-scenario-sdk requires one provider.",
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
        app.MapGet(
            MaterializationHarnessExecutionRoutes.RequestProjection,
            async (
                string provider,
                string operation,
                long? maximumBatchItems,
                MaterializationHarnessExecutionController executionController) => Results.Json(
                    await executionController.ProjectControlRequestAsync(
                        provider: provider,
                        operation: operation,
                        maximumBatchItems: maximumBatchItems,
                        issuedAtUtc: DateTimeOffset.UtcNow)));
        app.MapGet(
            MaterializationHarnessExecutionRoutes.FailureEvidence,
            async (
                string provider,
                string? generation,
                MaterializationHarnessExecutionController executionController,
                HttpContext httpContext) => Results.Json(await executionController.CaptureFailureEvidenceAsync(
                provider: provider,
                selectedGeneration: string.IsNullOrWhiteSpace(generation)
                    ? null
                    : new MaterializationGenerationId(generation),
                context: OperationContext.Create(cancellationToken: httpContext.RequestAborted))));
        app.MapProcessExecutionInspectApi(
            catalog.Inspect,
            MaterializationHarnessExecutionRoutes.Inspect,
            (_, _, _) => options.AuthorityScope,
            ResolvePolicy);
        app.MapProcessExecutionExplainApi(
            catalog.Explain,
            MaterializationHarnessExecutionRoutes.Explain,
            (_, _, _) => options.AuthorityScope,
            ResolvePolicy);
        app.MapProcessExecutionTracesApi(
            catalog.Traces,
            MaterializationHarnessExecutionRoutes.Traces,
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
            MaterializationHarnessExecutionRoutes.Start,
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<PauseProcessCommand>(
            catalog,
            catalog.Pause,
            MaterializationHarnessExecutionRoutes.Pause,
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<ContinueProcessCommand>(
            catalog,
            catalog.Continue,
            MaterializationHarnessExecutionRoutes.Continue,
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<RestartProcessAttemptCommand>(
            catalog,
            catalog.RestartAttempt,
            MaterializationHarnessExecutionRoutes.RestartAttempt,
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<CancelProcessCommand>(
            catalog,
            catalog.Cancel,
            MaterializationHarnessExecutionRoutes.Cancel,
            (context, http, operation) => ResolveInvocation(context, http, operation, options),
            ResolvePolicy);
        app.MapProcessExecutionCommandApi<ControlLimitUpdateCommand>(
            catalog,
            catalog.UpdateLimits,
            MaterializationHarnessExecutionRoutes.UpdateLimits,
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

    static ImmutableArray<string> ResolveProviders(
        string? requested,
        MaterializationHarnessExecutionController controller)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested, "all", StringComparison.Ordinal))
            return controller.Providers;
        if (controller.Providers.Contains(requested, StringComparer.Ordinal))
            return [requested];
        throw new ArgumentException("The provider must be postgres, cosmos, or all.", nameof(requested));
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
