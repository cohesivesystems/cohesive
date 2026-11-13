using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Connects prepared service processes to the existing Durable Task admission and execution interpreter.</summary>
/// <remarks>Bind before host start. The native worker freezes the catalog before consuming work. Native clients are
/// borrowed; this provider never starts a worker or owns an independent receipt/result store. External Request and
/// event-publication bindings require the standalone catalog API rather than this service-host convenience profile.</remarks>
public sealed class DurableTaskServiceProcessProvider : IDurableServiceProcessProvider, IAsyncProcessReferenceHost
{
    readonly object gate = new();
    readonly DurableTaskClient client;
    readonly DurableTaskProcessExecutionRepository values;
    readonly Dictionary<ExecutionDefinitionReference, PreparedServiceProcess> processes = [];
    readonly Dictionary<(ExecutionDefinitionReference Process, ExecutionDefinitionReference Query), IAsyncProcessReferenceHost> queries = [];
    readonly Dictionary<ExecutionDefinitionReference, DurableTaskProcessRealizationPlan> plans = [];
    DurableTaskSequentialProcessPlanCatalog? catalog;

    /// <summary>Creates an empty service deployment using the host's native client.</summary>
    /// <param name="client">Borrowed client for the same task hub as the worker.</param>
    public DurableTaskServiceProcessProvider(DurableTaskClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        values = new(client);
    }

    /// <inheritdoc />
    public ServiceRuntime Bind(PreparedServiceProcess process, ExecutionDefinitionDocument declaration,
        string authority, IServiceInvocationAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(process); ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(authorization); ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        if (process.Plan.Definition.Nodes.OfType<SendSignalProcessNode>().Any())
            throw Error("services.binding.signalUnsupported",
                "Signal targets require an explicit standalone catalog.", "/bindings/process/signal");
        var realized = DurableTaskProcessRealizationCompiler.CompileExecutable(process.Plan);
        var plan = realized.Plan ?? throw Error("services.binding.durableUnsupported",
            string.Join("; ", realized.Realization.Diagnostics.Select(item => item.Code + ": " + item.Message)), "/bindings/process");
        // Validate the executable profile and binding closure before changing this provider's catalog.
        _ = new DurableTaskSequentialProcessPlanCatalog([plan]);
        var start = client.CreateCohesiveProcessStartDispatcher((_, request, invocation) => new(
            invocation.Authorization.AuthorityScope, new(request.InitialContinuation.ProcessInstanceId.Value),
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), invocation.Provenance));
        var bindings = declaration.GetDefinition<ServiceDefinition>().Operations.Select(operation => operation switch
        {
            ServiceProcessOperation => (ServiceBinding)new ServiceProcessBinding(operation.Id, process.Plan, authority, start),
            ServiceProcessResultOperation => new ServiceProcessResultBinding(operation.Id, process.Plan, authority, values),
            _ => throw Error("services.binding.executionUnsupported",
                "The durable service provider binds process admission and result operations only.", "/bindings/process")
        }).ToArray();
        var runtime = new ServiceRuntime(declaration, bindings, authorization);
        lock (gate)
        {
            if (catalog is not null) throw new InvalidOperationException("The durable worker catalog is frozen; map services before starting the host.");
            var reference = process.Plan.DefinitionReference;
            if (processes.ContainsKey(reference)) throw Error("services.binding.duplicateProcess",
                "One exact process must have one prepared service host.", "/bindings/process");
            if (processes.Keys.Any(item => item.DefinitionId == reference.DefinitionId && item.RevisionId == reference.RevisionId))
                throw Error("services.binding.processRevisionConflict",
                    "One process ID/revision cannot identify different fingerprints in a deployment.", "/bindings/process");
            var dependencies = process.Plan.Definition.Nodes.OfType<EvaluateRelationProcessNode>().Select(item => item.Relation).Distinct().ToArray();
            processes.Add(reference, process);
            plans.Add(reference, plan);
            foreach (var dependency in dependencies) queries.Add((reference, dependency), process.Host);
        }
        return runtime;
    }

    /// <summary>Freezes the admitted process catalog before native worker activation.</summary>
    /// <returns>The shared immutable deployment catalog.</returns>
    /// <exception cref="InvalidOperationException">No service has been mapped.</exception>
    public DurableTaskSequentialProcessPlanCatalog Complete()
    {
        lock (gate)
        {
            if (plans.Count == 0) throw new InvalidOperationException("Map durable services before starting the scheduler worker.");
            return catalog ??= new(plans.OrderBy(item => item.Key.DefinitionId.Value, StringComparer.Ordinal).ThenBy(item => item.Key.RevisionId.Value, StringComparer.Ordinal).Select(item => item.Value));
        }
    }

    /// <inheritdoc />
    public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation) =>
        RequireProcess(invocation.Process).InvokeTransitionAsync(context, invocation);

    /// <inheritdoc />
    public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation)
    {
        RequireComplete();
        if (evaluation.Process is { } owner)
            return (queries.TryGetValue((owner, evaluation.Definition), out var ownedHost) ? ownedHost
                : throw new InvalidOperationException("The exact process query is not deployed for this owner."))
                .EvaluateRelationAsync(context, evaluation);
        // Legacy evaluation envelopes carry no owner. Preserve only unambiguous routing, never registration order.
        IAsyncProcessReferenceHost? selected = null;
        foreach (var (key, candidate) in queries)
        {
            if (key.Query != evaluation.Definition) continue;
            if (selected is not null) throw new InvalidOperationException("The query has multiple process owners; an exact originating process is required.");
            selected = candidate;
        }
        return (selected ?? throw new InvalidOperationException("The exact process query is not deployed."))
            .EvaluateRelationAsync(context, evaluation);
    }

    /// <inheritdoc />
    public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) =>
        ValueTask.FromResult(ProcessSignalTargetResult.Failed(new("services.binding.signalUnsupported", DiagnosticSeverity.Error,
            "This service-host profile has no signal target binding; use an explicit standalone catalog.", "/bindings/process/signal")));

    static ServiceBindingValidationException Error(string code, string message, string location) =>
        new(new([new(code, DiagnosticSeverity.Error, message, location)]));

    IAsyncProcessReferenceHost RequireProcess(ExecutionDefinitionReference reference)
    {
        RequireComplete();
        return processes.TryGetValue(reference, out var process) ? process.Host : throw new InvalidOperationException("The exact process is not deployed.");
    }
    void RequireComplete()
    {
        if (Volatile.Read(ref catalog) is null) throw new InvalidOperationException("The durable worker catalog has not been admitted.");
    }
}

/// <summary>Registers a native Durable Task client/worker pair for prepared Cohesive service processes.</summary>
public static class DurableTaskServiceProcessRegistration
{
    /// <summary>Registers one provider, native client and worker without activating a model or starting execution.</summary>
    /// <param name="services">Host service collection.</param>
    /// <param name="configureClient">Native SDK client configuration for the deployment's task hub.</param>
    /// <param name="configureWorker">Native SDK worker configuration for that same task hub.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="InvalidOperationException">Another provider or process host is already registered.</exception>
    public static IServiceCollection AddCohesiveDurableTaskServices(this IServiceCollection services,
        Action<IDurableTaskClientBuilder> configureClient, Action<IDurableTaskWorkerBuilder> configureWorker)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(configureClient); ArgumentNullException.ThrowIfNull(configureWorker);
        if (services.Any(item => item.ServiceType == typeof(IDurableServiceProcessProvider) || item.ServiceType == typeof(IAsyncProcessReferenceHost)))
            throw new InvalidOperationException("A durable service worker requires one process provider and host authority.");
        services.AddSingleton<DurableTaskServiceProcessProvider>();
        services.AddSingleton<IDurableServiceProcessProvider>(provider => provider.GetRequiredService<DurableTaskServiceProcessProvider>());
        services.AddSingleton<IAsyncProcessReferenceHost>(provider => provider.GetRequiredService<DurableTaskServiceProcessProvider>());
        services.AddDurableTaskClient(configureClient);
        services.AddDurableTaskWorker(worker =>
        {
            worker.AddCohesiveSequentialProcesses(provider => provider.GetRequiredService<DurableTaskServiceProcessProvider>().Complete());
            configureWorker(worker);
        });
        return services;
    }
}
