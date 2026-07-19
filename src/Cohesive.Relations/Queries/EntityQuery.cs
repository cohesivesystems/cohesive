using Cohesive.Relations.Model;

namespace Cohesive.Relations.Queries;

/// <summary>
/// Structured query request over entity observations.
/// </summary>
/// <remarks>
/// Queries may request rows, aggregation roots, or both. Row projection remains modeled directly
/// on the query for source compatibility with earlier row-only query callers.
/// </remarks>
public sealed record EntityQuery
{
    /// <summary>
    /// Creates a row query.
    /// </summary>
    /// <param name="Predicate">Optional predicate constraining rows and aggregation inputs.</param>
    /// <param name="Fields">Optional row field selection.</param>
    /// <param name="Window">Optional row pagination and ordering controls.</param>
    public EntityQuery(
        EntityPredicate? Predicate,
        FieldSelection? Fields = null,
        ResultPageOptions? Window = null
        )
    {
        this.Predicate = Predicate;
        this.Fields = Fields;
        this.Window = Window;
        IncludeRows = true;
    }

    EntityQuery(
        EntityPredicate? predicate,
        bool includeRows,
        FieldSelection? fields,
        ResultPageOptions? window,
        EntityAggregationQuery? aggregations
        )
    {
        Predicate = predicate;
        IncludeRows = includeRows;
        Fields = fields;
        Window = window;
        Aggregations = aggregations;
    }

    /// <summary>
    /// Optional predicate constraining rows and aggregation inputs.
    /// </summary>
    public EntityPredicate? Predicate { get; init; }

    /// <summary>
    /// Indicates whether row results should be returned.
    /// </summary>
    public bool IncludeRows { get; init; }

    /// <summary>
    /// Optional row field selection.
    /// </summary>
    public FieldSelection? Fields { get; init; }

    /// <summary>
    /// Optional row pagination and ordering controls.
    /// </summary>
    public ResultPageOptions? Window { get; init; }

    /// <summary>
    /// Optional aggregation query requested alongside or instead of rows.
    /// </summary>
    public EntityAggregationQuery? Aggregations { get; init; }

    /// <summary>
    /// Row query descriptor, or <see langword="null" /> when this query does not request rows.
    /// </summary>
    public EntityRowQuery? Rows => IncludeRows ? new(Fields, Window) : null;

    /// <summary>
    /// Creates a row query.
    /// </summary>
    public static EntityQuery ForRows(
        EntityPredicate? predicate,
        FieldSelection? fields = null,
        ResultPageOptions? window = null
        ) => new(predicate, includeRows: true, fields, window, aggregations: null);

    /// <summary>
    /// Creates an aggregation-only query.
    /// </summary>
    public static EntityQuery ForAggregations(
        EntityPredicate? predicate,
        IReadOnlyList<AggregationRoot> roots
        ) => new(predicate, includeRows: false, fields: null, window: null, aggregations: new(roots));

    /// <summary>
    /// Creates a query that returns both rows and aggregations from the same predicate scope.
    /// </summary>
    public static EntityQuery ForRowsAndAggregations(
        EntityPredicate? predicate,
        EntityRowQuery rows,
        EntityAggregationQuery aggregations
        )
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(aggregations);
        return new(predicate, includeRows: true, rows.Fields, rows.Window, aggregations);
    }

    /// <summary>
    /// Creates an aggregation-only query from a standalone aggregation plan.
    /// </summary>
    public static EntityQuery FromAggregationPlan(AggregationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ForAggregations(plan.Predicate, plan.Roots);
    }
}

/// <summary>
/// Row query controls within an <see cref="EntityQuery" />.
/// </summary>
/// <param name="Fields">Optional field selection.</param>
/// <param name="Window">Optional pagination and ordering controls.</param>
public sealed record EntityRowQuery(FieldSelection? Fields = null, ResultPageOptions? Window = null);

/// <summary>
/// Aggregation query controls within an <see cref="EntityQuery" />.
/// </summary>
/// <param name="Roots">Named aggregation roots to compute.</param>
public sealed record EntityAggregationQuery(IReadOnlyList<AggregationRoot> Roots)
{
    /// <summary>
    /// Creates an aggregation query descriptor.
    /// </summary>
    public EntityAggregationQuery(params AggregationRoot[] roots)
        : this((IReadOnlyList<AggregationRoot>)roots)
    {
    }
}

/// <summary>
/// Pagination metadata returned with a materialized query response.
/// </summary>
/// <param name="TotalCount">Total matching row count when known.</param>
/// <param name="NextCursor">Cursor for the next page when supported by the backend.</param>
/// <param name="Offset">Offset used to produce this page when offset pagination is in effect.</param>
/// <param name="Limit">Requested page size when present.</param>
/// <param name="HasMore">Whether another page is known to be available.</param>
public sealed record QueryPageInfo(
    int? TotalCount = null,
    QueryPageCursor? NextCursor = null,
    int? Offset = null,
    int? Limit = null,
    bool HasMore = false
);

/// <summary>
/// Materialized query response containing rows, pagination metadata, and optional aggregations.
/// </summary>
/// <typeparam name="TRow">Row type returned by the repository.</typeparam>
/// <param name="Rows">Materialized row results.</param>
/// <param name="PageInfo">Optional pagination metadata.</param>
/// <param name="Aggregations">Optional aggregation results keyed by aggregation root name.</param>
public sealed record EntityQueryResponse<TRow>(
    IReadOnlyList<TRow> Rows,
    QueryPageInfo? PageInfo = null,
    IReadOnlyDictionary<string, AggregationResult>? Aggregations = null
);

/// <summary>
/// Materialized observation query response.
/// </summary>
/// <param name="Rows">Materialized observation rows.</param>
/// <param name="PageInfo">Optional pagination metadata.</param>
/// <param name="Aggregations">Optional aggregation results keyed by aggregation root name.</param>
public sealed record EntityQueryResponse(
    IReadOnlyList<Observation> Rows,
    QueryPageInfo? PageInfo = null,
    IReadOnlyDictionary<string, AggregationResult>? Aggregations = null
);

/// <summary>
/// Result-page controls applied after filtering and before field projection.
/// </summary>
/// <param name="Limit">Maximum number of results.</param>
/// <param name="Cursor">Opaque cursor describing the next result page.</param>
/// <param name="Offset">Starting offset into the result set.</param>
/// <param name="OrderBy">Ordering of results.</param>
/// <param name="Mode">Pagination mode requested by the query author.</param>
public sealed record ResultPageOptions(
    int? Limit = null,
    QueryPageCursor? Cursor = null,
    int? Offset = null,
    QueryOrderBy[]? OrderBy = null,
    ResultPaginationMode Mode = ResultPaginationMode.Cursor
)
{
    /// <summary>
    /// Effective pagination mode after considering mode-specific request fields.
    /// </summary>
    public ResultPaginationMode EffectiveMode =>
        Offset is not null ? ResultPaginationMode.Offset : Mode;
}

/// <summary>
/// Supported result pagination modes.
/// </summary>
public enum ResultPaginationMode
{
    /// <summary>
    /// Cursor-based paging. This is the preferred mode for externally visible APIs and scalable backends.
    /// </summary>
    Cursor = 0,

    /// <summary>
    /// Offset-based paging. Useful for local development, deterministic small sets, and compatible backends.
    /// </summary>
    Offset = 1
}

/// <summary>
/// Opaque query-page cursor.
/// </summary>
public readonly record struct QueryPageCursor
{
    /// <summary>
    /// Creates a cursor value.
    /// </summary>
    public QueryPageCursor(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>
    /// Encoded cursor value.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// One ORDER BY expression for an in-process read query.
/// </summary>
public readonly record struct QueryOrderBy(FieldPath Path, bool Descending = false);
