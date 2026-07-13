using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.IR;

/// <summary>
/// Declares one parameter accepted by a persisted logical query definition.
/// Runtime parameter values belong to a separate invocation.
/// </summary>
public sealed record QueryParameterDefinition
{
    /// <summary>Creates a query parameter declaration.</summary>
    /// <param name="id">Stable parameter identifier.</param>
    /// <param name="type">Semantic parameter type.</param>
    /// <param name="presence">Whether an invocation must provide the parameter.</param>
    /// <param name="defaultValue">Optional value used when an optional parameter is omitted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="presence"/> is <see cref="FieldPresence.Required"/> and
    /// <paramref name="defaultValue"/> is not <see langword="null"/>.
    /// </exception>
    public QueryParameterDefinition(
        QueryParameterId id,
        TypeRef type,
        FieldPresence presence = FieldPresence.Required,
        ObservationValue? defaultValue = null)
    {
        Id = id;
        Type = Guard.RequireNotNull(type);
        Presence = presence;
        DefaultValue = defaultValue;

        if (Presence == FieldPresence.Required && DefaultValue is not null)
            throw new ArgumentException("A required parameter cannot declare a default value.", nameof(defaultValue));
    }

    /// <summary>Stable parameter identifier referenced by <see cref="ParameterExpr"/>.</summary>
    public QueryParameterId Id { get; init; }

    /// <summary>Semantic parameter type.</summary>
    public TypeRef Type { get; init; }

    /// <summary>Whether a runtime invocation must provide the parameter.</summary>
    public FieldPresence Presence { get; init; }

    /// <summary>Optional default value for an optional parameter.</summary>
    public ObservationValue? DefaultValue { get; init; }
}

/// <summary>
/// Portable logical node table shared by relation and query definitions.
/// </summary>
public sealed record LogicalQueryDefinition
{
    /// <summary>Creates a logical query definition.</summary>
    /// <param name="nodes">Logical query nodes indexed by stable identity.</param>
    /// <param name="parameters">Parameters referenced by semantic expressions.</param>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> is default or empty.</exception>
    public LogicalQueryDefinition(
        ImmutableArray<LogicalQueryNode> nodes,
        ImmutableArray<QueryParameterDefinition> parameters = default)
    {
        Nodes = nodes.IsDefault
            ? []
            : [.. nodes.OrderBy(static node => node.Id.Value, StringComparer.Ordinal)];
        Parameters = parameters.IsDefault
            ? []
            : [.. parameters.OrderBy(static parameter => parameter.Id.Value, StringComparer.Ordinal)];

        if (Nodes.IsDefaultOrEmpty)
            throw new ArgumentException("Logical query requires at least one node.", nameof(nodes));
    }

    /// <summary>Logical query nodes indexed by stable identity rather than declaration order.</summary>
    public ImmutableArray<LogicalQueryNode> Nodes { get; init; }

    /// <summary>Parameters referenced by semantic expressions in the logical query.</summary>
    public ImmutableArray<QueryParameterDefinition> Parameters { get; init; }
}

/// <summary>
/// Base portable definition for a relation or query.
/// </summary>
/// <remarks>
/// <para>
/// Relations and queries share the same logical node graph because both describe how shaped
/// values are filtered, joined, traversed, projected, aggregated, ordered, and paged. This
/// common representation allows the same compiler analysis, capability matching, optimization,
/// diagnostics, and backend lowering strategies to interpret either definition.
/// </para>
/// <para>
/// A relation describes a reusable semantic correspondence rooted in an input value. It declares
/// which source binding is the root, the shape and cardinality of the related output, and any
/// invariants that the correspondence must satisfy. Relations are therefore suitable for DTO
/// mapping, enrichment, hydration, denormalization, lineage analysis, and determining which
/// derived values may be affected when an input changes.
/// </para>
/// <para>
/// A query describes an independently invoked request for data. It exposes one or more named
/// result branches over the logical graph (such as rows and aggregations). Queries are therefore
/// suitable for retrieval, search, reporting, and aggregation across one or more sources.
/// </para>
/// <para>
/// The distinction is semantic rather than physical: neither definition chooses a database,
/// source placement, join algorithm, batching strategy, or execution runtime. Those decisions
/// belong to compilers, planners, and adapters interpreting this portable definition.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationQueryWireNames.DefinitionDiscriminator)]
[JsonDerivedType(typeof(RelationDefinition), RelationQueryWireNames.RelationDefinition)]
[JsonDerivedType(typeof(QueryDefinition), RelationQueryWireNames.QueryDefinition)]
public abstract record RelationQueryDefinition
{
    /// <summary>Creates a relation/query definition.</summary>
    /// <param name="body">Portable logical query body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    protected RelationQueryDefinition(LogicalQueryDefinition body)
    {
        Body = Guard.RequireNotNull(body);
    }

    /// <summary>Shared logical query body.</summary>
    public LogicalQueryDefinition Body { get; init; }
}

/// <summary>
/// Number of output rows a relation may emit for each root.
/// </summary>
public enum RelationOutputMode
{
    /// <summary>Exactly one output is expected for every root.</summary>
    OnePerRoot = 0,

    /// <summary>At most one output may be emitted for every root.</summary>
    ZeroOrOnePerRoot = 1,

    /// <summary>Any number of outputs may be emitted for every root.</summary>
    ManyPerRoot = 2,

    /// <summary>Outputs are computed over the complete input set rather than individual roots.</summary>
    Set = 3
}

/// <summary>
/// Declares the semantic output of a relation definition.
/// </summary>
public sealed record RelationOutputDefinition
{
    /// <summary>Creates a relation output definition.</summary>
    /// <param name="node">Logical node producing output rows.</param>
    /// <param name="shape">Semantic shape of every output row.</param>
    /// <param name="mode">Output cardinality relative to relation roots.</param>
    /// <param name="key">Optional expression defining stable output identity.</param>
    public RelationOutputDefinition(
        QueryNodeId node,
        QualifiedShapeId shape,
        RelationOutputMode mode,
        Expr? key = null)
    {
        Node = node;
        Shape = shape;
        Mode = mode;
        Key = key;
    }

    /// <summary>Logical node producing output rows.</summary>
    public QueryNodeId Node { get; init; }

    /// <summary>Semantic shape of every output row.</summary>
    public QualifiedShapeId Shape { get; init; }

    /// <summary>Output cardinality relative to relation roots.</summary>
    public RelationOutputMode Mode { get; init; }

    /// <summary>Optional expression defining stable output identity.</summary>
    public Expr? Key { get; init; }
}

/// <summary>
/// Canonical, portable relation definition.
/// </summary>
public sealed record RelationDefinition : RelationQueryDefinition
{
    /// <summary>Creates a canonical relation definition.</summary>
    /// <param name="id">Stable relation identifier.</param>
    /// <param name="name">Human-readable relation name.</param>
    /// <param name="body">Portable logical query body.</param>
    /// <param name="rootBinding">Source binding whose values define rooted execution.</param>
    /// <param name="output">Relation output semantics.</param>
    /// <param name="invariants">Invariants validated for relation outputs.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>, <paramref name="name"/>, <paramref name="body"/>, or
    /// <paramref name="output"/> is <see langword="null"/>.
    /// </exception>
    public RelationDefinition(
        RelationId id,
        RelationName name,
        LogicalQueryDefinition body,
        ValueBindingId rootBinding,
        RelationOutputDefinition output,
        ImmutableArray<InvariantDefinition> invariants = default)
        : base(body)
    {
        Id = Guard.RequireNotNull(id);
        Name = Guard.RequireNotNull(name);
        RootBinding = rootBinding;
        Output = Guard.RequireNotNull(output);
        Invariants = invariants.IsDefault ? [] : invariants;
    }

    /// <summary>Stable relation identifier.</summary>
    public RelationId Id { get; init; }

    /// <summary>Human-readable relation name.</summary>
    public RelationName Name { get; init; }

    /// <summary>Source binding whose values define rooted execution.</summary>
    public ValueBindingId RootBinding { get; init; }

    /// <summary>Relation output semantics.</summary>
    public RelationOutputDefinition Output { get; init; }

    /// <summary>Invariants validated for relation outputs.</summary>
    public ImmutableArray<InvariantDefinition> Invariants { get; init; }
}

/// <summary>
/// Base named result emitted by a canonical query definition.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationQueryWireNames.ResultDiscriminator)]
[JsonDerivedType(typeof(RowsQueryResultDefinition), RelationQueryWireNames.RowsResult)]
[JsonDerivedType(typeof(AggregationQueryResultDefinition), RelationQueryWireNames.AggregationResult)]
public abstract record QueryResultDefinition
{
    /// <summary>Creates a named query result definition.</summary>
    /// <param name="id">Stable result identifier.</param>
    /// <param name="input">Logical node whose rows form this result.</param>
    protected QueryResultDefinition(QueryResultId id, QueryNodeId input)
    {
        Id = id;
        Input = input;
    }

    /// <summary>Stable result identifier.</summary>
    public QueryResultId Id { get; init; }

    /// <summary>Logical node whose rows form this result.</summary>
    public QueryNodeId Input { get; init; }
}

/// <summary>
/// Named row result branch.
/// </summary>
public sealed record RowsQueryResultDefinition : QueryResultDefinition
{
    /// <summary>Creates a row result definition.</summary>
    /// <param name="id">Stable result identifier.</param>
    /// <param name="input">Logical node whose rows form this result.</param>
    public RowsQueryResultDefinition(QueryResultId id, QueryNodeId input)
        : base(id, input)
    {
    }
}

/// <summary>
/// Named aggregation result branch.
/// </summary>
public sealed record AggregationQueryResultDefinition : QueryResultDefinition
{
    /// <summary>Creates an aggregation result definition.</summary>
    /// <param name="id">Stable result identifier.</param>
    /// <param name="input">Aggregate node whose rows form this result.</param>
    public AggregationQueryResultDefinition(QueryResultId id, QueryNodeId input)
        : base(id, input)
    {
    }
}

/// <summary>
/// Canonical, portable query definition. Runtime values and source placement belong to invocation.
/// </summary>
public sealed record QueryDefinition : RelationQueryDefinition
{
    /// <summary>Creates a canonical query definition.</summary>
    /// <param name="id">Stable query identifier.</param>
    /// <param name="name">Human-readable query name.</param>
    /// <param name="body">Portable logical query body.</param>
    /// <param name="results">Named row and aggregation result branches.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="results"/> is default or empty.</exception>
    public QueryDefinition(
        QueryId id,
        QueryName name,
        LogicalQueryDefinition body,
        ImmutableArray<QueryResultDefinition> results)
        : base(body)
    {
        Id = id;
        Name = name;
        Results = results.IsDefault
            ? []
            : [.. results.OrderBy(static result => result.Id.Value, StringComparer.Ordinal)];

        if (Results.IsDefaultOrEmpty)
            throw new ArgumentException("Query definition requires at least one result.", nameof(results));
    }

    /// <summary>Stable query identifier.</summary>
    public QueryId Id { get; init; }

    /// <summary>Human-readable query name.</summary>
    public QueryName Name { get; init; }

    /// <summary>Named row and aggregation result branches.</summary>
    public ImmutableArray<QueryResultDefinition> Results { get; init; }
}
