using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.IR;

/// <summary>
/// Whether a relationship input is required by the consuming logical operation.
/// </summary>
public enum QueryInputRequirement
{
    /// <summary>The input may be absent.</summary>
    Optional = 0,

    /// <summary>The input must be resolved.</summary>
    Required = 1
}

/// <summary>
/// Sort direction for a logical query ordering.
/// </summary>
public enum QuerySortDirection
{
    /// <summary>Sort from lower to higher values.</summary>
    Ascending = 0,

    /// <summary>Sort from higher to lower values.</summary>
    Descending = 1
}

/// <summary>
/// Placement of null or missing values in an ordering.
/// </summary>
public enum QueryNullPlacement
{
    /// <summary>Place null and missing values before non-null values.</summary>
    First = 0,

    /// <summary>Place null and missing values after non-null values.</summary>
    Last = 1
}

/// <summary>
/// Base logical query node in the portable relation/query IR.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationQueryWireNames.NodeDiscriminator)]
[JsonDerivedType(typeof(SourceQueryNode), RelationQueryWireNames.SourceNode)]
[JsonDerivedType(typeof(FilterQueryNode), RelationQueryWireNames.FilterNode)]
[JsonDerivedType(typeof(TraverseRelationshipQueryNode), RelationQueryWireNames.TraverseRelationshipNode)]
[JsonDerivedType(typeof(JoinQueryNode), RelationQueryWireNames.JoinNode)]
[JsonDerivedType(typeof(ExpandCollectionQueryNode), RelationQueryWireNames.ExpandCollectionNode)]
[JsonDerivedType(typeof(ProjectQueryNode), RelationQueryWireNames.ProjectNode)]
[JsonDerivedType(typeof(DistinctQueryNode), RelationQueryWireNames.DistinctNode)]
[JsonDerivedType(typeof(AggregateQueryNode), RelationQueryWireNames.AggregateNode)]
[JsonDerivedType(typeof(OrderQueryNode), RelationQueryWireNames.OrderNode)]
[JsonDerivedType(typeof(PageQueryNode), RelationQueryWireNames.PageNode)]
public abstract record LogicalQueryNode
{
    /// <summary>
    /// Creates a logical query node.
    /// </summary>
    protected LogicalQueryNode(QueryNodeId id)
    {
        Id = id;
    }

    /// <summary>
    /// Stable node identifier.
    /// </summary>
    public QueryNodeId Id { get; init; }

    /// <summary>
    /// Logical nodes consumed by this node.
    /// </summary>
    [JsonIgnore]
    public abstract ImmutableArray<QueryNodeId> Inputs { get; }
}

/// <summary>
/// Declares a semantic-shaped input without fixing its physical placement or acquisition strategy.
/// </summary>
public sealed record SourceQueryNode : LogicalQueryNode
{
    /// <summary>Creates a semantic source node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="binding">Binding introduced by the source.</param>
    /// <param name="shape">Semantic shape of source values.</param>
    public SourceQueryNode(QueryNodeId id, ValueBindingId binding, ShapeId shape)
        : base(id)
    {
        Binding = binding;
        Shape = shape;
    }

    /// <summary>Binding introduced by this source.</summary>
    public ValueBindingId Binding { get; init; }

    /// <summary>Semantic shape of source values.</summary>
    public ShapeId Shape { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [];
}

/// <summary>
/// Filters an input rowset using a semantic predicate.
/// </summary>
public sealed record FilterQueryNode : LogicalQueryNode
{
    /// <summary>Creates a filter node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="input">Input rowset.</param>
    /// <param name="predicate">Predicate evaluated for each input row.</param>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public FilterQueryNode(QueryNodeId id, QueryNodeId input, Expr predicate)
        : base(id)
    {
        Input = input;
        Predicate = Guard.RequireNotNull(predicate);
    }

    /// <summary>Input rowset.</summary>
    public QueryNodeId Input { get; init; }

    /// <summary>Predicate evaluated for each input row.</summary>
    public Expr Predicate { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Input];
}

/// <summary>
/// Traverses a declared semantic relationship from a visible source binding.
/// </summary>
/// <remarks>
/// Unlike <see cref="JoinQueryNode"/>, which correlates independently produced rowsets using an
/// explicit predicate, this node follows a named domain relationship from one visible binding and
/// preserves that relationship's identity, cardinality, requiredness, lineage, and dependency
/// semantics. An interpreter may lower the traversal to a join, lookup, batch fetch, or graph-edge
/// traversal, but an explicit join should only be treated as this traversal when it can be proven
/// equivalent to the complete declared relationship.
/// </remarks>
public sealed record TraverseRelationshipQueryNode : LogicalQueryNode
{
    /// <summary>Creates a relationship traversal node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="input">Input rowset.</param>
    /// <param name="from">Visible binding from which traversal starts.</param>
    /// <param name="relationship">Semantic relationship to traverse.</param>
    /// <param name="result">Binding introduced for the related value.</param>
    /// <param name="joinKind">Join behavior when related values are absent.</param>
    /// <param name="requirement">Whether the related value must be resolved.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="joinKind"/> is <see cref="JoinKind.Right"/> or <see cref="JoinKind.Full"/>;
    /// forward traversal supports only inner or left join semantics.
    /// </exception>
    public TraverseRelationshipQueryNode(
        QueryNodeId id,
        QueryNodeId input,
        ValueBindingId from,
        RelationshipId relationship,
        ValueBindingId result,
        JoinKind joinKind,
        QueryInputRequirement requirement)
        : base(id)
    {
        Input = input;
        From = from;
        Relationship = relationship;
        Result = result;
        JoinKind = joinKind;
        Requirement = requirement;

        if (JoinKind is JoinKind.Right or JoinKind.Full)
        {
            throw new ArgumentOutOfRangeException(
                nameof(joinKind),
                joinKind,
                "A forward relationship traversal supports only inner or left join semantics.");
        }
    }

    /// <summary>Input rowset.</summary>
    public QueryNodeId Input { get; init; }

    /// <summary>Visible binding from which traversal starts.</summary>
    public ValueBindingId From { get; init; }

    /// <summary>Relationship to traverse.</summary>
    public RelationshipId Relationship { get; init; }

    /// <summary>Binding introduced for related values.</summary>
    public ValueBindingId Result { get; init; }

    /// <summary>Join behavior used when related values are absent.</summary>
    public JoinKind JoinKind { get; init; }

    /// <summary>Whether related input resolution is required.</summary>
    public QueryInputRequirement Requirement { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Input];
}

/// <summary>
/// Joins two logical rowsets using an explicit semantic predicate.
/// </summary>
public sealed record JoinQueryNode : LogicalQueryNode
{
    /// <summary>Creates an explicit join node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="left">Left input rowset.</param>
    /// <param name="right">Right input rowset.</param>
    /// <param name="kind">Join semantics.</param>
    /// <param name="predicate">Predicate evaluated over both input binding environments.</param>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public JoinQueryNode(QueryNodeId id, QueryNodeId left, QueryNodeId right, JoinKind kind, Expr predicate)
        : base(id)
    {
        Left = left;
        Right = right;
        Kind = kind;
        Predicate = Guard.RequireNotNull(predicate);
    }

    /// <summary>Left input rowset.</summary>
    public QueryNodeId Left { get; init; }

    /// <summary>Right input rowset.</summary>
    public QueryNodeId Right { get; init; }

    /// <summary>Join semantics.</summary>
    public JoinKind Kind { get; init; }

    /// <summary>Predicate evaluated over the combined binding environment.</summary>
    public Expr Predicate { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Left, Right];
}

/// <summary>
/// Expands a collection expression into one logical row per item while preserving the input bindings.
/// </summary>
/// <remarks>
/// For each input row, this node evaluates <see cref="Collection"/> and emits one output row for
/// each collection item. Existing bindings remain visible on every emitted row, and
/// <see cref="ItemBinding"/> identifies the current item. This belongs to the family of operations
/// commonly named <c>UNNEST</c>, <c>explode</c>, <c>unwind</c>, or Kusto
/// <c>mv-expand</c>. It also represents the expansion step of <c>flatMap</c>, but it does not itself
/// apply a nested query or mapping function to each item. This node has inner-expansion semantics:
/// a null or empty collection emits no rows, and an ordered collection emits items in collection
/// order. Interpreters must preserve these rules rather than inherit differing backend defaults.
/// </remarks>
public sealed record ExpandCollectionQueryNode : LogicalQueryNode
{
    /// <summary>Creates a collection-expansion node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="input">Input rowset.</param>
    /// <param name="collection">Collection expression to expand.</param>
    /// <param name="itemBinding">Binding introduced for each collection item.</param>
    /// <param name="itemType">Semantic type of each collection item.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="collection"/> or <paramref name="itemType"/> is <see langword="null"/>.
    /// </exception>
    public ExpandCollectionQueryNode(
        QueryNodeId id,
        QueryNodeId input,
        Expr collection,
        ValueBindingId itemBinding,
        TypeRef itemType)
        : base(id)
    {
        Input = input;
        Collection = Guard.RequireNotNull(collection);
        ItemBinding = itemBinding;
        ItemType = Guard.RequireNotNull(itemType);
    }

    /// <summary>Input rowset.</summary>
    public QueryNodeId Input { get; init; }

    /// <summary>Collection expression to expand.</summary>
    public Expr Collection { get; init; }

    /// <summary>Binding introduced for each collection item.</summary>
    public ValueBindingId ItemBinding { get; init; }

    /// <summary>Semantic type of each collection item.</summary>
    public TypeRef ItemType { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Input];
}

/// <summary>
/// Assigns a semantic expression to a target field in a projected shape.
/// </summary>
public sealed record ProjectionAssignment
{
    /// <summary>Creates a projection assignment.</summary>
    /// <param name="id">Stable assignment identifier.</param>
    /// <param name="target">Target field path.</param>
    /// <param name="value">Expression assigned to the target field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public ProjectionAssignment(QueryAssignmentId id, FieldPath target, Expr value)
    {
        Id = id;
        Target = target;
        Value = Guard.RequireNotNull(value);
    }

    /// <summary>Stable assignment identifier.</summary>
    public QueryAssignmentId Id { get; init; }

    /// <summary>Target field path.</summary>
    public FieldPath Target { get; init; }

    /// <summary>Expression assigned to the target field.</summary>
    public Expr Value { get; init; }
}

/// <summary>
/// Projects an input binding environment into a semantic output shape.
/// </summary>
public sealed record ProjectQueryNode : LogicalQueryNode
{
    /// <summary>Creates a projection node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="input">Input rowset.</param>
    /// <param name="resultBinding">Binding introduced for each projected value.</param>
    /// <param name="resultShape">Semantic shape produced by the projection.</param>
    /// <param name="assignments">Field assignments forming the projected shape.</param>
    /// <exception cref="ArgumentException"><paramref name="assignments"/> is default or empty.</exception>
    public ProjectQueryNode(
        QueryNodeId id,
        QueryNodeId input,
        ValueBindingId resultBinding,
        ShapeId resultShape,
        ImmutableArray<ProjectionAssignment> assignments)
        : base(id)
    {
        Input = input;
        ResultBinding = resultBinding;
        ResultShape = resultShape;
        Assignments = assignments.IsDefault ? [] : assignments;

        if (Assignments.IsDefaultOrEmpty)
            throw new ArgumentException("Projection requires at least one assignment.", nameof(assignments));
    }

    /// <summary>Input rowset.</summary>
    public QueryNodeId Input { get; init; }

    /// <summary>Binding introduced for the projected result.</summary>
    public ValueBindingId ResultBinding { get; init; }

    /// <summary>Semantic shape produced by the projection.</summary>
    public ShapeId ResultShape { get; init; }

    /// <summary>Output field assignments.</summary>
    public ImmutableArray<ProjectionAssignment> Assignments { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Input];
}

/// <summary>
/// Removes duplicate rows, optionally using explicit key expressions.
/// </summary>
public sealed record DistinctQueryNode : LogicalQueryNode
{
    /// <summary>Creates a distinct node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="input">Input rowset.</param>
    /// <param name="keys">Optional expressions defining row identity.</param>
    public DistinctQueryNode(QueryNodeId id, QueryNodeId input, ImmutableArray<Expr> keys = default)
        : base(id)
    {
        Input = input;
        Keys = keys.IsDefault ? [] : keys;
    }

    /// <summary>Input rowset.</summary>
    public QueryNodeId Input { get; init; }

    /// <summary>Optional expressions defining row identity for distinctness.</summary>
    public ImmutableArray<Expr> Keys { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Input];
}

/// <summary>
/// Defines one grouping field in an aggregate output.
/// </summary>
public sealed record QueryGrouping
{
    /// <summary>Creates a grouping definition.</summary>
    /// <param name="id">Stable grouping assignment identifier.</param>
    /// <param name="target">Target field receiving the grouping key.</param>
    /// <param name="key">Grouping key expression.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public QueryGrouping(QueryAssignmentId id, FieldPath target, Expr key)
    {
        Id = id;
        Target = target;
        Key = Guard.RequireNotNull(key);
    }

    /// <summary>Stable grouping assignment identifier.</summary>
    public QueryAssignmentId Id { get; init; }

    /// <summary>Target field receiving the grouping key.</summary>
    public FieldPath Target { get; init; }

    /// <summary>Grouping key expression.</summary>
    public Expr Key { get; init; }
}

/// <summary>
/// Defines one aggregate field in an aggregate output.
/// </summary>
public sealed record QueryAggregateAssignment
{
    /// <summary>Creates an aggregate assignment.</summary>
    /// <param name="id">Stable aggregate assignment identifier.</param>
    /// <param name="target">Target field receiving the aggregate result.</param>
    /// <param name="operation">Aggregate operation.</param>
    /// <param name="value">Optional value expression; count may omit it.</param>
    /// <param name="filter">Optional predicate scoped to this aggregate.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is <see langword="null"/> for an <paramref name="operation"/> other than
    /// <see cref="AggregateOperator.Count"/>.
    /// </exception>
    public QueryAggregateAssignment(
        QueryAssignmentId id,
        FieldPath target,
        AggregateOperator operation,
        Expr? value = null,
        Expr? filter = null)
    {
        Id = id;
        Target = target;
        Operation = operation;
        Value = value;
        Filter = filter;

        if (Operation != AggregateOperator.Count && Value is null)
            throw new ArgumentException($"Aggregate operation '{Operation}' requires a value expression.", nameof(value));
    }

    /// <summary>Stable aggregate assignment identifier.</summary>
    public QueryAssignmentId Id { get; init; }

    /// <summary>Target field receiving the aggregate result.</summary>
    public FieldPath Target { get; init; }

    /// <summary>Aggregate operation.</summary>
    public AggregateOperator Operation { get; init; }

    /// <summary>Optional value expression; count may omit it.</summary>
    public Expr? Value { get; init; }

    /// <summary>Optional predicate scoped to this aggregate.</summary>
    public Expr? Filter { get; init; }
}

/// <summary>
/// Groups an input rowset and projects aggregate results into a semantic shape.
/// </summary>
public sealed record AggregateQueryNode : LogicalQueryNode
{
    /// <summary>Creates an aggregate node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="input">Input rowset.</param>
    /// <param name="resultBinding">Binding introduced for each aggregate result.</param>
    /// <param name="resultShape">Semantic shape produced by aggregation.</param>
    /// <param name="groupings">Grouping fields in the aggregate output.</param>
    /// <param name="aggregates">Aggregate fields in the aggregate output.</param>
    /// <exception cref="ArgumentException"><paramref name="aggregates"/> is default or empty.</exception>
    public AggregateQueryNode(
        QueryNodeId id,
        QueryNodeId input,
        ValueBindingId resultBinding,
        ShapeId resultShape,
        ImmutableArray<QueryGrouping> groupings = default,
        ImmutableArray<QueryAggregateAssignment> aggregates = default)
        : base(id)
    {
        Input = input;
        ResultBinding = resultBinding;
        ResultShape = resultShape;
        Groupings = groupings.IsDefault ? [] : groupings;
        Aggregates = aggregates.IsDefault ? [] : aggregates;

        if (Aggregates.IsDefaultOrEmpty)
            throw new ArgumentException("Aggregate node requires at least one aggregate assignment.", nameof(aggregates));
    }

    /// <summary>Input rowset.</summary>
    public QueryNodeId Input { get; init; }

    /// <summary>Binding introduced for aggregate output rows.</summary>
    public ValueBindingId ResultBinding { get; init; }

    /// <summary>Semantic shape produced by aggregation.</summary>
    public ShapeId ResultShape { get; init; }

    /// <summary>Grouping assignments.</summary>
    public ImmutableArray<QueryGrouping> Groupings { get; init; }

    /// <summary>Aggregate assignments.</summary>
    public ImmutableArray<QueryAggregateAssignment> Aggregates { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Input];
}

/// <summary>
/// One deterministic ordering expression.
/// </summary>
public sealed record QueryOrdering
{
    /// <summary>Creates an ordering definition.</summary>
    /// <param name="key">Ordering key expression.</param>
    /// <param name="direction">Sort direction.</param>
    /// <param name="nullPlacement">Placement of null and missing values.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public QueryOrdering(
        Expr key,
        QuerySortDirection direction = QuerySortDirection.Ascending,
        QueryNullPlacement nullPlacement = QueryNullPlacement.Last)
    {
        Key = Guard.RequireNotNull(key);
        Direction = direction;
        NullPlacement = nullPlacement;
    }

    /// <summary>Ordering key expression.</summary>
    public Expr Key { get; init; }

    /// <summary>Ordering direction.</summary>
    public QuerySortDirection Direction { get; init; }

    /// <summary>Placement of null and missing values.</summary>
    public QueryNullPlacement NullPlacement { get; init; }
}

/// <summary>
/// Orders a logical rowset.
/// </summary>
public sealed record OrderQueryNode : LogicalQueryNode
{
    /// <summary>Creates an ordering node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="input">Input rowset.</param>
    /// <param name="orderings">Ordered keys, from primary to final tie-breaker.</param>
    /// <exception cref="ArgumentException"><paramref name="orderings"/> is default or empty.</exception>
    public OrderQueryNode(QueryNodeId id, QueryNodeId input, ImmutableArray<QueryOrdering> orderings)
        : base(id)
    {
        Input = input;
        Orderings = orderings.IsDefault ? [] : orderings;

        if (Orderings.IsDefaultOrEmpty)
            throw new ArgumentException("Order node requires at least one ordering.", nameof(orderings));
    }

    /// <summary>Input rowset.</summary>
    public QueryNodeId Input { get; init; }

    /// <summary>Ordered keys, from primary to final tie-breaker.</summary>
    public ImmutableArray<QueryOrdering> Orderings { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Input];
}

/// <summary>
/// Base semantic page request. Cursor encoding remains an invocation/adapter concern.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationQueryWireNames.PageDiscriminator)]
[JsonDerivedType(typeof(OffsetPageDefinition), RelationQueryWireNames.OffsetPage)]
[JsonDerivedType(typeof(KeysetPageDefinition), RelationQueryWireNames.KeysetPage)]
public abstract record QueryPageDefinition
{
    /// <summary>Creates a page definition.</summary>
    /// <param name="limit">Maximum number of rows requested.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    protected QueryPageDefinition(int limit)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Page limit must be positive.");
        Limit = limit;
    }

    /// <summary>Maximum number of rows requested.</summary>
    public int Limit { get; init; }
}

/// <summary>
/// Offset-based page semantics.
/// </summary>
public sealed record OffsetPageDefinition : QueryPageDefinition
{
    /// <summary>Creates an offset page definition.</summary>
    /// <param name="limit">Maximum number of rows requested.</param>
    /// <param name="offset">Number of rows skipped before the page.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="limit"/> is not positive or <paramref name="offset"/> is negative.
    /// </exception>
    public OffsetPageDefinition(int limit, int offset = 0)
        : base(limit)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Page offset must be non-negative.");
        Offset = offset;
    }

    /// <summary>Number of rows skipped before the page.</summary>
    public int Offset { get; init; }
}

/// <summary>
/// Keyset page semantics expressed through ordered continuation values or parameters.
/// </summary>
public sealed record KeysetPageDefinition : QueryPageDefinition
{
    /// <summary>Creates a keyset page definition.</summary>
    /// <param name="limit">Maximum number of rows requested.</param>
    /// <param name="after">Continuation expressions aligned with the preceding ordering.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    public KeysetPageDefinition(int limit, ImmutableArray<Expr> after = default)
        : base(limit)
    {
        After = after.IsDefault ? [] : after;
    }

    /// <summary>Continuation expressions aligned with the preceding order node.</summary>
    public ImmutableArray<Expr> After { get; init; }
}

/// <summary>
/// Applies semantic pagination to an input rowset.
/// </summary>
public sealed record PageQueryNode : LogicalQueryNode
{
    /// <summary>Creates a page node.</summary>
    /// <param name="id">Stable node identifier.</param>
    /// <param name="input">Input rowset.</param>
    /// <param name="page">Semantic page request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    public PageQueryNode(QueryNodeId id, QueryNodeId input, QueryPageDefinition page)
        : base(id)
    {
        Input = input;
        Page = Guard.RequireNotNull(page);
    }

    /// <summary>Input rowset.</summary>
    public QueryNodeId Input { get; init; }

    /// <summary>Semantic page request.</summary>
    public QueryPageDefinition Page { get; init; }

    /// <inheritdoc />
    public override ImmutableArray<QueryNodeId> Inputs => [Input];
}
