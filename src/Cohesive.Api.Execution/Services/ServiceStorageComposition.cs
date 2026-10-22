using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Storage;

namespace Cohesive.Api.Execution.Services;

/// <summary>Combines explicit storage ownership without probing providers or selecting by registration order.</summary>
public sealed class ServiceStorageComposition : IServiceStorageComposition
{
    readonly ImmutableArray<IServiceStorageComposition> sources;
    readonly Dictionary<ValueContract, int> repositories = [];
    readonly Dictionary<RelationQueryCompilationRequest, int> queries = new(ReferenceEqualityComparer.Instance);
    /// <summary>Validates unique ownership before native preparation.</summary>
    /// <param name="compositions">Model-owned storage compositions; repeated instances are shared.</param>
    /// <exception cref="ServiceBindingValidationException">Different compositions claim the same entity or query.</exception>
    public ServiceStorageComposition(IEnumerable<IServiceStorageComposition> compositions)
    {
        ArgumentNullException.ThrowIfNull(compositions);
        sources = [.. compositions.Distinct<IServiceStorageComposition>(ReferenceEqualityComparer.Instance)];
        var nativeBindings = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < sources.Length; index++)
        {
            foreach (var binding in sources[index].Bindings)
                if (!nativeBindings.Add(binding))
                    throw Error("ambiguousResource", "Different storage compositions reuse one native binding; share the same composition instead.");
            foreach (var observation in sources[index].Observations)
                if (!repositories.TryAdd(observation, index))
                    throw Error("ambiguousRepository", "More than one storage contribution supplies an entity observation.");
            foreach (var query in sources[index].Queries.Distinct<RelationQueryCompilationRequest>(ReferenceEqualityComparer.Instance))
                if (!queries.TryAdd(query, index))
                    throw Error("ambiguousQuery", "More than one storage contribution supplies a query.");
        }
        Observations = [.. repositories.Keys];
        Queries = [.. queries.Keys];
        Bindings = [.. sources.SelectMany(source => source.Bindings).Distinct(ReferenceEqualityComparer.Instance)];
    }
    /// <inheritdoc />
    public ImmutableArray<ValueContract> Observations { get; }
    /// <inheritdoc />
    public ImmutableArray<RelationQueryCompilationRequest> Queries { get; }
    /// <inheritdoc />
    public ImmutableArray<object> Bindings { get; }
    /// <summary>Checks endpoint query ownership without accessing native resources.</summary>
    /// <param name="plans">Complete endpoint implementation plans.</param>
    public void Validate(IEnumerable<ServiceImplementationPlan> plans)
    {
        var selected = plans.ToArray();
        foreach (var observation in selected.SelectMany(plan => plan.RepositoryRequirements))
            if (!repositories.ContainsKey(observation))
                throw Error("repositoryMissing", "No storage contribution supplies a required entity observation.");
        foreach (var selection in selected.SelectMany(plan => plan.QueryDependencies.Queries))
            if (!queries.ContainsKey(selection.Request))
                throw Error("queryMissing", $"No storage contribution supplies query '{selection.Reference.DefinitionId}'.");
    }
    /// <inheritdoc />
    public async ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var prepared = new IServicePersistence[sources.Length];
        for (var index = 0; index < sources.Length; index++)
            prepared[index] = await sources[index].BindAsync(services, cancellationToken).ConfigureAwait(false);
        return new Persistence(this, prepared);
    }
    static ServiceBindingValidationException Error(string code, string message) =>
        new(new([new("services.storage." + code, DiagnosticSeverity.Error, message, "/services/storage")]));
    sealed class Persistence(ServiceStorageComposition owner, IServicePersistence[] sources) : IServicePersistence
    {
        public IEntityRepository Repository(ValueContract observation) => owner.repositories.TryGetValue(observation, out var index)
            ? sources[index].Repository(observation) : throw Error("repositoryMissing", "No storage contribution supplies the requested entity observation.");
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query,
            int maximumRows, long maximumBytes, RelationQueryConsistency consistency) => owner.queries.TryGetValue(query.CompilationRequest, out var index)
            ? sources[index].Query(query, maximumRows, maximumBytes, consistency)
            : throw Error("queryMissing", "No storage contribution supplies the requested query.");
    }
}
