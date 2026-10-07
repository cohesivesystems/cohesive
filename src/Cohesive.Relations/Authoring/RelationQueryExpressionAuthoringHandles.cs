using System.Collections.Immutable;
using Cohesive.Model.Serialization;
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
    /// <summary>Wraps an existing canonical relationship for typed authoring without redefining its endpoints.</summary>
    /// <param name="definition">Canonical relationship authority.</param>
    /// <remarks>CLR/shape compatibility is validated when a query session traverses the handle.</remarks>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    public RelationQueryExpressionRelationship(RelationshipDefinition definition)
    {
        Definition = Guard.RequireNotNull(definition);
    }

    /// <summary>Attaches authoritative endpoint documents for automatic import during traversal.</summary>
    /// <param name="definition">Canonical relationship authority.</param>
    /// <param name="source">Exact graph containing the source endpoint.</param>
    /// <param name="target">Exact graph containing the target endpoint.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A document does not contain its declared endpoint.</exception>
    public RelationQueryExpressionRelationship(RelationshipDefinition definition, ShapeGraphDocument source, ShapeGraphDocument target)
        : this(definition)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Graph.Id != definition.SourceShape.GraphId || source.Graph.TryGetShape(definition.SourceShape) is null
            || target.Graph.Id != definition.TargetShape.GraphId || target.Graph.TryGetShape(definition.TargetShape) is null)
            throw new ArgumentException("Relationship documents must contain the exact declared endpoints.");
        sourceDocument = source;
        targetDocument = target;
    }

    readonly ShapeGraphDocument? sourceDocument;
    readonly ShapeGraphDocument? targetDocument;

    internal void ImportEndpoints(RelationQueryExpressionAuthoring author)
    {
        if (sourceDocument is not null) author.Clr.Shape<TSource>(sourceDocument, SourceShape);
        if (targetDocument is not null) author.Clr.Shape<TTarget>(targetDocument, TargetShape);
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
        ClrMemberPathResolver? memberPathResolver,
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

    internal ClrMemberPathResolver? MemberPathResolver { get; }

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

/// <summary>One typed binding seam shared by explicit bindings and focused query nodes.</summary>
/// <typeparam name="T">Canonical CLR value type.</typeparam>
/// <remarks>Implementations expose an existing session-owned binding; they cannot bypass visibility checks.</remarks>
public interface IRelationQueryBinding<T> where T : notnull
{
    /// <summary>The exact focused binding, retaining its authoring session and semantic identity.</summary>
    RelationQueryExpressionValueBinding<T> Binding { get; }
    internal RelationQueryExpressionAuthoring Owner { get; }
}

/// <summary>Typed CLR value binding used as a parameter in expression-authoring lambdas.</summary>
/// <typeparam name="T">CLR type represented by the binding.</typeparam>
public sealed class RelationQueryExpressionValueBinding<T> : RelationQueryExpressionValueBinding, IRelationQueryBinding<T>
    where T : notnull
{
    internal RelationQueryExpressionValueBinding(
        RelationQueryExpressionAuthoring owner,
        RelationQueryBindingHandle structural,
        TypeRef type,
        QualifiedShapeId? shape,
        ClrMemberPathResolver? memberPathResolver = null,
        Func<Type, TypeRef>? typeResolver = null,
        bool usesImportedMapping = false)
        : base(owner, structural, type, shape, memberPathResolver, typeResolver, usesImportedMapping)
    {
    }

    RelationQueryExpressionValueBinding<T> IRelationQueryBinding<T>.Binding => this;
    RelationQueryExpressionAuthoring IRelationQueryBinding<T>.Owner => Owner;

    internal override Type ClrType => typeof(T);
}

/// <summary>Node-type-erased base for a logical node and its focused CLR value binding.</summary>
/// <typeparam name="TValue">CLR type represented by <see cref="Binding"/>.</typeparam>
/// <remarks>
/// The erased node and relation-root context are authoring conveniences only. Canonical definitions retain the
/// exact logical node, binding, and root identities rather than this handle or its CLR type.
/// </remarks>
public abstract class RelationQueryExpressionBoundNode<TValue> : IRelationQueryBinding<TValue>
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

    RelationQueryExpressionAuthoring IRelationQueryBinding<TValue>.Owner => Binding.Owner;

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

    /// <summary>Filters this focused branch through its owning authoring session.</summary>
    /// <param name="predicate">Canonical expression over the focused row.</param>
    /// <returns>The filtered branch.</returns>
    public RelationQueryExpressionBoundNode<FilterQueryNode, TValue> Where(Expression<Func<TValue, bool>> predicate) =>
        Binding.Owner.Where(this, predicate);

    /// <summary>Projects the focused row through the owning authoring session.</summary>
    /// <typeparam name="TResult">Projected row type.</typeparam>
    /// <param name="projection">Canonical projection expression.</param>
    /// <returns>The projected branch.</returns>
    /// <exception cref="ArgumentNullException">Projection is null.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The projection cannot be lowered exactly.</exception>
    public RelationQueryExpressionBoundNode<ProjectQueryNode, TResult> Select<TResult>(Expression<Func<TValue, TResult>> projection) where TResult : notnull =>
        Binding.Owner.Project(this, projection);

    /// <summary>Traverses an incoming relationship from this focused branch.</summary>
    /// <typeparam name="TSource">Related source entity type.</typeparam>
    /// <param name="relationship">Canonical relationship ending at this row type.</param>
    /// <returns>The related source branch using the default left traversal.</returns>
    public RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, TSource> TraverseInverse<TSource>(RelationQueryExpressionRelationship<TSource, TValue> relationship) where TSource : notnull =>
        Binding.Owner.TraverseInverse(this, relationship);

    /// <summary>Traverses an outgoing relationship from this focused branch.</summary>
    /// <typeparam name="TRelated">Related target entity type.</typeparam>
    /// <param name="relationship">Canonical relationship starting at this row type.</param>
    /// <returns>The related target branch using the default left traversal.</returns>
    public RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, TRelated> Traverse<TRelated>(RelationQueryExpressionRelationship<TValue, TRelated> relationship) where TRelated : notnull =>
        Binding.Owner.Traverse(this, Binding, relationship);

    /// <summary>Traverses a relationship and selects from the starting and related rows.</summary>
    /// <typeparam name="TRelated">Related entity type.</typeparam>
    /// <typeparam name="TResult">Projected row type.</typeparam>
    /// <param name="relationship">Canonical relationship in the requested direction.</param>
    /// <param name="resultSelector">Canonical expression over the starting row and related row.</param>
    /// <returns>A projected branch; traversal uses left semantics, preserving missing related values.</returns>
    /// <exception cref="ArgumentNullException">Relationship or result selector is null.</exception>
    /// <exception cref="ArgumentException">Relationship endpoint shapes are incompatible.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The selector cannot be lowered exactly.</exception>
    public RelationQueryExpressionBoundNode<ProjectQueryNode, TResult> Traverse<TRelated, TResult>(
        RelationQueryExpressionRelationship<TValue, TRelated> relationship, Expression<Func<TValue, TRelated, TResult>> resultSelector)
        where TRelated : notnull where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(resultSelector);
        var related = Binding.Owner.Traverse(this, Binding, relationship);
        var projected = Binding.Owner.Project(related.Node, resultSelector, Binding, related.Binding);
        return new(projected.Node, projected.Binding, RelationRoot);
    }

    /// <summary>Traverses a relationship and selects from the starting and related rows.</summary>
    /// <typeparam name="TSource">Related entity type.</typeparam>
    /// <typeparam name="TResult">Projected row type.</typeparam>
    /// <param name="relationship">Canonical relationship in the requested direction.</param>
    /// <param name="resultSelector">Canonical expression over the starting row and related row.</param>
    /// <returns>A projected branch; traversal uses left semantics, preserving missing related values.</returns>
    /// <exception cref="ArgumentNullException">Relationship or result selector is null.</exception>
    /// <exception cref="ArgumentException">Relationship endpoint shapes are incompatible.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The selector cannot be lowered exactly.</exception>
    public RelationQueryExpressionBoundNode<ProjectQueryNode, TResult> TraverseInverse<TSource, TResult>(
        RelationQueryExpressionRelationship<TSource, TValue> relationship, Expression<Func<TValue, TSource, TResult>> resultSelector)
        where TSource : notnull where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(resultSelector);
        var related = Binding.Owner.TraverseInverse(this, relationship);
        var projected = Binding.Owner.Project(related.Node, resultSelector, Binding, related.Binding);
        return new(projected.Node, projected.Binding, RelationRoot);
    }

    /// <summary>Inner-joins two focused branches using their existing session-owned bindings.</summary>
    /// <typeparam name="TRightNode">Right logical node type.</typeparam>
    /// <typeparam name="TRight">Right focused row type.</typeparam>
    /// <param name="right">Other branch from the same session.</param>
    /// <param name="predicate">Join predicate over the two focused rows.</param>
    /// <returns>A joined branch retaining both typed bindings for fluent projection.</returns>
    /// <exception cref="ArgumentNullException">Right branch or predicate is null.</exception>
    public RelationQueryExpressionJoinedNode<TValue, TRight> Join<TRightNode, TRight>(RelationQueryExpressionBoundNode<TRightNode, TRight> right,
        Expression<Func<TValue, TRight, bool>> predicate)
        where TRightNode : LogicalQueryNode where TRight : notnull
        => JoinCore(right, predicate, JoinKind.Inner);

    /// <summary>Left-joins another branch, preserving rows with no matching right value.</summary>
    /// <typeparam name="TRightNode">Right logical node type.</typeparam>
    /// <typeparam name="TRight">Right focused row type.</typeparam>
    /// <param name="right">Other branch from the same session.</param>
    /// <param name="predicate">Canonical equality or join predicate.</param>
    /// <returns>A joined branch retaining both bindings; missing right values remain canonical missing values.</returns>
    /// <exception cref="ArgumentNullException">Right branch or predicate is null.</exception>
    /// <exception cref="ArgumentException">The right branch belongs to another session.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The predicate cannot be lowered exactly.</exception>
    public RelationQueryExpressionJoinedNode<TValue, TRight> LeftJoin<TRightNode, TRight>(
        RelationQueryExpressionBoundNode<TRightNode, TRight> right, Expression<Func<TValue, TRight, bool>> predicate)
        where TRightNode : LogicalQueryNode where TRight : notnull
        => JoinCore(right, predicate, JoinKind.Left);

    RelationQueryExpressionJoinedNode<TValue, TRight> JoinCore<TRightNode, TRight>(
        RelationQueryExpressionBoundNode<TRightNode, TRight> right, Expression<Func<TValue, TRight, bool>> predicate, JoinKind kind)
        where TRightNode : LogicalQueryNode where TRight : notnull
    {
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(predicate);
        return new(Binding.Owner.Join(Node, right.Node, kind, predicate, Binding, right.Binding), Binding, right.Binding);
    }

    /// <summary>Captures an array-result query definition; does not execute or compile the query.</summary>
    /// <typeparam name="TInput">Invocation parameter type.</typeparam>
    /// <param name="id">Stable canonical query identity.</param>
    /// <param name="name">Human-readable query name.</param>
    /// <param name="parameter">The single invocation parameter from this session.</param>
    /// <returns>An immutable definition returning all complete rows, or an empty array for no rows. Ordering is only that declared by the query.</returns>
    /// <exception cref="ArgumentNullException">Parameter is null.</exception>
    /// <exception cref="ArgumentException">Parameter belongs to another session or the query has other parameters.</exception>
    public RelationQuery<TInput, TValue[]> BuildArrayQuery<TInput>(QueryId id, QueryName name, RelationQueryExpressionParameter<TInput> parameter) =>
        Binding.Owner.BuildArrayQuery(id, name, this, parameter);


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

/// <summary>Authoring handle retaining the two focused bindings of a join; no additional canonical model.</summary>
/// <typeparam name="TLeft">Left focused row.</typeparam>
/// <typeparam name="TRight">Right focused row.</typeparam>
public sealed class RelationQueryExpressionJoinedNode<TLeft, TRight> where TLeft : notnull where TRight : notnull
{
    readonly RelationQueryExpressionValueBinding<TLeft> left;
    readonly RelationQueryExpressionValueBinding<TRight> right;
    internal RelationQueryExpressionJoinedNode(RelationQueryNodeHandle<JoinQueryNode> node,
        RelationQueryExpressionValueBinding<TLeft> left, RelationQueryExpressionValueBinding<TRight> right)
    { Node = node; this.left = left; this.right = right; }

    /// <summary>Canonical structural join for explicit graph authoring.</summary>
    public RelationQueryNodeHandle<JoinQueryNode> Node { get; }

    /// <summary>Projects both joined rows through their existing authoring session.</summary>
    /// <typeparam name="TResult">Projected row type.</typeparam>
    /// <param name="projection">Canonical expression over left and right bindings.</param>
    /// <returns>The focused projected branch.</returns>
    /// <exception cref="ArgumentNullException">Projection is null.</exception>
    public RelationQueryExpressionBoundNode<ProjectQueryNode, TResult> Select<TResult>(Expression<Func<TLeft, TRight, TResult>> projection)
        where TResult : notnull => left.Owner.Project(Node, projection, left, right);
}
