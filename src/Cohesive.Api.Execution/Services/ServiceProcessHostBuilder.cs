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
    readonly ServiceOperationBuilder operation;
    readonly string operationId;
    readonly Process<TInput, TResult> process;
    readonly InteractionContractCatalog contracts;
    readonly Dictionary<ExecutionDefinitionReference, ProcessTransitionOperationBinding> transitions = [];
    readonly List<ProcessDefinitionLink> links = [];
    readonly List<ProcessRelationHandlerRegistration> queries = [];

    internal ServiceProcessHostBuilder(ServiceOperationBuilder operation, string operationId, Process<TInput, TResult> process, InteractionContractCatalog? contracts)
    {
        this.operation = operation;
        this.operationId = operationId;
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
    /// <param name="expectedConcurrencyTokenField">Optional required string input field carrying the previously observed storage token.</param>
    /// <returns>This setup session.</returns>
    /// <exception cref="ServiceBindingValidationException">Compilation is invalid, receipt capability is missing,
    /// or observation authority does not match.</exception>
    /// <exception cref="ArgumentException">The exact definition was already registered.</exception>
    public ServiceProcessHostBuilder<TInput, TResult> Transition<TEntity, TTransitionInput, TOutcome>(
        Transition<TEntity, TTransitionInput, TOutcome> transition, IEntityRepository<TEntity> repository,
        string? expectedConcurrencyTokenField = null) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(repository);
        var compilation = transition.Compile();
        var plan = compilation.Plan ?? throw new ServiceBindingValidationException(compilation.Validation);
        return Transition(new ProcessTransitionOperationBinding(plan, repository, contracts,
            expectedConcurrencyTokenField: expectedConcurrencyTokenField));
    }

    /// <summary>Attaches an existing transition binding, preserving its complete subject, token and emission policies.</summary>
    /// <param name="binding">Prepared native binding; CreateProcessDefinitionLink owns capability and authority validation.</param>
    /// <returns>This binding phase.</returns>
    /// <exception cref="ServiceBindingValidationException">The native binding cannot attest the required receipt contract.</exception>
    /// <exception cref="ArgumentException">The definition is already registered.</exception>
    public ServiceProcessHostBuilder<TInput, TResult> Transition(ProcessTransitionOperationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ProcessDefinitionLink link;
        try { link = binding.CreateProcessDefinitionLink(); }
        catch (ProcessTransitionBindingException exception)
        {
            throw new ServiceBindingValidationException(new([.. exception.Validation.Diagnostics.Select(diagnostic => diagnostic with
            {
                Code = diagnostic.Code switch
                {
                    ProcessTransitionBindingDiagnosticCodes.ObservationMismatch => ServiceBindingDiagnosticCodes.ObservationMismatch,
                    ProcessTransitionBindingDiagnosticCodes.ReceiptCapabilityMissing => ServiceBindingDiagnosticCodes.ReceiptCapabilityMissing,
                    // New native diagnostics keep their identity until deliberately projected here.
                    _ => diagnostic.Code
                }
            })]));
        }
        transitions.Add(binding.Plan.DefinitionReference, binding);
        links.Add(link);
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
    /// <param name="authority">Logical execution authority.</param>
    /// <param name="timeout">Explicit finite execution bound.</param>
    /// <param name="authorization">Invocation-time admission and resource authorization.</param>
    /// <returns>The service document and its prepared native runtime.</returns>
    /// <exception cref="ServiceBindingValidationException">The exact process or realization is invalid.</exception>
    public HostedServiceProcess Build(string authority,
        TimeSpan timeout, IServiceInvocationAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var compilation = process.Compile(new ProcessDefinitionValidationContext(links));
        var plan = compilation.Plan ?? throw new ServiceBindingValidationException(compilation.Validation);
        var declaration = operation.Run(plan).ExecuteEphemerally(timeout).Build();
        var deployed = new Dictionary<ExecutionDefinitionReference, ProcessTransitionOperationBinding>(transitions);
        var adapter = new EntityTransitionProcessOperationAdapter(invocation => deployed.GetValueOrDefault(invocation.Definition));
        var host = new RegisteredAsyncProcessReferenceHost(new ProcessRelationHandlerCatalog(queries), adapter.ExecuteAsync);
        return new(declaration, new ServiceRuntime(declaration,
            [new ServiceEphemeralProcessBinding(operationId, plan, authority, (_, _) => host)], authorization), adapter);
    }
}

/// <summary>Canonical service declaration and its host-lifetime native realization.</summary>
public sealed class HostedServiceProcess
{
    readonly EntityTransitionProcessOperationAdapter adapter;
    internal HostedServiceProcess(ExecutionDefinitionDocument declaration, ServiceRuntime runtime,
        EntityTransitionProcessOperationAdapter adapter)
    {
        Declaration = declaration;
        Runtime = runtime;
        this.adapter = adapter;
    }
    /// <summary>Portable semantic authority.</summary>
    public ExecutionDefinitionDocument Declaration { get; }
    /// <summary>Prepared runtime; no invocation state is cached.</summary>
    public ServiceRuntime Runtime { get; }
    /// <summary>Subscribes to full private failure diagnostics from this hosted service only.</summary>
    /// <param name="observer">Synchronous, thread-safe callback writing only to protected operator sinks.</param>
    /// <returns>Idempotent subscription handle; disposal releases the callback. A delivery already in flight may still invoke its callback.</returns>
    /// <exception cref="ArgumentNullException">Observer is null.</exception>
    /// <remarks>No replay/history, global listener or automatic export. Observer failures do not affect operations or other observers.</remarks>
    public IDisposable SubscribeTransitionFailures(Action<EntityTransitionFailureDiagnostic> observer) =>
        adapter.SubscribeTransitionFailures(observer);
}
