using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Authoring;

/// <summary>Typed handle for one logical node owned by a structural authoring core.</summary>
/// <typeparam name="TNode">Canonical logical-node type referenced by the handle.</typeparam>
public readonly struct RelationQueryNodeHandle<TNode> where TNode : LogicalQueryNode
{
    internal RelationQueryNodeHandle(RelationQueryAuthoringCore owner, QueryNodeId id)
    {
        Owner = owner;
        Id = id;
    }

    internal RelationQueryAuthoringCore? Owner { get; }

    /// <summary>Canonical logical-node identity.</summary>
    public QueryNodeId Id { get; }

    /// <inheritdoc />
    public override string ToString() => Id.ToString();
}

/// <summary>Typed pair returned by a logical node that introduces a value binding.</summary>
/// <typeparam name="TNode">Canonical logical-node type referenced by the node handle.</typeparam>
public readonly struct RelationQueryBoundNodeHandle<TNode> where TNode : LogicalQueryNode
{
    internal RelationQueryBoundNodeHandle(
        RelationQueryNodeHandle<TNode> node,
        RelationQueryBindingHandle binding)
    {
        Node = node;
        Binding = binding;
    }

    /// <summary>Typed logical-node handle.</summary>
    public RelationQueryNodeHandle<TNode> Node { get; }

    /// <summary>Binding introduced by the logical node.</summary>
    public RelationQueryBindingHandle Binding { get; }
}

/// <summary>Typed handle for one semantic value binding owned by a structural authoring core.</summary>
public readonly struct RelationQueryBindingHandle
{
    internal RelationQueryBindingHandle(RelationQueryAuthoringCore owner, ValueBindingId id)
    {
        Owner = owner;
        Id = id;
    }

    internal RelationQueryAuthoringCore? Owner { get; }

    /// <summary>Canonical value-binding identity.</summary>
    public ValueBindingId Id { get; }

    /// <summary>Creates an expression referencing a field in this binding.</summary>
    /// <param name="path">Field path within the bound value.</param>
    /// <returns>A binding-qualified canonical field expression.</returns>
    public Expr Field(FieldPath path) => Expr.Field(Id, path);

    /// <summary>Creates an expression referencing a field in this binding.</summary>
    /// <param name="path">Dotted field path within the bound value.</param>
    /// <returns>A binding-qualified canonical field expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, white space, or contains no field segments.
    /// </exception>
    public Expr Field(string path) => Expr.Field(Id, path);

    /// <inheritdoc />
    public override string ToString() => Id.ToString();
}

/// <summary>Typed handle for one query parameter owned by a structural authoring core.</summary>
public readonly struct RelationQueryParameterHandle
{
    internal RelationQueryParameterHandle(RelationQueryAuthoringCore owner, QueryParameterId id)
    {
        Owner = owner;
        Id = id;
    }

    internal RelationQueryAuthoringCore? Owner { get; }

    /// <summary>Canonical query-parameter identity.</summary>
    public QueryParameterId Id { get; }

    /// <summary>Creates a canonical expression referencing this parameter.</summary>
    public Expr Expression => Expr.Param(Id.Value);

    /// <inheritdoc />
    public override string ToString() => Id.ToString();
}

/// <summary>Untyped base for heterogeneous named-result handle collections.</summary>
public abstract class RelationQueryResultHandle
{
    private protected RelationQueryResultHandle(
        RelationQueryAuthoringCore owner,
        QueryResultDefinition definition)
    {
        Owner = owner;
        Definition = definition;
    }

    internal RelationQueryAuthoringCore Owner { get; }

    internal QueryResultDefinition Definition { get; }

    /// <summary>Canonical named-result identity.</summary>
    public QueryResultId Id => Definition.Id;

    /// <summary>Logical node that produces the named result.</summary>
    public QueryNodeId Input => Definition.Input;
}

/// <summary>Typed handle for one named result owned by a structural authoring core.</summary>
/// <typeparam name="TResult">Canonical named-result definition type.</typeparam>
public sealed class RelationQueryResultHandle<TResult> : RelationQueryResultHandle
    where TResult : QueryResultDefinition
{
    internal RelationQueryResultHandle(RelationQueryAuthoringCore owner, TResult definition)
        : base(owner, definition)
    {
    }

    /// <summary>Canonical result definition represented by this handle.</summary>
    public TResult Result => (TResult)Definition;
}

/// <summary>
/// Canonical definition, canonical validation, and non-semantic authoring attribution produced by
/// one structural terminal.
/// </summary>
/// <typeparam name="TDefinition">Canonical relation or query definition type.</typeparam>
public sealed class RelationQueryAuthoringResult<TDefinition>
    where TDefinition : RelationQueryDefinition
{
    internal RelationQueryAuthoringResult(
        TDefinition definition,
        DocumentValidationResult validation,
        RelationQueryAuthoringManifest provenance)
    {
        Definition = definition;
        Validation = validation;
        Provenance = provenance;
    }

    /// <summary>Canonical relation or query definition.</summary>
    public TDefinition Definition { get; }

    /// <summary>Result from the authoritative canonical definition validator.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Non-semantic identity and producer-source attribution.</summary>
    public RelationQueryAuthoringManifest Provenance { get; }

    /// <summary>Creates a validated, fingerprinted current-version persistence envelope.</summary>
    /// <param name="metadata">Optional portable document metadata.</param>
    /// <returns>A canonical relation/query document containing <see cref="Definition"/>.</returns>
    /// <exception cref="ArgumentException"><see cref="Definition"/> fails canonical semantic validation.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Definition"/> contains a value without a canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="Definition"/> contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public RelationQueryDocument CreateDocument(RelationQueryDocumentMetadata? metadata = null) =>
        RelationQueryDocument.FromDefinition(Definition, metadata);
}

/// <summary>Structural input for one field assignment in a projection node.</summary>
public sealed record RelationQueryProjectionAssignment
{
    /// <summary>Creates a projection-assignment input.</summary>
    /// <param name="target">Target field path in the projected shape.</param>
    /// <param name="value">Canonical expression assigned to the target field.</param>
    /// <param name="id">Optional explicit assignment identity.</param>
    /// <param name="assignmentSource">Optional producer-source attribution for the assignment decision.</param>
    /// <param name="valueSource">Optional producer-source attribution for the value-expression site.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public RelationQueryProjectionAssignment(
        FieldPath target,
        Expr value,
        QueryAssignmentId? id = null,
        RelationQueryAuthoringSource? assignmentSource = null,
        RelationQueryAuthoringSource? valueSource = null)
    {
        Target = target;
        Value = Guard.RequireNotNull(value);
        Id = id;
        AssignmentSource = assignmentSource;
        ValueSource = valueSource;
    }

    /// <summary>Target field path in the projected shape.</summary>
    public FieldPath Target { get; init; }

    /// <summary>Canonical expression assigned to the target field.</summary>
    public Expr Value { get; init; }

    /// <summary>Optional explicit assignment identity.</summary>
    public QueryAssignmentId? Id { get; init; }

    /// <summary>Optional producer-source attribution for the assignment decision.</summary>
    public RelationQueryAuthoringSource? AssignmentSource { get; init; }

    /// <summary>Optional producer-source attribution for the value-expression site.</summary>
    public RelationQueryAuthoringSource? ValueSource { get; init; }
}

/// <summary>Structural input for one grouping assignment in an aggregate node.</summary>
public sealed record RelationQueryGroupingAssignment
{
    /// <summary>Creates a grouping-assignment input.</summary>
    /// <param name="target">Target field receiving the grouping value.</param>
    /// <param name="key">Canonical grouping-key expression.</param>
    /// <param name="id">Optional explicit assignment identity.</param>
    /// <param name="assignmentSource">Optional producer-source attribution for the assignment decision.</param>
    /// <param name="keySource">Optional producer-source attribution for the key-expression site.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public RelationQueryGroupingAssignment(
        FieldPath target,
        Expr key,
        QueryAssignmentId? id = null,
        RelationQueryAuthoringSource? assignmentSource = null,
        RelationQueryAuthoringSource? keySource = null)
    {
        Target = target;
        Key = Guard.RequireNotNull(key);
        Id = id;
        AssignmentSource = assignmentSource;
        KeySource = keySource;
    }

    /// <summary>Target field receiving the grouping value.</summary>
    public FieldPath Target { get; init; }

    /// <summary>Canonical grouping-key expression.</summary>
    public Expr Key { get; init; }

    /// <summary>Optional explicit assignment identity.</summary>
    public QueryAssignmentId? Id { get; init; }

    /// <summary>Optional producer-source attribution for the assignment decision.</summary>
    public RelationQueryAuthoringSource? AssignmentSource { get; init; }

    /// <summary>Optional producer-source attribution for the key-expression site.</summary>
    public RelationQueryAuthoringSource? KeySource { get; init; }
}

/// <summary>Structural input for one aggregate assignment in an aggregate node.</summary>
public sealed record RelationQueryAggregateAssignment
{
    /// <summary>Creates an aggregate-assignment input.</summary>
    /// <param name="target">Target field receiving the aggregate result.</param>
    /// <param name="operation">Canonical aggregate operation.</param>
    /// <param name="value">Optional aggregate value expression; count may omit it.</param>
    /// <param name="filter">Optional predicate scoped to this aggregate.</param>
    /// <param name="id">Optional explicit assignment identity.</param>
    /// <param name="assignmentSource">Optional producer-source attribution for the assignment decision.</param>
    /// <param name="valueSource">Optional producer-source attribution for the value-expression site.</param>
    /// <param name="filterSource">Optional producer-source attribution for the filter-expression site.</param>
    public RelationQueryAggregateAssignment(
        FieldPath target,
        AggregateOperator operation,
        Expr? value = null,
        Expr? filter = null,
        QueryAssignmentId? id = null,
        RelationQueryAuthoringSource? assignmentSource = null,
        RelationQueryAuthoringSource? valueSource = null,
        RelationQueryAuthoringSource? filterSource = null)
    {
        Target = target;
        Operation = operation;
        Value = value;
        Filter = filter;
        Id = id;
        AssignmentSource = assignmentSource;
        ValueSource = valueSource;
        FilterSource = filterSource;
    }

    /// <summary>Target field receiving the aggregate result.</summary>
    public FieldPath Target { get; init; }

    /// <summary>Canonical aggregate operation.</summary>
    public AggregateOperator Operation { get; init; }

    /// <summary>Optional aggregate value expression; count may omit it.</summary>
    public Expr? Value { get; init; }

    /// <summary>Optional predicate scoped to this aggregate.</summary>
    public Expr? Filter { get; init; }

    /// <summary>Optional explicit assignment identity.</summary>
    public QueryAssignmentId? Id { get; init; }

    /// <summary>Optional producer-source attribution for the assignment decision.</summary>
    public RelationQueryAuthoringSource? AssignmentSource { get; init; }

    /// <summary>Optional producer-source attribution for the value-expression site.</summary>
    public RelationQueryAuthoringSource? ValueSource { get; init; }

    /// <summary>Optional producer-source attribution for the filter-expression site.</summary>
    public RelationQueryAuthoringSource? FilterSource { get; init; }
}

/// <summary>Structural input for one ordering expression.</summary>
public sealed record RelationQueryOrderingInput
{
    /// <summary>Creates an ordering input.</summary>
    /// <param name="key">Canonical ordering-key expression.</param>
    /// <param name="direction">Sort direction.</param>
    /// <param name="nullPlacement">Placement of null or missing values.</param>
    /// <param name="keySource">Optional producer-source attribution for the key-expression site.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public RelationQueryOrderingInput(
        Expr key,
        QuerySortDirection direction = QuerySortDirection.Ascending,
        QueryNullPlacement nullPlacement = QueryNullPlacement.Last,
        RelationQueryAuthoringSource? keySource = null)
    {
        Key = Guard.RequireNotNull(key);
        Direction = direction;
        NullPlacement = nullPlacement;
        KeySource = keySource;
    }

    /// <summary>Canonical ordering-key expression.</summary>
    public Expr Key { get; init; }

    /// <summary>Sort direction.</summary>
    public QuerySortDirection Direction { get; init; }

    /// <summary>Placement of null or missing values.</summary>
    public QueryNullPlacement NullPlacement { get; init; }

    /// <summary>Optional producer-source attribution for the key-expression site.</summary>
    public RelationQueryAuthoringSource? KeySource { get; init; }
}

/// <summary>Structural input for an expression site that has no assignment identity.</summary>
public sealed record RelationQueryExpressionInput
{
    /// <summary>Creates an expression input.</summary>
    /// <param name="value">Canonical semantic expression.</param>
    /// <param name="source">Optional producer-source attribution for the expression site.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public RelationQueryExpressionInput(Expr value, RelationQueryAuthoringSource? source = null)
    {
        Value = Guard.RequireNotNull(value);
        Source = source;
    }

    /// <summary>Canonical semantic expression.</summary>
    public Expr Value { get; init; }

    /// <summary>Optional producer-source attribution for the expression site.</summary>
    public RelationQueryAuthoringSource? Source { get; init; }

    /// <summary>Wraps a canonical expression without producer-source attribution.</summary>
    /// <param name="value">Canonical semantic expression.</param>
    /// <returns>A structural expression input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static implicit operator RelationQueryExpressionInput(Expr value) => new(value);
}
