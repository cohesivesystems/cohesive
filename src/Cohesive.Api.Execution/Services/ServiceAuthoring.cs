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

namespace Cohesive.Api.Execution.Services;

/// <summary>Human-readable authoring of the canonical service document; no runtime objects survive lowering.</summary>
public static class Service
{
    /// <summary>Begins an immutable service declaration with attributable identity and revision.</summary>
    public static ServiceBuilder Define(ExecutionDefinitionId id, ExecutionRevisionId revision, ExecutionProvenance provenance) =>
        new(id, revision, provenance);
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
        ImmutableArray<ApiAuthorizationRequirement> requirements = default, ImmutableArray<ServiceOperation> operations = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.Value);
        this.id = id;
        this.revision = revision;
        this.provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        this.requirements = requirements.IsDefault ? [] : requirements;
        this.operations = operations.IsDefault ? [] : operations;
    }

    /// <summary>Adds a requirement inherited by subsequently declared operations.</summary>
    /// <remarks>Earlier operations retain their declared requirements; repeated requirements are idempotent.</remarks>
    public ServiceBuilder Require(ApiAuthorizationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return new(id, revision, provenance, requirements.Contains(requirement) ? requirements : requirements.Add(requirement), operations);
    }

    /// <summary>Begins one operation; backing behavior and execution policy must be selected before building.</summary>
    public ServiceOperationBuilder Operation(string operationId) => new(this, operationId, null, requirements);

    internal ServiceBuilder Add(ServiceOperation operation) =>
        new(id, revision, provenance, requirements, operations.Add(operation));

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
        return new(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint);
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
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsValid) throw new ServiceBindingValidationException(definition.Validation);
        return new(service, id, definition.Reference, requirements, definition.Definition.Result);
    }

    /// <summary>References a canonical data-authored Process without introducing a CLR input authority.</summary>
    /// <remarks>Performs document validation only; linked compilation and infrastructure resolution stay at runtime binding.</remarks>
    public ServiceOperationBuilder Run(ExecutionDefinitionDocument document) =>
        new(service, id, RequireDocument(document), requirements, document.GetDefinition<ProcessDefinition>().Result);

    /// <summary>References already compiled behavior without revalidating its dependency closure.</summary>
    public ServiceOperationBuilder Run(CompiledProcessPlan plan)
    {
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
