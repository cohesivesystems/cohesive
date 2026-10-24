using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Storage;

namespace Cohesive.Api.Execution.Services;

/// <summary>Routes endpoint selections by canonical entity placement before any native preparation.</summary>
public sealed class ServiceStorageComposition : IServiceStorageComposition
{
    readonly ImmutableArray<(string Name, IServiceStorageComposition Storage)> sources;
    readonly Dictionary<ValueContract, int> repositories = [];
    readonly Dictionary<RelationQueryPlanComponentFingerprint, int> queries = [];
    ImmutableArray<IServiceStorageComposition> prepared;
    ServiceStorageExplanation? explanation;
    /// <summary>Offline admission evidence, available only after successful validation. This is not native readiness.</summary>
    /// <exception cref="InvalidOperationException">Admission has not succeeded.</exception>
    public ServiceStorageExplanation Explanation => explanation ?? throw new InvalidOperationException("Validate endpoint selections before explaining storage.");

    /// <summary>Creates an offline ownership scope; repeated composition instances are shared.</summary>
    /// <param name="compositions">Storage declarations, independent of API selections.</param>
    public ServiceStorageComposition(IEnumerable<IServiceStorageComposition> compositions)
        : this(compositions.Select(source => (source.GetType().FullName!, source))) { }

    /// <summary>Creates a named scope for attributable ownership diagnostics.</summary>
    /// <param name="compositions">Contributing model names and their storage declarations, in explicit preparation order.</param>
    public ServiceStorageComposition(IEnumerable<(string Name, IServiceStorageComposition Storage)> compositions)
    {
        ArgumentNullException.ThrowIfNull(compositions);
        sources = [.. compositions.DistinctBy(item => item.Storage, ReferenceEqualityComparer.Instance)];
        Observations = [.. sources.SelectMany(source => source.Storage.Observations).Distinct()];
        Shapes = [.. sources.SelectMany(source => source.Storage.Shapes).Distinct()];
        Bindings = [.. sources.SelectMany(source => source.Storage.Bindings).Distinct(ReferenceEqualityComparer.Instance)];
    }
    /// <inheritdoc />
    public ImmutableArray<ValueContract> Observations { get; }
    /// <inheritdoc />
    public ImmutableArray<QualifiedShapeId> Shapes { get; }
    /// <inheritdoc />
    public ImmutableArray<object> Bindings { get; }
    /// <inheritdoc />
    public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> selections)
    {
        var selected = new ServiceStorageComposition(sources);
        selected.Admit([], selections);
        return selected;
    }
    /// <summary>Aggregates ownership failures, then prepares all selected queries offline. No native clients are accessed.</summary>
    /// <param name="plans">Complete endpoint plans from independent API modules.</param>
    public void Validate(IEnumerable<ServiceImplementationPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var selected = plans.ToArray();
        Admit(selected.SelectMany(plan => plan.RepositoryRequirements),
            selected.SelectMany(plan => plan.QueryDependencies.Queries).ToImmutableArray());
    }
    void Admit(IEnumerable<ValueContract> required, ImmutableArray<ServiceQuerySelection> selections)
    {
        if (!prepared.IsDefault) throw new InvalidOperationException("Storage admission is already complete.");
        List<DocumentValidationDiagnostic> diagnostics = [];
        var repositoryOwners = new Dictionary<ValueContract, List<int>>();
        var bindingOwners = new Dictionary<object, List<int>>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < sources.Length; index++)
        {
            foreach (var contract in sources[index].Storage.Observations.Distinct())
            {
                if (!repositoryOwners.TryGetValue(contract, out var owners)) repositoryOwners.Add(contract, owners = []);
                owners.Add(index);
            }
            foreach (var binding in sources[index].Storage.Bindings.Distinct(ReferenceEqualityComparer.Instance))
            {
                if (!bindingOwners.TryGetValue(binding, out var owners)) bindingOwners.Add(binding, owners = []);
                owners.Add(index);
            }
        }
        string Names(IEnumerable<int> owners) => string.Join(", ", owners.Select(index => sources[index].Name));
        void Error(string code, string message) => diagnostics.Add(new("services.storage." + code, DiagnosticSeverity.Error, message, "/services/storage"));
        foreach (var (contract, owners) in repositoryOwners.Where(pair => pair.Value.Count > 1))
            Error("ambiguousRepository", $"Observation '{contract}' is supplied by competing models: {Names(owners)}.");
        foreach (var (binding, owners) in bindingOwners.Where(pair => pair.Value.Count > 1))
            Error("ambiguousResource", $"Native binding '{binding.GetType().FullName}' is reused by competing models: {Names(owners)}. Share one composition.");
        foreach (var contract in required.Distinct().Where(contract => !repositoryOwners.ContainsKey(contract)))
            Error("repositoryMissing", $"No storage model supplies observation '{contract}'. Candidates: {Names(Enumerable.Range(0, sources.Length))}.");

        var explainedQueries = new List<ServiceQueryPlacement>();
        var routed = sources.Select(_ => new List<ServiceQuerySelection>()).ToArray();
        var queryOwners = new Dictionary<RelationQueryPlanComponentFingerprint, int>();
        var identities = new Dictionary<RelationQueryPlanComponentFingerprint, ServiceQuerySelection>();
        var revisions = new Dictionary<(string Id, string Revision), RelationQueryPlanComponentFingerprint>();
        foreach (var selection in selections)
        {
            var compilation = RelationQueryStaticCompiler.Compile(selection.Request);
            if (!compilation.IsSuccessful)
            {
                diagnostics.AddRange(compilation.Diagnostics);
                continue;
            }
            var planReference = RelationQueryCompiledPlanReference.From(compilation.Plan!);
            var key = RelationQueryCompiledPlanReferenceFingerprinter.Compute(planReference);
            var revision = (selection.Reference.DefinitionId.Value, selection.Reference.RevisionId.Value);
            if (revisions.TryGetValue(revision, out var priorKey) && priorKey != key)
            {
                Error("querySelectionMismatch", $"Query '{revision.Item1}@{revision.Item2}' names different canonical compilation inputs across modules.");
                continue;
            }
            revisions[revision] = key;
            if (identities.TryGetValue(key, out var previous))
            {
                if (previous.Reference != selection.Reference || previous.MaximumRows != selection.MaximumRows
                    || previous.MaximumBytes != selection.MaximumBytes || previous.Consistency != selection.Consistency)
                    Error("querySelectionMismatch", $"Query '{selection.Reference.DefinitionId}' has conflicting revisions, bounds or consistency across modules.");
                continue;
            }
            identities.Add(key, selection);
            var inputs = compilation.Plan!.InputContract;
            var shapes = inputs.Sources.Select(source => source.Shape).Concat(inputs.Traversals.Select(source => source.ResultShape)).Distinct().ToArray();
            var owners = Enumerable.Range(0, sources.Length).Where(index => shapes.Length > 0 && shapes.All(sources[index].Storage.Shapes.Contains)).ToArray();
            if (owners.Length != 1)
            {
                Error(owners.Length == 0 ? "queryMissing" : "ambiguousQuery",
                    $"Query '{selection.Reference.DefinitionId}' sources [{string.Join(", ", shapes.AsEnumerable())}] require one complete placement; " +
                    $"models: {Names(owners.Length == 0 ? Enumerable.Range(0, sources.Length) : owners)}. Cross-store reads require an explicit composed storage declaration.");
                continue;
            }
            queryOwners.Add(key, owners[0]); routed[owners[0]].Add(selection);
            explainedQueries.Add(new(selection.Reference, planReference,
                [.. shapes.OrderBy(shape => shape.ToString(), StringComparer.Ordinal)], sources[owners[0]].Name,
                selection.Consistency, selection.MaximumRows, selection.MaximumBytes));
        }
        if (diagnostics.Count > 0) throw new ServiceBindingValidationException(new([.. diagnostics]));
        // Provider planning, including consistency validation, is still offline and must succeed everywhere before IO.
        var admitted = ImmutableArray.CreateBuilder<IServiceStorageComposition>(sources.Length);
        for (var index = 0; index < sources.Length; index++)
        {
            try { admitted.Add(sources[index].Storage.PrepareQueries([.. routed[index]])); }
            catch (ServiceBindingValidationException error)
            {
                diagnostics.AddRange(error.Validation.Diagnostics);
            }
        }
        if (diagnostics.Count > 0) throw new ServiceBindingValidationException(new([.. diagnostics]));
        foreach (var (contract, owners) in repositoryOwners) repositories.Add(contract, owners[0]);
        foreach (var entry in queryOwners) queries.Add(entry.Key, entry.Value);
        prepared = admitted.ToImmutable();
        explanation = new(
            [.. explainedQueries.OrderBy(query => query.Query.DefinitionId.Value, StringComparer.Ordinal)],
            [.. repositoryOwners.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                .Select(pair => new ServiceRepositoryPlacement(pair.Key, sources[pair.Value[0]].Name))]);
    }
    static RelationQueryPlanComponentFingerprint Key(RelationQueryCompilationResult compilation) =>
        RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(compilation.Plan!));
    static RelationQueryPlanComponentFingerprint Key(RelationQueryCompilationRequest request)
    {
        var compiled = RelationQueryStaticCompiler.Compile(request);
        if (!compiled.IsSuccessful) throw new RelationQueryPreparationException("storage ownership", compiled);
        return Key(compiled);
    }
    /// <inheritdoc />
    public async ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (prepared.IsDefault) throw new InvalidOperationException("Validate endpoint selections before native preparation.");
        var runtimes = new IServicePersistence[prepared.Length];
        for (var index = 0; index < prepared.Length; index++)
            runtimes[index] = await prepared[index].BindAsync(services, cancellationToken).ConfigureAwait(false);
        return new Persistence(this, runtimes);
    }
    static ServiceBindingValidationException Failure(string code, string message) =>
        new(new([new(code, DiagnosticSeverity.Error, message, "/services/storage")]));

    sealed class Persistence(ServiceStorageComposition owner, IServicePersistence[] sources) : IServicePersistence
    {
        public IEntityRepository Repository(ValueContract observation) => owner.repositories.TryGetValue(observation, out var index)
            ? sources[index].Repository(observation)
            : throw Failure("services.storage.repositoryMissing", $"No admitted storage supplies observation '{observation}'.");
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query,
            int maximumRows, long maximumBytes, RelationQueryConsistency consistency) =>
            owner.queries.TryGetValue(Key(query.CompilationRequest), out var index)
                ? sources[index].Query(query, maximumRows, maximumBytes, consistency)
                : throw Failure("services.storage.queryMissing", "The query has no admitted storage placement.");
    }
}
