using Cohesive.Relations.Model;
using Cohesive.Relations.Queries;

namespace Cohesive.Storage;

/// <summary>
/// Adapts observation repositories with structured query support to the relational query-read contract.
/// </summary>
public sealed class ObservationQueryReadRepositoryAdapter : IQueryRepository
{
    readonly ObservationReadRepositoryAdapter pointReadAdapter;
    readonly IEntityQueryRepository queryRepository;

    /// <summary>
    /// Creates an adapter over a repository that implements both point reads and queries.
    /// </summary>
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
    public ObservationQueryReadRepositoryAdapter(
        IEntityOutboxRepository repository,
        QueryCapabilitySet? capabilities = null)
        : this(
            repository,
            repository as IEntityQueryRepository
            ?? throw new InvalidOperationException(
                $"Repository '{repository.GetType().Name}' does not implement '{nameof(IEntityQueryRepository)}'."),
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
}
