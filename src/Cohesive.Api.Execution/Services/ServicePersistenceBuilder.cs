using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage;

namespace Cohesive.Api.Execution.Services;

/// <summary>Selects explicit repository and query implementations over an optional default persistence.</summary>
/// <remarks>Build once per host. Registrations retain caller-owned backends; no IO or disposal is performed.
/// Exact query instance and observation contracts select overrides, never CLR names or registration order.</remarks>
public sealed class ServicePersistenceBuilder
{
    readonly Dictionary<ValueContract, IEntityRepository> repositories = [];
    readonly Dictionary<object, Func<int, long, object>> queries = new(ReferenceEqualityComparer.Instance);
    readonly AuthoringState<IServicePersistence> state;

    /// <summary>Starts a composition with a fallback for bindings not explicitly selected here.</summary>
    /// <param name="fallback">Existing native persistence, or null for an exhaustive explicit composition.</param>
    public ServicePersistenceBuilder(IServicePersistence? fallback = null) =>
        state = new(() => new Persistence(new(repositories), new(queries, ReferenceEqualityComparer.Instance), fallback));

    /// <summary>Selects a repository for its exact observation contract.</summary>
    /// <param name="repository">Caller-owned repository, including native receipt capabilities.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The repository is null.</exception>
    /// <exception cref="ArgumentException">A contract is already registered.</exception>
    /// <exception cref="InvalidOperationException">Build has frozen authoring.</exception>
    public ServicePersistenceBuilder Repository(IEntityRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return state.Edit(() => { repositories.Add(ValueContract.FromShape(repository.EntityDefinition.Shape), repository); return this; });
    }

    /// <summary>Selects a prepared query factory, cached per exact query and requested bounds.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Query result.</typeparam>
    /// <param name="query">Exact shared logical declaration.</param>
    /// <param name="prepare">Offline preparation honoring the requested row and byte limits.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The query or factory is null.</exception>
    /// <exception cref="ArgumentException">The query already has a selection.</exception>
    /// <exception cref="InvalidOperationException">Build has frozen authoring.</exception>
    public ServicePersistenceBuilder Query<TInput, TResult>(RelationQuery<TInput, TResult> query,
        Func<int, long, IRelationQueryReader<TInput, TResult>> prepare)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(prepare);
        return state.Edit(() =>
        {
            queries.Add(query, (rows, bytes) =>
            {
                var reader = prepare(rows, bytes);
                if (reader is null || !ReferenceEquals(reader.Definition, query))
                    throw new InvalidOperationException("Prepared query must retain its exact registered declaration.");
                return reader;
            });
            return this;
        });
    }

    /// <summary>Freezes selections and returns one host-owned resolver.</summary>
    /// <returns>Resolver sharing prepared queries and caller-owned repositories.</returns>
    public IServicePersistence Build() => state.Value;

    sealed class Persistence(Dictionary<ValueContract, IEntityRepository> repositories,
        Dictionary<object, Func<int, long, object>> queries, IServicePersistence? fallback) : IServicePersistence
    {
        readonly Dictionary<object, Dictionary<(int, long), Lazy<object>>> prepared = new(ReferenceEqualityComparer.Instance);
        public IEntityRepository Repository(ValueContract observation) => repositories.TryGetValue(observation, out var repository)
            ? repository : fallback?.Repository(observation) ?? throw new InvalidOperationException("No repository is selected for the observation contract.");
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query, int maximumRows, long maximumBytes, RelationQueryConsistency consistency)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            if (!queries.TryGetValue(query, out var factory))
                return ServiceQueryConsistencyValidation.Require(fallback?.Query(query, maximumRows, maximumBytes, consistency)
                    ?? throw new InvalidOperationException("No persistence is selected for the query declaration."), consistency);
            Lazy<object> value;
            lock (prepared)
            {
                if (!prepared.TryGetValue(query, out var bounds)) prepared.Add(query, bounds = []);
                if (!bounds.TryGetValue((maximumRows, maximumBytes), out value!))
                    bounds.Add((maximumRows, maximumBytes), value = new(() => factory(maximumRows, maximumBytes)));
            }
            return ServiceQueryConsistencyValidation.Require((IRelationQueryReader<TInput, TResult>)value.Value, consistency);
        }
    }
}
