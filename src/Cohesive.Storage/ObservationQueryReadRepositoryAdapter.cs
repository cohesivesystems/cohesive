using Cohesive.Relations.Model;
using Cohesive.Relations.Queries;

namespace Cohesive.Storage;

/// <summary>
/// Adapts the temporary Cosmos-compatible entity query facade to the legacy relational query-read contract.
/// </summary>
/// <remarks>
/// Retained only until the Cosmos entity repository and its compatibility tests migrate to canonical source readers
/// and <see cref="Cohesive.Relations.Execution.IRelationQueryEvaluator"/>.
/// </remarks>
public sealed class ObservationQueryReadRepositoryAdapter : IQueryRepository
{
    readonly ObservationReadRepositoryAdapter pointReadAdapter;
    readonly IEntityQueryRepository queryRepository;

    /// <summary>
    /// Creates an adapter over a repository that implements both point reads and queries.
    /// </summary>
    /// <param name="repository">Legacy point-read and query repository to adapt.</param>
    /// <param name="capabilities">Optional legacy query capability declaration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="repository"/> is <see langword="null"/>.</exception>
    public ObservationQueryReadRepositoryAdapter(
        IEntityQueryRepository repository,
        QueryCapabilitySet? capabilities = null
        )
        : this(repository, repository, capabilities)
    {
    }

    /// <summary>
    /// Creates an adapter over separate point-read and query repositories for one observation source.
    /// </summary>
    /// <param name="repository">Point-read entity repository to adapt.</param>
    /// <param name="queryRepository">Legacy query repository to adapt.</param>
    /// <param name="capabilities">Optional legacy query capability declaration.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="repository"/> or <paramref name="queryRepository"/> is <see langword="null"/>.
    /// </exception>
    public ObservationQueryReadRepositoryAdapter(
        IEntityRepository repository,
        IEntityQueryRepository queryRepository,
        QueryCapabilitySet? capabilities = null
        )
    {
        pointReadAdapter = new(repository);
        this.queryRepository = Guard.RequireNotNull(queryRepository);
        Capabilities = capabilities;
    }

    /// <summary>
    /// Creates an adapter over a repository that implements both point reads and queries.
    /// </summary>
    /// <param name="repository">Outbox repository that must also implement the legacy query contract.</param>
    /// <param name="capabilities">Optional legacy query capability declaration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="repository"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="repository"/> does not implement <see cref="IEntityQueryRepository"/>.
    /// </exception>
    public ObservationQueryReadRepositoryAdapter(
        IEntityOutboxRepository repository,
        QueryCapabilitySet? capabilities = null)
        : this(
            repository,
            RequireQueryRepository(repository),
            capabilities)
    {
    }

    /// <inheritdoc />
    public QueryCapabilitySet? Capabilities { get; }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, Observation>> GetByIds(
        OperationContext context,
        IReadOnlyCollection<string> ids,
        FieldSelection? options = null) =>
        pointReadAdapter.GetByIds(context, ids, options);

    /// <inheritdoc />
    public async Task<EntityQueryResponse> Query(OperationContext context, EntityQuery query)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        context.ThrowIfCancellationRequested();

        var response = await queryRepository.Query(context, query).ConfigureAwait(false);
        return new(
            Rows: [.. response.Rows.Select(snapshot => Project(snapshot.Entity, query.Fields))],
            PageInfo: response.PageInfo,
            Aggregations: response.Aggregations);
    }

    static Observation Project(Observation observation, FieldSelection? read)
    {
        if (read?.Fields is null || read.Fields.Count == 0)
            return observation;

        Dictionary<string, ObservationValue> fields = new(StringComparer.Ordinal);
        foreach (var fieldName in read.Fields)
        {
            if (observation.TryGetField(fieldName, out var value))
                fields[fieldName] = value;
        }

        return new(
            shapeId: observation.ShapeId,
            id: observation.Id,
            fields: fields,
            version: observation.Version,
            lineage: observation.Lineage);
    }

    static IEntityQueryRepository RequireQueryRepository(IEntityOutboxRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return repository as IEntityQueryRepository
            ?? throw new InvalidOperationException(
                $"Repository '{repository.GetType().Name}' does not implement '{nameof(IEntityQueryRepository)}'.");
    }
}
