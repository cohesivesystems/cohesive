using Cohesive.Relations.Execution;
using Cohesive.Model.Serialization;
using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Api.Execution.Services;

/// <summary>Immutable host-composition registrations; exact Process references select dependencies at setup.</summary>
/// <remarks>Canonical documents remain semantic authority. Delegates are runtime attachments, never persisted IR.
/// Unused process dependencies do not resolve repositories. Query selections are explicit transport roots.
/// Transition compilation is thread-safe and cached, including failures, for the registration lifetime.
/// Lookup delegates must support concurrent invocation; repositories remain caller-owned.</remarks>
public sealed class ServiceDependencies
{
    delegate ProcessTransitionOperationBinding TransitionFactory(Func<ValueContract, IEntityRepository> resolve, InteractionContractCatalog contracts);
    delegate (ProcessDefinitionLink Link, ProcessRelationHandlerRegistration Handler) LookupFactory(Func<ValueContract, IEntityRepository> resolve);

    readonly ImmutableDictionary<ExecutionDefinitionReference, TransitionFactory> transitions;
    readonly ImmutableDictionary<ExecutionDefinitionReference, LookupFactory> lookups;
    /// <summary>Creates an empty registration set.</summary>
    public ServiceDependencies() : this(ImmutableDictionary<ExecutionDefinitionReference, TransitionFactory>.Empty,
        ImmutableDictionary<ExecutionDefinitionReference, LookupFactory>.Empty, []) { }
    ServiceDependencies(
        ImmutableDictionary<ExecutionDefinitionReference, TransitionFactory> transitions,
        ImmutableDictionary<ExecutionDefinitionReference, LookupFactory> lookups,
        ImmutableArray<ServiceQuerySelection> queries)
    { this.transitions = transitions; this.lookups = lookups; Queries = queries; }

    /// <summary>Explicit relational query roots selected for this service's transports.</summary>
    public ImmutableArray<ServiceQuerySelection> Queries { get; }

    /// <summary>Includes typed attachments generated from a process's actual direct invocations.</summary>
    /// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process result.</typeparam>
    /// <param name="model">Completed process authoring instance.</param>
    /// <returns>A new registration set; compilation and repository resolution remain deferred to binding.</returns>
    /// <exception cref="ArgumentNullException">Model is null.</exception>
    /// <exception cref="ServiceBindingValidationException">An attachment differs from the canonical graph, competing recipes claim one exact dependency.</exception>
    /// <exception cref="ArgumentException">An exact dependency is already explicitly registered.</exception>
    /// <remarks>Raw exact references and arbitrary hosted queries require explicit bindings by final host binding, in any registration order. No handler is
    /// inferred from a query name. Independently declared services can use this method or register each binding.</remarks>
    public ServiceDependencies Process<TInput, TResult>(ProcessModel<TInput, TResult> model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var required = Required(model.Definition.Definition);
        var contributed = new Dictionary<ExecutionDefinitionReference, ProcessDependency>();
        var receiver = new DependencyReceiver(this);
        foreach (var dependency in model.Dependencies)
        {
            var reference = dependency.Reference;
            if (!required.Transitions.Contains(reference) && !required.Queries.Contains(reference))
                throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.GeneratedDependencyMismatch,
                    "A generated attachment does not match an exact dependency in the canonical process.", "/bindings/process/dependencies");
            if (contributed.TryGetValue(reference, out var previous))
            {
                if (!previous.HasSameAuthority(dependency))
                    throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.GeneratedDependencyAmbiguous,
                        "One exact dependency has more than one declaration or host recipe.", "/bindings/process/dependencies");
                continue;
            }
            contributed.Add(reference, dependency);
            dependency.Contribute(receiver);
        }
        return receiver.Dependencies;
    }

    sealed class DependencyReceiver(ServiceDependencies dependencies) : IProcessDependencyReceiver
    {
        internal ServiceDependencies Dependencies { get; private set; } = dependencies;
        public void Transition<TEntity, TInput, TResult>(Transition<TEntity, TInput, TResult> transition) where TEntity : notnull =>
            Dependencies = Dependencies.Transition(transition);
        public void PointRead<TEntity, TInput, TResult>(EntityPointRead<TEntity, TInput, TResult> read)
            where TEntity : class where TInput : notnull where TResult : notnull =>
            Dependencies = Dependencies.Lookup(read.Query, read.Entity, read.Key, read.Result);
    }

    /// <summary>Registers canonical transition behavior, without choosing a repository manually.</summary>
    /// <typeparam name="TEntity">State projection.</typeparam><typeparam name="TInput">Command projection.</typeparam><typeparam name="TResult">Outcome projection.</typeparam>
    /// <param name="transition">Exact transition authority.</param>
    /// <param name="expectedConcurrencyTokenField">Optional input field carrying the required observed token.</param>
    /// <returns>A new immutable registration set.</returns>
    /// <exception cref="ArgumentNullException">Transition is null.</exception>
    /// <exception cref="ArgumentException">The exact dependency is already registered.</exception>
    public ServiceDependencies Transition<TEntity, TInput, TResult>(Transition<TEntity, TInput, TResult> transition,
        string? expectedConcurrencyTokenField = null) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        var prepared = new Lazy<Cohesive.Transitions.Compilation.CompiledTransitionPlan>(() =>
        {
            var result = transition.Compile();
            return result.Plan ?? throw new ServiceBindingValidationException(result.Validation);
        });
        return new(transitions.Add(transition.Reference, (resolve, contracts) =>
            new(prepared.Value, resolve(transition.Definition.Observation), contracts,
                expectedConcurrencyTokenField: expectedConcurrencyTokenField)), lookups, Queries);
    }

    /// <summary>Attaches a typed entity point read to an exact hosted-query contract.</summary>
    /// <typeparam name="TEntity">Entity projection.</typeparam><typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Query result.</typeparam>
    /// <param name="query">Canonical hosted query.</param><param name="entity">Authoritative entity.</param>
    /// <param name="key">Invocation-time entity identity projection.</param><param name="result">Projection of the entity or its absence.</param>
    /// <returns>A new immutable registration set.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The exact query is already registered.</exception>
    /// <exception cref="ServiceBindingValidationException">The query claims deterministic rather than observational semantics.</exception>
    public ServiceDependencies Lookup<TEntity, TInput, TResult>(HostedQuery<TInput, TResult> query,
        DomainEntity<TEntity> entity, Func<TInput, string> key, Func<TEntity?, TResult> result)
        where TEntity : class where TInput : notnull where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(result);
        if (query.EvaluationSemantics != Cohesive.Relations.IR.HostedQueryEvaluationSemantics.Observation)
            throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.LookupSemanticsInvalid,
                "An entity lookup cannot implement a deterministic-computation contract.", "/bindings/lookup");
        return new(transitions, lookups.Add(query.Reference, resolve =>
        {
            var repository = resolve(ValueContract.FromShape(entity.Definition.Shape));
            if (repository.EntityDefinition != entity.Definition)
                throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.ObservationMismatch,
                    "Lookup repository differs from its entity authority.", "/bindings/lookup/entity");
            var partition = repository.TransitionOperationCapabilities.PartitionKey
                ?? throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.LookupPartitionMissing,
                    "Entity lookup requires an explicit fixed repository partition.", "/bindings/lookup/partition");
            var typed = new TypedEntityRepository<TEntity>(repository);
            var options = new EntityReadOptions(partitionKey: partition);
            return (query.CreateProcessDefinitionLink(), ProcessRelationHandlerRegistration.Create(query,
                async (context, _, input) => result(await typed.TryGetEntity(context, key(input), options).ConfigureAwait(false))));
        }), Queries);
    }

    /// <summary>Selects a canonical relational query once for transport reading and infrastructure preparation.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Query output.</typeparam>
    /// <param name="query">Canonical query authority.</param><param name="revision">Explicit semantic revision.</param>
    /// <param name="maximumRows">Complete-result row bound.</param><param name="maximumBytes">Decoded-value byte bound.</param>
    /// <param name="consistency">Required acquisition guarantee; independent-source selection cannot claim a single snapshot.</param>
    /// <returns>A new immutable registration set.</returns>
    /// <exception cref="ArgumentException">The query is invalid or already selected.</exception>
    public ServiceDependencies Query<TInput, TResult>(RelationQuery<TInput, TResult> query, ExecutionRevisionId revision,
        int maximumRows, long maximumBytes, RelationQueryConsistency consistency = RelationQueryConsistency.SingleSnapshot)
    {
        ArgumentNullException.ThrowIfNull(query);
        var reference = ServiceQueryBinding.GetReference(revision, query.CompilationRequest);
        if (Queries.Any(item => item.Reference.DefinitionId == reference.DefinitionId))
            throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.QueryDuplicate, "A query is already selected.", "/bindings/queries");
        return new(transitions, lookups, Queries.Add(new(reference, query.CompilationRequest, maximumRows, maximumBytes, consistency)));
    }

    // Shared preparation slots retain successful projections only for the definition's lifetime.
    static readonly WeakPreparationCache<ProcessDefinition, RequiredDependencies> requiredByDefinition = new();
    static RequiredDependencies Required(ProcessDefinition definition) =>
        requiredByDefinition.Get(definition, static value => new RequiredDependencies(value));

    sealed class RequiredDependencies
    {
        internal ImmutableArray<ExecutionDefinitionReference> Transitions { get; }
        internal ImmutableArray<ExecutionDefinitionReference> Queries { get; }
        internal RequiredDependencies(ProcessDefinition definition)
        {
            var transitions = ImmutableHashSet.CreateBuilder<ExecutionDefinitionReference>();
            var queries = ImmutableHashSet.CreateBuilder<ExecutionDefinitionReference>();
            foreach (var node in definition.Nodes)
            {
                if (node is InvokeTransitionProcessNode transition) transitions.Add(transition.Transition);
                if (node is EvaluateRelationProcessNode query) queries.Add(query.Relation);
            }
            var order = Comparer<ExecutionDefinitionReference>.Create(ExecutionDefinitionReference.CompareCanonical);
            Transitions = transitions.OrderBy(reference => reference, order).ToImmutableArray();
            Queries = queries.OrderBy(reference => reference, order).ToImmutableArray();
        }
    }

    void RequireDependencies(RequiredDependencies required)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        Collect(required.Transitions, transitions.ContainsKey, ServiceBindingDiagnosticCodes.TransitionMissing, "transitions");
        Collect(required.Queries, lookups.ContainsKey, ServiceBindingDiagnosticCodes.QueryMissing, "queries");
        if (diagnostics.Count != 0) throw new ServiceBindingValidationException(new(diagnostics.ToImmutable()));

        void Collect(IEnumerable<ExecutionDefinitionReference> references, Func<ExecutionDefinitionReference, bool> registered,
            string code, string kind)
        {
            foreach (var reference in references)
            {
                if (registered(reference)) continue;
                var exact = $"{reference.DefinitionId.Value}@{reference.RevisionId.Value} [{reference.Fingerprint.Algorithm}/{reference.Fingerprint.Canonicalization}:{reference.Fingerprint.Value}]";
                diagnostics.Add(new(code, DiagnosticSeverity.Error, $"Missing exact dependency: {exact}.",
                    $"/bindings/{kind}/{Uri.EscapeDataString(exact)}"));
            }
        }
    }

    internal void Apply<TInput, TResult>(ServiceProcessHostBuilder<TInput, TResult> host, Process<TInput, TResult> process,
        Func<ValueContract, IEntityRepository> resolve, InteractionContractCatalog contracts)
    {
        var required = Required(process.Definition);
        RequireDependencies(required);
        foreach (var reference in required.Transitions)
        {
            host.Transition(transitions[reference](resolve, contracts));
        }
        foreach (var reference in required.Queries)
        {
            var binding = lookups[reference](resolve);
            host.Query(binding.Link, binding.Handler);
        }
    }
}

/// <summary>One selected relational query and explicit execution bounds; no runtime data source is retained.</summary>
public sealed class ServiceQuerySelection
{
    internal ServiceQuerySelection(ExecutionDefinitionReference reference, RelationQueryCompilationRequest request, int maximumRows, long maximumBytes, RelationQueryConsistency consistency)
    {
        if (!Enum.IsDefined(consistency)) throw new ArgumentOutOfRangeException(nameof(consistency));
        if (!Enum.IsDefined(consistency)) throw new ArgumentOutOfRangeException(nameof(consistency));
        Consistency = consistency;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        Reference = reference; Request = request; MaximumRows = maximumRows; MaximumBytes = maximumBytes;
    }
    /// <summary>Selected acquisition guarantee.</summary>
    public RelationQueryConsistency Consistency { get; }
    /// <summary>Canonical query identity, explicit revision and definition fingerprint.</summary>
    public ExecutionDefinitionReference Reference { get; }
    /// <summary>Exact semantic request.</summary>
    public RelationQueryCompilationRequest Request { get; }
    /// <summary>Complete-result row bound.</summary>
    public int MaximumRows { get; }
    /// <summary>Decoded-value byte bound.</summary>
    public long MaximumBytes { get; }
}
