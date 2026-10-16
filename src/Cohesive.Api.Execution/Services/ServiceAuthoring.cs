using System.Collections.Immutable;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Storage;

namespace Cohesive.Api.Execution.Services;

/// <summary>Human-readable authoring of the canonical service document; no runtime objects survive lowering.</summary>
public static class Service
{
    /// <summary>Begins an immutable service declaration with attributable identity and revision.</summary>
    public static ServiceBuilder Define(ExecutionDefinitionId id, ExecutionRevisionId revision, ExecutionProvenance provenance) =>
        new(id, revision, provenance);
    /// <summary>Binds an exact separately declared service to a unique repository per observation contract.</summary>
    /// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process output.</typeparam>
    /// <param name="declaration">Portable service authority.</param><param name="process">Exact process authority.</param>
    /// <param name="dependencies">Immutable behavior registrations.</param><param name="repositories">Caller-owned entity repositories; matching is semantic, not CLR-name based.</param>
    /// <returns>The prepared finite binding phase.</returns>
    /// <exception cref="ServiceBindingValidationException">Repository authorities are missing or ambiguous, or service binding fails.</exception>
    public static ServiceProcessHostBuilder<TInput, TResult> Bind<TInput, TResult>(ExecutionDefinitionDocument declaration,
        Process<TInput, TResult> process, ServiceDependencies dependencies, params IEntityRepository[] repositories)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        var selected = new Dictionary<ValueContract, IEntityRepository>();
        foreach (var repository in repositories)
        {
            ArgumentNullException.ThrowIfNull(repository);
            if (!selected.TryAdd(ValueContract.FromShape(repository.EntityDefinition.Shape), repository))
                throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.RepositoryAmbiguous, "Multiple repositories claim the same entity observation.", "/bindings/repositories");
        }
        return Bind(declaration, process, dependencies, observation => selected.TryGetValue(observation, out var repository)
            ? repository : throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.RepositoryMissing, "No repository supplies the required entity observation.", "/bindings/repositories"));
    }

    /// <summary>Binds a separately declared service through the same exact dependency resolver as inline authoring.</summary>
    /// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process output.</typeparam>
    /// <param name="declaration">Validated portable service document.</param><param name="process">Exact process authority.</param>
    /// <param name="dependencies">Immutable canonical definitions and native query selections.</param>
    /// <param name="repositories">Setup-time resolver of exact observation contracts to entity authorities.</param>
    /// <param name="contracts">Optional interaction catalog; omission selects an empty catalog.</param>
    /// <returns>A prepared binding phase; invocation policy remains in the service document.</returns>
    /// <exception cref="ServiceBindingValidationException">Document, execution mode or exact dependencies are invalid.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static ServiceProcessHostBuilder<TInput, TResult> Bind<TInput, TResult>(ExecutionDefinitionDocument declaration,
        Process<TInput, TResult> process, ServiceDependencies dependencies, Func<ValueContract, IEntityRepository> repositories,
        InteractionContractCatalog? contracts = null)
    {
        ArgumentNullException.ThrowIfNull(declaration); ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(dependencies); ArgumentNullException.ThrowIfNull(repositories);
        var validation = ServiceDefinitionDocuments.ValidateAndProject(declaration, out _);
        if (!validation.IsValid) throw new ServiceBindingValidationException(validation);
        if (!process.IsValid) throw new ServiceBindingValidationException(process.Validation);
        if (!declaration.Extensions.IsEmpty)
            throw ServiceBindingValidationException.Error("services.binding.extensionsUnsupported", "This finite binding profile does not support service extensions.", "/extensions");
        var definition = declaration.GetDefinition<ServiceDefinition>();
        if (definition.Operations.Length != 1 || definition.Operations[0] is not ServiceProcessOperation operation
            || operation.Process != process.Reference || operation.ResultClassifier is not null
            || operation.Execution is not { Lifetime: ProcessExecutionLifetime.Ephemeral, Completion: ServiceProcessCompletion.Terminal })
            throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.ProcessSelectionInvalid,
                "This finite host requires one exact, unclassified ephemeral process operation.", "/bindings/process");
        var host = new ServiceProcessHostBuilder<TInput, TResult>(declaration, operation, process, contracts);
        host.Use(dependencies, repositories);
        return host;
    }

}

/// <summary>Immutable service authoring state. Branching a declaration never mutates an earlier branch.</summary>
public sealed class ServiceBuilder
{
    readonly ExecutionDefinitionId id;
    readonly ExecutionRevisionId revision;
    readonly ExecutionProvenance provenance;
    readonly ImmutableArray<ApiAuthorizationRequirement> requirements;
    readonly ImmutableArray<ServiceOperation> operations;

    internal ServiceBuilder(ExecutionDefinitionId id, ExecutionRevisionId revision, ExecutionProvenance provenance,
        ImmutableArray<ApiAuthorizationRequirement> requirements = default, ImmutableArray<ServiceOperation> operations = default, ServiceDependencies? dependencies = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.Value);
        Dependencies = dependencies ?? new ServiceDependencies();
        this.id = id;
        this.revision = revision;
        this.provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        this.requirements = requirements.IsDefault ? [] : requirements;
        this.operations = operations.IsDefault ? [] : operations;
    }

    /// <summary>Host-composition selections shared by runtime and infrastructure preparation.</summary>
    public ServiceDependencies Dependencies { get; }

    /// <summary>Attaches immutable dependency registrations without changing the portable service document.</summary>
    /// <param name="dependencies">Canonical behavior and explicit query roots.</param><returns>A new builder.</returns>
    /// <exception cref="ArgumentNullException">Dependencies are null.</exception>
    public ServiceBuilder Use(ServiceDependencies dependencies) =>
        new(id, revision, provenance, requirements, operations, dependencies ?? throw new ArgumentNullException(nameof(dependencies)));

    /// <summary>Binds the authored document to caller-owned repositories selected by exact observation contract.</summary>
    /// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process output.</typeparam>
    /// <param name="process">Exact process authority.</param><param name="repositories">Unique entity authorities.</param>
    /// <returns>The prepared finite binding phase.</returns>
    /// <exception cref="ServiceBindingValidationException">The document or repositories cannot satisfy dependencies.</exception>
    public ServiceProcessHostBuilder<TInput, TResult> Bind<TInput, TResult>(Process<TInput, TResult> process,
        params IEntityRepository[] repositories) => Service.Bind(Build(), process, Dependencies, repositories);

    /// <summary>Binds the authored document with its retained registrations.</summary>
    /// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process output.</typeparam>
    /// <param name="process">Exact authored process.</param><param name="repositories">Setup-time entity authority resolver.</param>
    /// <param name="contracts">Optional explicit interaction catalog.</param><returns>The finite process binding phase.</returns>
    /// <exception cref="ServiceBindingValidationException">The exact document or dependencies cannot be bound.</exception>
    public ServiceProcessHostBuilder<TInput, TResult> Bind<TInput, TResult>(Process<TInput, TResult> process,
        Func<ValueContract, IEntityRepository> repositories, InteractionContractCatalog? contracts = null) =>
        Service.Bind(Build(), process, Dependencies, repositories, contracts);

    /// <summary>Adds a requirement inherited by subsequently declared operations.</summary>
    /// <remarks>Earlier operations retain their declared requirements; repeated requirements are idempotent.</remarks>
    public ServiceBuilder Require(ApiAuthorizationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return new(id, revision, provenance, requirements.Contains(requirement) ? requirements : requirements.Add(requirement), operations, Dependencies);
    }

    /// <summary>Begins one operation; backing behavior and execution policy must be selected before building.</summary>
    public ServiceOperationBuilder Operation(string operationId) => new(this, operationId, null, requirements);

    internal ServiceBuilder Add(ServiceOperation operation) =>
        new(id, revision, provenance, requirements, operations.Add(operation), Dependencies);

    /// <summary>Materializes the existing canonical service IR and fingerprint; duplicate identities are rejected.</summary>
    /// <exception cref="ArgumentException">The service is empty or has duplicate operations or invalid requirements.</exception>
    public ExecutionDefinitionDocument Build() => ServiceDefinitionDocuments.Create(id, revision, new(operations), provenance);
}

/// <summary>Selects exact operation behavior and local authorization without copying canonical contracts.</summary>
public sealed class ServiceOperationBuilder
{
    readonly ServiceBuilder service;
    readonly string id;
    readonly ExecutionDefinitionReference? process;
    readonly ValueContract? processResult;
    readonly ImmutableArray<ApiAuthorizationRequirement> requirements;

    internal ServiceOperationBuilder(ServiceBuilder service, string id, ExecutionDefinitionReference? process,
        ImmutableArray<ApiAuthorizationRequirement> requirements, ValueContract? processResult = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        this.service = service;
        this.id = id;
        this.process = process;
        this.processResult = processResult;
        this.requirements = requirements;
    }

    /// <summary>Attaches native bindings while compiling a canonical process at host setup.</summary>
    /// <typeparam name="TInput">Process input projection.</typeparam>
    /// <typeparam name="TResult">Process result projection.</typeparam>
    /// <param name="definition">Canonical process whose exact dependencies will be bound.</param>
    /// <param name="configureBindings">Native transition and query associations prepared once at setup.</param>
    /// <param name="contracts">Optional explicit interaction catalog; omission selects an empty catalog.</param>
    /// <returns>The process binding phase of this service operation.</returns>
    /// <remarks>Returns a separate binding phase with no Run method. The original immutable operation
    /// remains available for independent branches. Configure each returned phase on one thread.</remarks>
    /// <exception cref="InvalidOperationException">This operation already selected process execution.</exception>
    public ServiceProcessHostBuilder<TInput, TResult> Run<TInput, TResult>(Process<TInput, TResult> definition,
        Action<ServiceProcessHostBuilder<TInput, TResult>> configureBindings, InteractionContractCatalog? contracts = null)
    {
        RequireUnselected();
        ArgumentNullException.ThrowIfNull(configureBindings);
        var binding = new ServiceProcessHostBuilder<TInput, TResult>(this, id, definition, contracts);
        configureBindings(binding);
        return binding;
    }

    /// <summary>Adds an authorization requirement only to this operation; sibling operations are unchanged.</summary>
    public ServiceOperationBuilder Require(ApiAuthorizationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return new(service, id, process, requirements.Contains(requirement) ? requirements : requirements.Add(requirement), processResult);
    }

    /// <summary>Exposes a scoped canonical query without creating an evaluator or acquiring data.</summary>
    /// <param name="query">Canonical query and its existing compilation inputs; no physical plan is prepared.</param>
    /// <param name="revision">Application revision of the immutable query.</param>
    /// <param name="scopeParameter">Required logical-scope parameter supplied by trusted invocation admission.</param>
    /// <returns>The service with this query operation added.</returns>
    public ServiceBuilder EvaluateQuery(RelationQueryCompilationRequest query, ExecutionRevisionId revision,
        QueryParameterId scopeParameter)
    {
        RequireUnselected();
        return service.Add(new ServiceQueryOperation(id, ServiceQueryBinding.GetReference(revision, query), scopeParameter, requirements));
    }

    /// <summary>Exposes the retained public output of an exact typed Process with independent authorization.</summary>
    public ServiceBuilder ReadResultOf<TInput, TResult>(Process<TInput, TResult> definition,
        HostedQuery<TResult, ServiceResultClassification>? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RequireUnselected();
        if (!definition.IsValid) throw new ServiceBindingValidationException(definition.Validation);
        if (classifier is not null && (!classifier.IsValid || classifier.InputContract != definition.Definition.Result
            || classifier.EvaluationSemantics != HostedQueryEvaluationSemantics.DeterministicComputation))
            throw new ArgumentException("The result classifier must be a valid deterministic Query over the exact Process output.", nameof(classifier));
        return service.Add(new ServiceProcessResultOperation(id, definition.Reference, requirements, classifier?.Reference));
    }

    /// <summary>Exposes a data-authored Process output with an exact typed deterministic classifier.</summary>
    /// <remarks>Validates the document locally without compiling or resolving its dependency closure.</remarks>
    public ServiceBuilder ReadResultOf<TResult>(ExecutionDefinitionDocument document,
        HostedQuery<TResult, ServiceResultClassification> classifier) where TResult : notnull
    {
        RequireUnselected();
        ArgumentNullException.ThrowIfNull(classifier);
        var reference = RequireDocument(document);
        var definition = document.GetDefinition<Cohesive.Processes.IR.ProcessDefinition>();
        if (!classifier.IsValid || classifier.InputContract != definition.Result
            || classifier.EvaluationSemantics != HostedQueryEvaluationSemantics.DeterministicComputation)
            throw new ArgumentException("The result classifier must be a valid deterministic Query over the exact Process output.", nameof(classifier));
        return service.Add(new ServiceProcessResultOperation(id, reference, requirements, classifier.Reference));
    }

    static ExecutionDefinitionReference RequireDocument(ExecutionDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validation = ProcessDefinitionDocuments.Validate(document);
        if (!validation.IsValid) throw new ServiceBindingValidationException(validation);
        return document.Reference;
    }

    void RequireUnselected()
    {
        if (process is not null)
            throw new InvalidOperationException("This operation already selects Process execution; complete its execution policy first.");
    }

    /// <summary>References typed canonical behavior; no compilation or infrastructure resolution occurs here.</summary>
    /// <exception cref="ArgumentException">The authored Process is invalid.</exception>
    public ServiceOperationBuilder Run<TInput, TResult>(Process<TInput, TResult> definition)
    {
        RequireUnselected();
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsValid) throw new ServiceBindingValidationException(definition.Validation);
        return new(service, id, definition.Reference, requirements, definition.Definition.Result);
    }

    /// <summary>References a canonical data-authored Process without introducing a CLR input authority.</summary>
    /// <remarks>Performs document validation only; linked compilation and infrastructure resolution stay at runtime binding.</remarks>
    public ServiceOperationBuilder Run(ExecutionDefinitionDocument document)
    {
        RequireUnselected();
        return new(service, id, RequireDocument(document), requirements, document.GetDefinition<ProcessDefinition>().Result);
    }

    /// <summary>References already compiled behavior without revalidating its dependency closure.</summary>
    public ServiceOperationBuilder Run(CompiledProcessPlan plan)
    {
        RequireUnselected();
        ArgumentNullException.ThrowIfNull(plan);
        return new(service, id, plan.DefinitionReference, requirements, plan.Definition.Result);
    }

    /// <summary>Completes the operation with invocation-local execution and an explicit cooperative deadline.</summary>
    public ServiceBuilder ExecuteEphemerally(TimeSpan timeout) => Complete(
        new(ProcessExecutionLifetime.Ephemeral, ServiceProcessCompletion.Terminal, timeout));

    /// <summary>Completes ephemeral execution with a declared classifier over its public terminal output.</summary>
    /// <param name="timeout">Positive cooperative execution budget.</param>
    /// <param name="classifier">Exact deterministic Query over the selected Process result contract.</param>
    /// <returns>The service containing the classified ephemeral operation.</returns>
    /// <exception cref="ArgumentException">The classifier is invalid, nondeterministic, or has a different input contract.</exception>
    /// <exception cref="InvalidOperationException">No Process has been selected.</exception>
    public ServiceBuilder ExecuteEphemerally<TResult>(TimeSpan timeout,
        HostedQuery<TResult, ServiceResultClassification> classifier) where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(classifier);
        if (process is null) throw new InvalidOperationException("Select the Process with Run before choosing its execution policy.");
        if (!classifier.IsValid || classifier.InputContract != processResult
            || classifier.EvaluationSemantics != HostedQueryEvaluationSemantics.DeterministicComputation)
            throw new ArgumentException("The result classifier must be a valid deterministic Query over the exact Process output.", nameof(classifier));
        return Complete(new(ProcessExecutionLifetime.Ephemeral, ServiceProcessCompletion.Terminal, timeout), classifier.Reference);
    }

    /// <summary>Completes the operation with durable admission; the response does not promise Process completion.</summary>
    public ServiceBuilder ReturnAfterDurableAdmission() => Complete(
        new(ProcessExecutionLifetime.Durable, ServiceProcessCompletion.Admission));

    ServiceBuilder Complete(ServiceProcessExecution execution, ExecutionDefinitionReference? classifier = null) => service.Add(new ServiceProcessOperation(id,
        process ?? throw new InvalidOperationException("Select the Process with Run before choosing its execution policy."), requirements, execution, classifier));
}
