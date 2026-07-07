using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Queries;

/// <summary>
/// Point read repository used by the query execution engine.
/// </summary>
public interface IReadRepository
{
    /// <summary>
    /// Reads observations by id.
    /// </summary>
    Task<IReadOnlyDictionary<string, Observation>> GetByIds(OperationContext context, IReadOnlyCollection<string> ids, FieldSelection? options = null);
}

/// <summary>
/// A read repository that also supports structured queries.
/// </summary>
public interface IQueryRepository : IReadRepository
{
    /// <summary>
    /// Optional advertised query capabilities for diagnostics and future planner use.
    /// </summary>
    QueryCapabilitySet? Capabilities { get; }

    /// <summary>
    /// Executes a structured observation query and returns rows, pagination metadata, and optional aggregations.
    /// </summary>
    Task<EntityQueryResponse> Query(OperationContext context, EntityQuery query);
}

/// <summary>
/// In-memory observation repository used by tests and lightweight experiments.
/// </summary>
public sealed class InMemoryReadRepository : IQueryRepository
{
    readonly Dictionary<string, Observation> recordsById;

    /// <inheritdoc />
    public QueryCapabilitySet? Capabilities { get; } = new(
        QueryCapability.Equality
        | QueryCapability.Prefix
        | QueryCapability.Suffix
        | QueryCapability.Contains
        | QueryCapability.FullText
        | QueryCapability.Exists
        | QueryCapability.NumberRange
        | QueryCapability.DateRange
        | QueryCapability.SetMembership
        | QueryCapability.NestedAny
        | QueryCapability.GeoDistance
        | QueryCapability.ScopedFields
        | QueryCapability.Negation
        | QueryCapability.Aggregation
        | QueryCapability.CaseInsensitiveStringComparison
        );

    public InMemoryReadRepository(IEnumerable<Observation> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        recordsById = records.ToDictionary(static record => record.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates an in-memory repository from CLR records using observation mapping conventions.
    /// </summary>
    public static InMemoryReadRepository From<TRecord>(IEnumerable<TRecord> records, Func<TRecord, string> idSelector, ShapeId? schemaId = null, ShapeMappingContext? mappingContext = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(idSelector);
        var mapping = mappingContext ?? ShapeMappingContext.Default;
        var schema = schemaId ?? new ShapeId(typeof(TRecord).Name);
        return new(records.Select(record => mapping.Map(
            record,
            schema,
            new() { Id = Guard.RequireNotNullOrWhiteSpace(idSelector(record)) })
            )
        );
    }

    /// <summary>
    /// Creates an in-memory repository from CLR records using default id conventions.
    /// </summary>
    public static InMemoryReadRepository From<TRecord>(IEnumerable<TRecord> records, ShapeId? schemaId = null, ShapeMappingContext? mappingContext = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var mapping = mappingContext ?? ShapeMappingContext.Default;
        var schema = schemaId ?? new ShapeId(typeof(TRecord).Name);
        return new(records.Select(record => mapping.Map(record, schema)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, Observation>> GetByIds(OperationContext context, IReadOnlyCollection<string> ids, FieldSelection? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ids);
        context.ThrowIfCancellationRequested();

        Dictionary<string, Observation> result = new(StringComparer.Ordinal);
        foreach (var id in ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            if (recordsById.TryGetValue(id, out var record))
                result[id] = Project(record, options);
        }

        return Task.FromResult<IReadOnlyDictionary<string, Observation>>(result);
    }

    /// <inheritdoc />
    public Task<EntityQueryResponse> Query(OperationContext context, EntityQuery query)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        context.ThrowIfCancellationRequested();

        var filtered = recordsById.Values
            .Where(record => query.Predicate is null || EntityPredicateEvaluator.Evaluate(record, query.Predicate))
            .ToArray();

        IReadOnlyList<Observation> rows = [];
        QueryPageInfo? pageInfo = null;
        if (query.IncludeRows)
        {
            rows = [.. ApplyWindow(filtered, query.Window).Select(record => Project(record, query.Fields))];
            pageInfo = CreatePageInfo(query.Window, filtered.Length, rows.Count);
        }

        var aggregations = query.Aggregations is null
            ? null
            : AggregationPlanEvaluator.Evaluate(recordsById.Values, new(query.Aggregations.Roots, query.Predicate));

        return Task.FromResult(new EntityQueryResponse(rows, pageInfo, aggregations));
    }

    static IEnumerable<Observation> ApplyWindow(IEnumerable<Observation> records, ResultPageOptions? window)
    {
        var results = records;

        if (window?.OrderBy is { Length: > 0 } orderBy)
        {
            IOrderedEnumerable<Observation>? ordered = null;
            foreach (var order in orderBy)
            {
                ordered = ordered is null
                    ? ApplyPrimaryOrdering(results, order)
                    : ApplySecondaryOrdering(ordered, order);
            }

            results = ordered ?? results;
        }

        if (window?.Cursor is not null)
            throw new NotSupportedException("In-memory read repositories do not yet support cursor page resumption.");

        if (window?.EffectiveMode == ResultPaginationMode.Offset && window.Offset is { } offset and > 0)
            results = results.Skip(offset);

        if (window?.Limit is { } limit)
        {
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(window), limit, "Read-query limit must be non-negative.");

            results = results.Take(limit);
        }

        return results;
    }

    static QueryPageInfo CreatePageInfo(ResultPageOptions? window, int totalCount, int returnedCount)
    {
        var offset = window?.EffectiveMode == ResultPaginationMode.Offset ? window.Offset ?? 0 : 0;
        var limit = window?.Limit;
        var hasMore = limit is not null && offset + returnedCount < totalCount;

        return new(
            TotalCount: totalCount,
            NextCursor: null,
            Offset: window?.EffectiveMode == ResultPaginationMode.Offset ? offset : null,
            Limit: limit,
            HasMore: hasMore);
    }

    static IOrderedEnumerable<Observation> ApplyPrimaryOrdering(IEnumerable<Observation> source, QueryOrderBy order) =>
        order.Descending
            ? source.OrderByDescending(record => record, ObservationOrderingComparer.ForField(order.Path))
            : source.OrderBy(record => record, ObservationOrderingComparer.ForField(order.Path));

    static IOrderedEnumerable<Observation> ApplySecondaryOrdering(IOrderedEnumerable<Observation> source, QueryOrderBy order) =>
        order.Descending
            ? source.ThenByDescending(record => record, ObservationOrderingComparer.ForField(order.Path))
            : source.ThenBy(record => record, ObservationOrderingComparer.ForField(order.Path));

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
            lineage: observation.Lineage
            );
    }

    sealed class ObservationOrderingComparer(FieldPath path) : IComparer<Observation>
    {
        public int Compare(Observation? x, Observation? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            return EntityPredicateEvaluator.CompareFieldValues(x, y, path);
        }

        public static ObservationOrderingComparer ForField(FieldPath path) => new(path);
    }
}
