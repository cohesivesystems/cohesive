using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

internal interface IRelationQueryExpressionParameterMarker
{
    QueryParameterId ParameterId { get; }

    RelationQueryExpressionAuthoring? Owner => null;

    bool IsProvablyNonNull => true;
}

/// <summary>Typed authoring handle for one explicit canonical relationship.</summary>
/// <typeparam name="TSource">CLR type at the relationship source endpoint.</typeparam>
/// <typeparam name="TTarget">CLR type at the relationship target endpoint.</typeparam>
public sealed class RelationQueryExpressionRelationship<TSource, TTarget>
    where TSource : notnull
    where TTarget : notnull
{
    internal RelationQueryExpressionRelationship(RelationshipDefinition definition)
    {
        Definition = Guard.RequireNotNull(definition);
    }

    /// <summary>Canonical portable relationship definition.</summary>
    public RelationshipDefinition Definition { get; }

    /// <summary>Stable canonical relationship identity.</summary>
    public RelationshipId Id => Definition.Id;

    /// <summary>Graph-qualified source shape containing the reference.</summary>
    public QualifiedShapeId SourceShape => Definition.SourceShape;

    /// <summary>Graph-qualified target shape addressed by the reference.</summary>
    public QualifiedShapeId TargetShape => Definition.TargetShape;
}

/// <summary>
/// Untyped base for a CLR value binding used by expression authoring.
/// </summary>
/// <remarks>
/// The binding is authoring-time metadata only. Canonical definitions retain only
/// <see cref="Id"/> and never retain this handle or its CLR type.
/// </remarks>
public abstract class RelationQueryExpressionValueBinding
{
    private protected RelationQueryExpressionValueBinding(
        RelationQueryExpressionAuthoring owner,
        RelationQueryBindingHandle structural,
        TypeRef type,
        QualifiedShapeId? shape,
        RelationQueryExpressionMemberPathResolver? memberPathResolver,
        Func<Type, TypeRef>? typeResolver,
        bool usesImportedMapping)
    {
        Owner = owner;
        Structural = structural;
        Type = Guard.RequireNotNull(type);
        Shape = shape;
        MemberPathResolver = memberPathResolver;
        TypeResolver = typeResolver;
        UsesImportedMapping = usesImportedMapping;
    }

    internal RelationQueryExpressionAuthoring Owner { get; }

    internal RelationQueryExpressionMemberPathResolver? MemberPathResolver { get; }

    internal Func<Type, TypeRef>? TypeResolver { get; }

    internal bool UsesImportedMapping { get; }

    /// <summary>
    /// Structural binding handle used to author canonical operations directly through the owning
    /// expression session's <see cref="RelationQueryExpressionAuthoring.Structural"/> core.
    /// </summary>
    public RelationQueryBindingHandle Structural { get; }

    internal abstract Type ClrType { get; }

    /// <summary>Canonical value-binding identity.</summary>
    public ValueBindingId Id => Structural.Id;

    /// <summary>Portable semantic type of the bound value.</summary>
    public TypeRef Type { get; }

    /// <summary>
    /// Graph-qualified semantic shape represented by the binding, or <see langword="null"/> for a
    /// scalar or structurally typed collection item that has no root shape.
    /// </summary>
    public QualifiedShapeId? Shape { get; }

    /// <inheritdoc />
    public override string ToString() => Id.ToString();
}

/// <summary>Typed CLR value binding used as a parameter in expression-authoring lambdas.</summary>
/// <typeparam name="T">CLR type represented by the binding.</typeparam>
public sealed class RelationQueryExpressionValueBinding<T> : RelationQueryExpressionValueBinding
    where T : notnull
{
    internal RelationQueryExpressionValueBinding(
        RelationQueryExpressionAuthoring owner,
        RelationQueryBindingHandle structural,
        TypeRef type,
        QualifiedShapeId? shape,
        RelationQueryExpressionMemberPathResolver? memberPathResolver = null,
        Func<Type, TypeRef>? typeResolver = null,
        bool usesImportedMapping = false)
        : base(owner, structural, type, shape, memberPathResolver, typeResolver, usesImportedMapping)
    {
    }

    internal override Type ClrType => typeof(T);
}

/// <summary>Node-type-erased base for a logical node and its focused CLR value binding.</summary>
/// <typeparam name="TValue">CLR type represented by <see cref="Binding"/>.</typeparam>
/// <remarks>
/// The erased node and relation-root context are authoring conveniences only. Canonical definitions retain the
/// exact logical node, binding, and root identities rather than this handle or its CLR type.
/// </remarks>
public abstract class RelationQueryExpressionBoundNode<TValue>
    where TValue : notnull
{
    private protected RelationQueryExpressionBoundNode(
        RelationQueryNodeHandle<LogicalQueryNode> node,
        RelationQueryExpressionValueBinding<TValue> binding,
        RelationQueryExpressionValueBinding? relationRoot)
    {
        Binding = Guard.RequireNotNull(binding);
        if (!ReferenceEquals(node.Owner, binding.Structural.Owner))
        {
            throw new InvalidOperationException(
                "A bound node and its focused binding must belong to the same authoring session.");
        }

        if (relationRoot is not null && !ReferenceEquals(binding.Owner, relationRoot.Owner))
        {
            throw new InvalidOperationException(
                "A bound node and its relation-root context must belong to the same authoring session.");
        }

        StructuralNode = node;
        RelationRoot = relationRoot;
    }

    internal RelationQueryNodeHandle<LogicalQueryNode> StructuralNode { get; }

    internal RelationQueryExpressionValueBinding? RelationRoot { get; }

    /// <summary>Typed CLR binding focused by the node.</summary>
    public RelationQueryExpressionValueBinding<TValue> Binding { get; }
}

/// <summary>
/// Typed pair returned by an expression-authored logical node that introduces a CLR value binding.
/// </summary>
/// <typeparam name="TNode">Canonical logical-node type referenced by <see cref="Node"/>.</typeparam>
/// <typeparam name="TValue">CLR type represented by <see cref="RelationQueryExpressionBoundNode{TValue}.Binding"/>.</typeparam>
public sealed class RelationQueryExpressionBoundNode<TNode, TValue> : RelationQueryExpressionBoundNode<TValue>
    where TNode : LogicalQueryNode
    where TValue : notnull
{
    internal RelationQueryExpressionBoundNode(
        RelationQueryNodeHandle<TNode> node,
        RelationQueryExpressionValueBinding<TValue> binding,
        RelationQueryExpressionValueBinding? relationRoot = null)
        : base(
            new RelationQueryNodeHandle<LogicalQueryNode>(Guard.RequireNotNull(node.Owner), node.Id),
            binding,
            relationRoot)
    {
        Node = node;
    }

    /// <summary>Structural handle for the canonical logical node.</summary>
    public RelationQueryNodeHandle<TNode> Node { get; }

    /// <summary>Builds this rooted output into a convention-identified relation without an output key.</summary>
    /// <param name="mode">Output cardinality relative to each root.</param>
    /// <param name="invariants">Optional already-canonical output invariants.</param>
    /// <param name="id">Optional explicit relation identity overriding the endpoint convention.</param>
    /// <param name="name">Optional explicit relation display name overriding the CLR output-type convention.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>The canonical relation, validation result, and authoring provenance.</returns>
    /// <exception cref="ArgumentException">
    /// The output is invalid for a relation terminal or <paramref name="invariants"/> contains a null entry.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">
    /// This handle has no unambiguous originating source. Use the explicit authoring overload that accepts a root.
    /// </exception>
    public RelationQueryAuthoringResult<RelationDefinition> BuildRelation(
        RelationOutputMode mode = RelationOutputMode.OnePerRoot,
        ImmutableArray<InvariantDefinition> invariants = default,
        RelationId? id = null,
        RelationName? name = null,
        string? sourceReference = null) =>
        Binding.Owner.BuildRelation(this, mode, invariants, id, name, sourceReference);

    /// <summary>Builds this rooted output into a convention-identified relation with an expression-authored key.</summary>
    /// <typeparam name="TKey">CLR output-key type.</typeparam>
    /// <param name="key">Stable non-null output-key expression.</param>
    /// <param name="mode">Output cardinality relative to each root.</param>
    /// <param name="invariants">Optional output invariants lowered before the terminal commits.</param>
    /// <param name="id">Optional explicit relation identity overriding the endpoint convention.</param>
    /// <param name="name">Optional explicit relation display name overriding the CLR output-type convention.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>The canonical relation, validation result, and authoring provenance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The output is invalid for a relation terminal, an invariant is null, or invariant names repeat.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">
    /// This handle has no unambiguous originating source. Use the explicit authoring overload that accepts a root.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The key or an invariant cannot be lowered exactly, or the key contains a raw CLR temporal carrier instead
    /// of an explicitly normalized canonical scalar; no relation terminal is committed.
    /// </exception>
    public RelationQueryAuthoringResult<RelationDefinition> BuildRelation<TKey>(
        Expression<Func<TValue, TKey>> key,
        RelationOutputMode mode = RelationOutputMode.OnePerRoot,
        IEnumerable<RelationQueryExpressionInvariant<TValue>>? invariants = null,
        RelationId? id = null,
        RelationName? name = null,
        string? sourceReference = null) =>
        Binding.Owner.BuildRelation(this, key, mode, invariants, id, name, sourceReference);
}

/// <summary>
/// Typed declaration of a runtime query parameter referenced by expression-authored semantics.
/// </summary>
/// <typeparam name="T">Supported CLR parameter type.</typeparam>
/// <remarks>
/// <see cref="Value"/> is an expression marker and must only occur inside a C# expression tree supplied
/// to the same authoring session. Reading it as ordinary CLR code is invalid. The expression translator
/// recognizes the framework-owned marker without evaluating user property getters or arbitrary captures.
/// </remarks>
public sealed class RelationQueryExpressionParameter<T> : IRelationQueryExpressionParameterMarker
{
    internal RelationQueryExpressionParameter(
        RelationQueryExpressionAuthoring owner,
        RelationQueryParameterHandle structural,
        bool isProvablyNonNull)
    {
        Owner = owner;
        Structural = structural;
        IsProvablyNonNull = isProvablyNonNull;
    }

    internal RelationQueryExpressionAuthoring Owner { get; }

    internal RelationQueryParameterHandle Structural { get; }

    QueryParameterId IRelationQueryExpressionParameterMarker.ParameterId => Id;

    RelationQueryExpressionAuthoring IRelationQueryExpressionParameterMarker.Owner => Owner;

    bool IRelationQueryExpressionParameterMarker.IsProvablyNonNull => IsProvablyNonNull;

    internal bool IsProvablyNonNull { get; }

    /// <summary>Canonical query-parameter identity.</summary>
    public QueryParameterId Id => Structural.Id;

    /// <summary>
    /// Marker used to reference this parameter inside an expression-authoring lambda.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The marker is evaluated as CLR code instead of being inspected as part of an expression tree.
    /// </exception>
    public T Value => throw new InvalidOperationException(
        "A relation/query parameter marker may only be used inside an expression-authoring lambda.");

    /// <inheritdoc />
    public override string ToString() => Id.ToString();
}

/// <summary>Untyped base for named query-result handles produced by expression authoring.</summary>
public abstract class RelationQueryExpressionResult
{
    private protected RelationQueryExpressionResult(
        RelationQueryExpressionAuthoring owner,
        RelationQueryResultHandle structural,
        QualifiedShapeId shape)
    {
        Owner = owner;
        Structural = structural;
        Shape = shape;
    }

    internal RelationQueryExpressionAuthoring Owner { get; }

    internal RelationQueryResultHandle Structural { get; }

    /// <summary>Canonical named-result identity.</summary>
    public QueryResultId Id => Structural.Id;

    /// <summary>Graph-qualified semantic shape of each result value.</summary>
    public QualifiedShapeId Shape { get; }

    /// <inheritdoc />
    public override string ToString() => Id.ToString();
}

/// <summary>Typed named row result produced by expression authoring.</summary>
/// <typeparam name="T">CLR projection type represented by each result row.</typeparam>
public sealed class RelationQueryExpressionRowsResult<T> : RelationQueryExpressionResult
    where T : notnull
{
    internal RelationQueryExpressionRowsResult(
        RelationQueryExpressionAuthoring owner,
        RelationQueryResultHandle<RowsQueryResultDefinition> structural,
        QualifiedShapeId shape)
        : base(owner, structural, shape)
    {
    }
}

/// <summary>Typed named aggregation result produced by expression authoring.</summary>
/// <typeparam name="T">CLR projection type represented by each aggregation row.</typeparam>
public sealed class RelationQueryExpressionAggregationResult<T> : RelationQueryExpressionResult
    where T : notnull
{
    internal RelationQueryExpressionAggregationResult(
        RelationQueryExpressionAuthoring owner,
        RelationQueryResultHandle<AggregationQueryResultDefinition> structural,
        QualifiedShapeId shape)
        : base(owner, structural, shape)
    {
    }
}
