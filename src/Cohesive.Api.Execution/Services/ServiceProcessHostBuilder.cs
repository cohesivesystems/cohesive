using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Api.Execution.Services;

/// <summary>Host-lifetime native associations for a finite process. Materialized process IR remains authoritative.</summary>
/// <typeparam name="TInput">Process input projection.</typeparam>
/// <typeparam name="TResult">Process result projection.</typeparam>
/// <remarks>Configure on one thread before Build. Compilation and catalog construction occur at setup;
/// invocation identity, authorization and repository reads remain invocation-scoped. No durable worker is created.</remarks>
public sealed class ServiceProcessHostBuilder<TInput, TResult>
{
    readonly Process<TInput, TResult> process;
    readonly InteractionContractCatalog contracts;
    readonly Dictionary<ExecutionDefinitionReference, ProcessTransitionOperationBinding> transitions = [];
    readonly List<ProcessDefinitionLink> links = [];
    readonly List<ProcessRelationHandlerRegistration> queries = [];

    internal ServiceProcessHostBuilder(Process<TInput, TResult> process, InteractionContractCatalog? contracts)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        if (contracts is null)
        {
            var validation = InteractionContractCatalog.TryCreate([], out contracts);
            if (!validation.IsValid) throw new ServiceBindingValidationException(validation);
        }
        this.contracts = contracts ?? throw new InvalidOperationException("Interaction catalog construction produced no catalog.");
    }

    /// <summary>Compiles and associates an exact transition with its receipt-capable entity authority.</summary>
    /// <typeparam name="TEntity">Entity projection.</typeparam>
    /// <typeparam name="TTransitionInput">Transition input projection.</typeparam>
    /// <typeparam name="TOutcome">Transition outcome projection.</typeparam>
    /// <param name="transition">Canonical authored transition.</param>
    /// <param name="repository">Native atomic state/receipt authority; fixed partition is inherited.</param>
    /// <returns>This setup session.</returns>
    /// <exception cref="ServiceBindingValidationException">Compilation or native capability is invalid.</exception>
    /// <exception cref="ArgumentException">The exact definition was already registered.</exception>
    public ServiceProcessHostBuilder<TInput, TResult> Transition<TEntity, TTransitionInput, TOutcome>(
        Transition<TEntity, TTransitionInput, TOutcome> transition, IEntityRepository<TEntity> repository) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(repository);
        if (!repository.TransitionOperationCapabilities.SupportsAtomicStateAndReceipt)
            throw ServiceBindingValidationException.Error(EntityTransitionOperationDiagnosticCodes.CapabilityInsufficient,
                "Process transitions require atomic state and receipt support.", "/bindings/repository");
        var compilation = transition.Compile();
        var plan = compilation.Plan ?? throw new ServiceBindingValidationException(compilation.Validation);
        if (plan.Definition.Observation != ValueContract.FromShape(repository.EntityDefinition.Shape))
            throw ServiceBindingValidationException.Error("services.binding.observationMismatch",
                "The transition observation must match the repository entity authority.", "/bindings/repository");
        var binding = new ProcessTransitionOperationBinding(plan, repository, contracts);
        transitions.Add(plan.DefinitionReference, binding);
        links.Add(binding.CreateProcessDefinitionLink());
        return this;
    }

    /// <summary>Associates one exact hosted query with its native read implementation.</summary>
    /// <typeparam name="TQueryInput">Query input projection.</typeparam>
    /// <typeparam name="TQueryResult">Query result projection.</typeparam>
    /// <param name="query">Canonical query including declared evaluation guarantees.</param>
    /// <param name="handler">Invocation-scoped read; must honor the query's declared guarantees and cancellation.</param>
    /// <returns>This setup session.</returns>
    public ServiceProcessHostBuilder<TInput, TResult> Query<TQueryInput, TQueryResult>(
        HostedQuery<TQueryInput, TQueryResult> query, ProcessRelationHandler<TQueryInput, TQueryResult> handler)
        where TQueryInput : notnull where TQueryResult : notnull
    {
        var registration = ProcessRelationHandlerRegistration.Create(query, handler);
        queries.Add(registration);
        links.Add(query.CreateProcessDefinitionLink());
        return this;
    }

    /// <summary>Compiles exact links and builds one finite service operation and its runtime.</summary>
    /// <param name="service">Canonical service authoring state, including authorization requirements.</param>
    /// <param name="operationId">Operation identity within that service.</param>
    /// <param name="authority">Logical execution authority.</param>
    /// <param name="timeout">Explicit finite execution bound.</param>
    /// <param name="authorization">Invocation-time admission and resource authorization.</param>
    /// <returns>The service document and its prepared native runtime.</returns>
    /// <exception cref="ServiceBindingValidationException">The exact process or realization is invalid.</exception>
    public HostedServiceProcess Build(ServiceBuilder service, string operationId, string authority,
        TimeSpan timeout, IServiceInvocationAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(authorization);
        var compilation = process.Compile(new ProcessDefinitionValidationContext(links));
        var plan = compilation.Plan ?? throw new ServiceBindingValidationException(compilation.Validation);
        var declaration = service.Operation(operationId).Run(plan).ExecuteEphemerally(timeout).Build();
        var deployed = new Dictionary<ExecutionDefinitionReference, ProcessTransitionOperationBinding>(transitions);
        var adapter = new EntityTransitionProcessOperationAdapter(invocation => deployed.GetValueOrDefault(invocation.Definition));
        var host = new RegisteredAsyncProcessReferenceHost(new ProcessRelationHandlerCatalog(queries), adapter.ExecuteAsync);
        return new(declaration, new ServiceRuntime(declaration,
            [new ServiceEphemeralProcessBinding(operationId, plan, authority, (_, _) => host)], authorization));
    }
}

/// <summary>Canonical service declaration and its host-lifetime native realization.</summary>
/// <param name="Declaration">Portable semantic authority.</param>
/// <param name="Runtime">Prepared runtime; no invocation state is cached.</param>
public sealed record HostedServiceProcess(ExecutionDefinitionDocument Declaration, ServiceRuntime Runtime);
