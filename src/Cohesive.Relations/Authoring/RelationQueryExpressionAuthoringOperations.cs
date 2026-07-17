using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

/// <summary>Transient authored invariant lowered at a relation terminal.</summary>
/// <typeparam name="T">CLR relation-output type inspected by the invariant.</typeparam>
/// <remarks>
/// This object belongs only to the C# authoring frontend. A successful terminal retains the invariant's
/// canonical expression and never retains this object or its expression tree.
/// </remarks>
public sealed record RelationQueryExpressionInvariant<T>
    where T : notnull
{
    /// <summary>Creates an expression-authored relation invariant.</summary>
    /// <param name="name">Non-empty, terminal-unique invariant name.</param>
    /// <param name="predicate">Boolean predicate over the relation output.</param>
    /// <param name="message">Optional diagnostic message used when the invariant is violated.</param>
    /// <param name="entity">Optional entity identity associated with the invariant.</param>
    /// <param name="sourceReference">Optional stable producer reference for expression provenance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    public RelationQueryExpressionInvariant(
        string name,
        Expression<Func<T, bool>> predicate,
        string? message = null,
        EntityId? entity = null,
        string? sourceReference = null)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Predicate = Guard.RequireNotNull(predicate);
        Message = message;
        Entity = entity;
        SourceReference = sourceReference.TrimmedEmptyOrWhiteSpaceAs();
    }

    /// <summary>Stable invariant name.</summary>
    public string Name { get; }

    /// <summary>Boolean predicate over the relation output.</summary>
    public Expression<Func<T, bool>> Predicate { get; }

    /// <summary>Optional diagnostic message used when the invariant is violated.</summary>
    public string? Message { get; }

    /// <summary>Optional entity identity associated with the invariant.</summary>
    public EntityId? Entity { get; }

    /// <summary>Optional stable producer reference for expression provenance.</summary>
    public string? SourceReference { get; }
}

public sealed partial class RelationQueryExpressionAuthoring
{
    RelationQueryExpressionLowerer Lowerer => ExpressionLowerer;

    /// <summary>Filters a logical branch using an arbitrary-width typed CLR lambda.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Logical input to filter.</param>
    /// <param name="predicate">Boolean lambda whose parameters correspond positionally to <paramref name="bindings"/>.</param>
    /// <param name="bindings">Visible typed bindings corresponding to the lambda parameters.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance and diagnostics.</param>
    /// <returns>A structural handle for the canonical filter node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="predicate"/> or <paramref name="bindings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, is not visible in <paramref name="input"/>, or lambda
    /// parameters do not match the binding CLR types.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The predicate contains unsupported or ambiguous C# semantics.
    /// </exception>
    public RelationQueryNodeHandle<FilterQueryNode> Filter<TInput>(
        RelationQueryNodeHandle<TInput> input,
        LambdaExpression predicate,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
    {
        var reference = sourceReference ?? "filter";
        RequireReturnType(predicate, typeof(bool), nameof(predicate));
        var handles = RequireBindings(predicate, bindings);
        RequireBindingsVisible(input, handles, nameof(bindings));
        var lowered = Lowerer.LowerValue(predicate, handles, reference).RequireValue();
        return structural.Filter(
            input,
            lowered.Value,
            source: Source(reference, "Expression-authored filter."),
            predicateSource: lowered.Source);
    }

    /// <summary>Filters a logical branch using one typed binding.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="T1">CLR type of the first binding.</typeparam>
    /// <param name="input">Logical input to filter.</param>
    /// <param name="predicate">Boolean predicate over the binding.</param>
    /// <param name="binding">Binding corresponding to the lambda parameter.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical filter node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The binding belongs to another session, is not visible in <paramref name="input"/>, or has a
    /// mismatched CLR type.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The predicate cannot be lowered exactly.</exception>
    public RelationQueryNodeHandle<FilterQueryNode> Filter<TInput, T1>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<T1, bool>> predicate,
        RelationQueryExpressionValueBinding<T1> binding,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where T1 : notnull =>
        Filter(input, predicate, [binding], sourceReference);

    /// <summary>Filters a logical branch using two typed bindings.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="T1">CLR type of the first binding.</typeparam>
    /// <typeparam name="T2">CLR type of the second binding.</typeparam>
    /// <param name="input">Logical input to filter.</param>
    /// <param name="predicate">Boolean predicate over both bindings.</param>
    /// <param name="first">Binding corresponding to the first lambda parameter.</param>
    /// <param name="second">Binding corresponding to the second lambda parameter.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical filter node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, is not visible in <paramref name="input"/>, or has a mismatched CLR type.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The predicate cannot be lowered exactly.</exception>
    public RelationQueryNodeHandle<FilterQueryNode> Filter<TInput, T1, T2>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<T1, T2, bool>> predicate,
        RelationQueryExpressionValueBinding<T1> first,
        RelationQueryExpressionValueBinding<T2> second,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where T1 : notnull
        where T2 : notnull =>
        Filter(input, predicate, [first, second], sourceReference);

    /// <summary>Filters a logical branch using three typed bindings.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="T1">CLR type of the first binding.</typeparam>
    /// <typeparam name="T2">CLR type of the second binding.</typeparam>
    /// <typeparam name="T3">CLR type of the third binding.</typeparam>
    /// <param name="input">Logical input to filter.</param>
    /// <param name="predicate">Boolean predicate over all bindings.</param>
    /// <param name="first">Binding corresponding to the first lambda parameter.</param>
    /// <param name="second">Binding corresponding to the second lambda parameter.</param>
    /// <param name="third">Binding corresponding to the third lambda parameter.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical filter node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, is not visible in <paramref name="input"/>, or has a mismatched CLR type.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The predicate cannot be lowered exactly.</exception>
    public RelationQueryNodeHandle<FilterQueryNode> Filter<TInput, T1, T2, T3>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<T1, T2, T3, bool>> predicate,
        RelationQueryExpressionValueBinding<T1> first,
        RelationQueryExpressionValueBinding<T2> second,
        RelationQueryExpressionValueBinding<T3> third,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where T1 : notnull
        where T2 : notnull
        where T3 : notnull =>
        Filter(input, predicate, [first, second, third], sourceReference);

    /// <summary>Projects a logical branch through an arbitrary-width CLR object lambda.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="TResult">CLR projection type.</typeparam>
    /// <param name="input">Logical input to project.</param>
    /// <param name="resultShape">Typed deterministic or explicitly registered result shape.</param>
    /// <param name="projection">Object lambda whose parameters correspond positionally to <paramref name="bindings"/>.</param>
    /// <param name="bindings">Visible typed bindings corresponding to the lambda parameters.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance and diagnostics.</param>
    /// <returns>Typed projection-node and result-binding handles.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resultShape"/>, <paramref name="projection"/>, or <paramref name="bindings"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, is not visible in <paramref name="input"/>, or lambda
    /// parameters do not match the binding CLR types.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The projection contains unsupported or ambiguous C# semantics.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TResult"/> is already bound to another semantic shape in this session.
    /// </exception>
    public RelationQueryExpressionBoundNode<ProjectQueryNode, TResult> Project<TInput, TResult>(
        RelationQueryNodeHandle<TInput> input,
        RelationQueryClrShape<TResult> resultShape,
        LambdaExpression projection,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(resultShape);
        var reference = sourceReference ?? $"project/{StableTypeName(typeof(TResult))}";
        RequireReturnType(projection, typeof(TResult), nameof(projection));
        var handles = RequireBindings(projection, bindings);
        RequireBindingsVisible(input, handles, nameof(bindings));
        TrackShape(resultShape);
        var lowered = Lowerer.LowerProjection(projection, handles, reference).RequireValue();
        var projected = structural.Project(
            input,
            resultShape.Id,
            lowered.Assignments,
            source: lowered.NodeSource,
            bindingSource: lowered.BindingSource);
        return new(
            projected.Node,
            new RelationQueryExpressionValueBinding<TResult>(
                this,
                projected.Binding,
                resultShape.Type,
                resultShape.Id,
                resultShape.ResolveMemberPath,
                resultShape.ResolveType,
                resultShape.IdentityOrigin == RelationQueryClrIdentityOrigin.Imported));
    }

    /// <summary>Projects one typed binding to a deterministic CLR result shape.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="T1">CLR type of the source binding.</typeparam>
    /// <typeparam name="TResult">CLR projection type.</typeparam>
    /// <param name="input">Logical input to project.</param>
    /// <param name="projection">Object projection over the source binding.</param>
    /// <param name="binding">Binding corresponding to the lambda parameter.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>Typed projection-node and result-binding handles.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The binding belongs to another session, is not visible in <paramref name="input"/>, or has a
    /// mismatched CLR type.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TResult"/> cannot be represented by the configured CLR shape context.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The projection cannot be lowered exactly.</exception>
    public RelationQueryExpressionBoundNode<ProjectQueryNode, TResult> Project<TInput, T1, TResult>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<T1, TResult>> projection,
        RelationQueryExpressionValueBinding<T1> binding,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where T1 : notnull
        where TResult : notnull =>
        Project(input, clr.Shape<TResult>(), projection, [binding], sourceReference);

    /// <summary>Projects two typed bindings to a deterministic CLR result shape.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="T1">CLR type of the first binding.</typeparam>
    /// <typeparam name="T2">CLR type of the second binding.</typeparam>
    /// <typeparam name="TResult">CLR projection type.</typeparam>
    /// <param name="input">Logical input to project.</param>
    /// <param name="projection">Object projection over both bindings.</param>
    /// <param name="first">Binding corresponding to the first lambda parameter.</param>
    /// <param name="second">Binding corresponding to the second lambda parameter.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>Typed projection-node and result-binding handles.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, is not visible in <paramref name="input"/>, or has a mismatched CLR type.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TResult"/> cannot be represented by the configured CLR shape context.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The projection cannot be lowered exactly.</exception>
    public RelationQueryExpressionBoundNode<ProjectQueryNode, TResult> Project<TInput, T1, T2, TResult>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<T1, T2, TResult>> projection,
        RelationQueryExpressionValueBinding<T1> first,
        RelationQueryExpressionValueBinding<T2> second,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where T1 : notnull
        where T2 : notnull
        where TResult : notnull =>
        Project(input, clr.Shape<TResult>(), projection, [first, second], sourceReference);

    /// <summary>Projects three typed bindings to a deterministic CLR result shape.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="T1">CLR type of the first binding.</typeparam>
    /// <typeparam name="T2">CLR type of the second binding.</typeparam>
    /// <typeparam name="T3">CLR type of the third binding.</typeparam>
    /// <typeparam name="TResult">CLR projection type.</typeparam>
    /// <param name="input">Logical input to project.</param>
    /// <param name="projection">Object projection over all bindings.</param>
    /// <param name="first">Binding corresponding to the first lambda parameter.</param>
    /// <param name="second">Binding corresponding to the second lambda parameter.</param>
    /// <param name="third">Binding corresponding to the third lambda parameter.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>Typed projection-node and result-binding handles.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, is not visible in <paramref name="input"/>, or has a mismatched CLR type.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TResult"/> cannot be represented by the configured CLR shape context.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The projection cannot be lowered exactly.</exception>
    public RelationQueryExpressionBoundNode<ProjectQueryNode, TResult> Project<TInput, T1, T2, T3, TResult>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<T1, T2, T3, TResult>> projection,
        RelationQueryExpressionValueBinding<T1> first,
        RelationQueryExpressionValueBinding<T2> second,
        RelationQueryExpressionValueBinding<T3> third,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where T1 : notnull
        where T2 : notnull
        where T3 : notnull
        where TResult : notnull =>
        Project(input, clr.Shape<TResult>(), projection, [first, second, third], sourceReference);

    /// <summary>Joins two logical branches using a typed arbitrary-width correlation predicate.</summary>
    /// <typeparam name="TLeft">Canonical type of the left node.</typeparam>
    /// <typeparam name="TRight">Canonical type of the right node.</typeparam>
    /// <param name="left">Left logical branch.</param>
    /// <param name="right">Right logical branch.</param>
    /// <param name="kind">Canonical join semantics.</param>
    /// <param name="predicate">Correlation lambda whose parameters correspond to <paramref name="bindings"/>.</param>
    /// <param name="bindings">Visible bindings used by the predicate.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical join node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="predicate"/> or <paramref name="bindings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, is not visible in either input branch, or has a mismatched CLR type.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The predicate cannot be lowered exactly.</exception>
    public RelationQueryNodeHandle<JoinQueryNode> Join<TLeft, TRight>(
        RelationQueryNodeHandle<TLeft> left,
        RelationQueryNodeHandle<TRight> right,
        JoinKind kind,
        LambdaExpression predicate,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string? sourceReference = null)
        where TLeft : LogicalQueryNode
        where TRight : LogicalQueryNode
    {
        var reference = sourceReference ?? "join";
        RequireReturnType(predicate, typeof(bool), nameof(predicate));
        var handles = RequireBindings(predicate, bindings);
        RequireBindingsVisibleInEither(left, right, handles, nameof(bindings));
        var lowered = Lowerer.LowerValue(predicate, handles, reference).RequireValue();
        return structural.Join(
            left,
            right,
            kind,
            lowered.Value,
            source: Source(reference, "Expression-authored explicit join."),
            predicateSource: lowered.Source);
    }

    /// <summary>Joins two logical branches using two typed bindings.</summary>
    /// <typeparam name="TLeft">Canonical type of the left node.</typeparam>
    /// <typeparam name="TRight">Canonical type of the right node.</typeparam>
    /// <typeparam name="T1">CLR type of the first binding.</typeparam>
    /// <typeparam name="T2">CLR type of the second binding.</typeparam>
    /// <param name="left">Left logical branch.</param>
    /// <param name="right">Right logical branch.</param>
    /// <param name="kind">Canonical join semantics.</param>
    /// <param name="predicate">Correlation predicate over both bindings.</param>
    /// <param name="first">First predicate binding.</param>
    /// <param name="second">Second predicate binding.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical join node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, is not visible in either input branch, or has a mismatched CLR type.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The predicate cannot be lowered exactly.</exception>
    public RelationQueryNodeHandle<JoinQueryNode> Join<TLeft, TRight, T1, T2>(
        RelationQueryNodeHandle<TLeft> left,
        RelationQueryNodeHandle<TRight> right,
        JoinKind kind,
        Expression<Func<T1, T2, bool>> predicate,
        RelationQueryExpressionValueBinding<T1> first,
        RelationQueryExpressionValueBinding<T2> second,
        string? sourceReference = null)
        where TLeft : LogicalQueryNode
        where TRight : LogicalQueryNode
        where T1 : notnull
        where T2 : notnull =>
        Join(left, right, kind, predicate, [first, second], sourceReference);

    /// <summary>Expands a collection selected by an arbitrary-width typed lambda.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="TItem">CLR type of each collection element.</typeparam>
    /// <param name="input">Logical input to expand.</param>
    /// <param name="collection">Collection-valued lambda whose parameters correspond to <paramref name="bindings"/>.</param>
    /// <param name="bindings">Visible bindings used by the collection expression.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>Typed expansion-node and item-binding handles.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="collection"/> or <paramref name="bindings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A binding belongs to another session, is not visible in <paramref name="input"/>, or has a mismatched CLR type.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The collection expression cannot be lowered exactly or combines complex items whose authoritative
    /// member-path provenance cannot be proven.
    /// </exception>
    public RelationQueryExpressionBoundNode<ExpandCollectionQueryNode, TItem> Expand<TInput, TItem>(
        RelationQueryNodeHandle<TInput> input,
        LambdaExpression collection,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TItem : notnull
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(bindings);
        var reference = sourceReference ?? $"expand/{StableTypeName(typeof(TItem))}";
        if (!typeof(IEnumerable<TItem>).IsAssignableFrom(collection.ReturnType))
        {
            throw new ArgumentException(
                $"The collection lambda returns '{collection.ReturnType}', which is not an enumerable of '{typeof(TItem)}'.",
                nameof(collection));
        }
        var handles = RequireBindings(collection, bindings);
        RequireBindingsVisible(input, handles, nameof(bindings));
        var lowered = Lowerer.LowerValue(collection, handles, reference).RequireValue();
        var provenance = ResolveCollectionBindingProvenance(collection, handles);
        if (provenance.IsAmbiguous)
        {
            throw new RelationQueryExpressionAuthoringException(
            [
                new RelationQueryExpressionDiagnostic(
                    RelationQueryExpressionDiagnosticCodes.MemberPathUnavailable,
                    DiagnosticSeverity.Error,
                    "The collection expression combines items from different semantic bindings, so one authoritative member mapping cannot be proven.",
                    expressionPath: "body",
                    reference,
                    symbol: StableTypeName(typeof(TItem)),
                    suggestion: "Project both collection branches to one canonical item shape before expansion, or author the expansion structurally with an explicit item type and paths.")
            ]);
        }

        QualifiedShapeId? itemShape = null;
        RelationQueryExpressionMemberPathResolver? itemMemberPathResolver = null;
        Func<Type, TypeRef>? itemTypeResolver = null;
        var usesImportedMapping = false;
        TypeRef itemType;
        if (provenance.Binding?.TypeResolver is { } sourceTypeResolver)
        {
            itemType = sourceTypeResolver(typeof(TItem));
            itemMemberPathResolver = provenance.Binding.MemberPathResolver;
            itemTypeResolver = sourceTypeResolver;
            usesImportedMapping = provenance.Binding.UsesImportedMapping;
        }
        else if (clr.TryGetRegisteredShape<TItem>() is { } registeredItemShape)
        {
            TrackShape(registeredItemShape);
            itemType = registeredItemShape.Type;
            itemShape = registeredItemShape.Id;
            itemMemberPathResolver = registeredItemShape.ResolveMemberPath;
            itemTypeResolver = registeredItemShape.ResolveType;
            usesImportedMapping = registeredItemShape.IdentityOrigin == RelationQueryClrIdentityOrigin.Imported;
        }
        else
        {
            itemType = clr.GetTypeRef(typeof(TItem));
        }

        if (itemType is NamedTypeRef or ObjectTypeRef)
        {
            if (itemMemberPathResolver is not null && !usesImportedMapping)
            {
                var inferredItemShape = clr.TryGetRegisteredShape<TItem>();
                if (inferredItemShape is not { IdentityOrigin: RelationQueryClrIdentityOrigin.Imported })
                {
                    inferredItemShape ??= clr.Shape<TItem>();
                    TrackShape(inferredItemShape);
                    itemShape = inferredItemShape.Id;
                    itemMemberPathResolver = inferredItemShape.ResolveMemberPath;
                    itemTypeResolver = inferredItemShape.ResolveType;
                    itemType = inferredItemShape.Type;
                }
            }

            if (itemMemberPathResolver is null)
            {
                var registeredItemShape = clr.Shape<TItem>();
                TrackShape(registeredItemShape);
                itemShape = registeredItemShape.Id;
                itemMemberPathResolver = registeredItemShape.ResolveMemberPath;
                itemTypeResolver = registeredItemShape.ResolveType;
                itemType = registeredItemShape.Type;
                usesImportedMapping = registeredItemShape.IdentityOrigin == RelationQueryClrIdentityOrigin.Imported;
            }
        }

        var expanded = structural.Expand(
            input,
            lowered.Value,
            itemType,
            source: Source(reference, $"Collection expansion to '{StableTypeName(typeof(TItem))}'."),
            bindingSource: Source(reference + "/binding", "Expanded collection-item binding."),
            collectionSource: lowered.Source);

        return new(
            expanded.Node,
            new RelationQueryExpressionValueBinding<TItem>(
                this,
                expanded.Binding,
                itemType,
                itemShape,
                itemMemberPathResolver,
                itemTypeResolver,
                usesImportedMapping));
    }

    static CollectionBindingProvenance ResolveCollectionBindingProvenance(
        LambdaExpression collection,
        ImmutableArray<RelationQueryExpressionValueBinding> bindings) =>
        ResolveCollectionBindingProvenance(collection.Body, collection.Parameters, bindings);

    static CollectionBindingProvenance ResolveCollectionBindingProvenance(
        Expression expression,
        IReadOnlyList<ParameterExpression> parameters,
        ImmutableArray<RelationQueryExpressionValueBinding> bindings)
    {
        while (expression is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
        {
            expression = unary.Operand;
        }

        if (expression is MemberExpression member)
        {
            Expression? root = member;
            while (root is MemberExpression nested)
                root = nested.Expression;
            if (root is ParameterExpression parameter)
            {
                for (var index = 0; index < parameters.Count; index++)
                {
                    if (ReferenceEquals(parameters[index], parameter))
                        return new(bindings[index], IsAmbiguous: false);
                }
            }

            return default;
        }

        if (expression is ConditionalExpression conditional)
        {
            return CombineCollectionProvenance(
                ResolveCollectionBindingProvenance(conditional.IfTrue, parameters, bindings),
                ResolveCollectionBindingProvenance(conditional.IfFalse, parameters, bindings));
        }

        if (expression is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
        {
            return CombineCollectionProvenance(
                ResolveCollectionBindingProvenance(coalesce.Left, parameters, bindings),
                ResolveCollectionBindingProvenance(coalesce.Right, parameters, bindings));
        }

        return default;
    }

    static CollectionBindingProvenance CombineCollectionProvenance(
        CollectionBindingProvenance left,
        CollectionBindingProvenance right)
    {
        if (left.IsAmbiguous || right.IsAmbiguous)
            return new(null, IsAmbiguous: true);
        if (left.Binding is null && right.Binding is null)
            return default;
        if (left.Binding is null || right.Binding is null)
            return new(null, IsAmbiguous: true);
        return ReferenceEquals(left.Binding, right.Binding)
            ? left
            : new(null, IsAmbiguous: true);
    }

    readonly record struct CollectionBindingProvenance(
        RelationQueryExpressionValueBinding? Binding,
        bool IsAmbiguous);

    /// <summary>Expands a collection selected from one typed binding.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="T1">CLR type of the source binding.</typeparam>
    /// <typeparam name="TItem">CLR type of each collection element.</typeparam>
    /// <param name="input">Logical input to expand.</param>
    /// <param name="collection">Collection selector over the source binding.</param>
    /// <param name="binding">Binding corresponding to the selector parameter.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>Typed expansion-node and item-binding handles.</returns>
    /// <exception cref="ArgumentException">
    /// The binding belongs to another session, is not visible in <paramref name="input"/>, or has a
    /// mismatched CLR type.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The collection expression cannot be lowered exactly.</exception>
    public RelationQueryExpressionBoundNode<ExpandCollectionQueryNode, TItem> Expand<TInput, T1, TItem>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<T1, IEnumerable<TItem>>> collection,
        RelationQueryExpressionValueBinding<T1> binding,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where T1 : notnull
        where TItem : notnull =>
        Expand<TInput, TItem>(input, collection, [binding], sourceReference);

    /// <summary>Removes whole-row duplicates from a logical branch.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Logical branch to de-duplicate.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>This operation does not return because untyped whole-row equality cannot prove portable carriers.</returns>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// Always thrown because whole-row distinctness cannot prove that every visible field uses a
    /// carrier-independent canonical equality domain. Use typed keys or the structural escape hatch.
    /// </exception>
    public RelationQueryNodeHandle<DistinctQueryNode> Distinct<TInput>(
        RelationQueryNodeHandle<TInput> input,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
    {
        var reference = sourceReference ?? "distinct";
        throw new RelationQueryExpressionAuthoringException(
        [
            new RelationQueryExpressionDiagnostic(
                RelationQueryExpressionDiagnosticCodes.KeyDomainUnsupported,
                DiagnosticSeverity.Error,
                "Expression-authored whole-row distinctness cannot prove carrier-independent equality for every visible field.",
                expressionPath: "row",
                reference,
                suggestion: "Declare one or more explicitly normalized typed distinct keys, or use the structural builder when backend equality policy is intentional.")
        ]);
    }

    /// <summary>Orders a logical branch by one typed expression.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="T1">CLR type of the source binding.</typeparam>
    /// <typeparam name="TKey">CLR type of the ordering key.</typeparam>
    /// <param name="input">Logical branch to order.</param>
    /// <param name="key">Ordering-key expression.</param>
    /// <param name="binding">Binding corresponding to the key lambda parameter.</param>
    /// <param name="direction">Sort direction.</param>
    /// <param name="nullPlacement">Placement of null or missing values.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical order node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The binding belongs to another session, is not visible in <paramref name="input"/>, or has a
    /// mismatched CLR type.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/> or <paramref name="nullPlacement"/> is unsupported.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The ordering key cannot be lowered exactly or returns a raw CLR temporal carrier instead of an explicitly
    /// normalized canonical scalar key.
    /// </exception>
    public RelationQueryNodeHandle<OrderQueryNode> Order<TInput, T1, TKey>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<T1, TKey>> key,
        RelationQueryExpressionValueBinding<T1> binding,
        QuerySortDirection direction = QuerySortDirection.Ascending,
        QueryNullPlacement nullPlacement = QueryNullPlacement.Last,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where T1 : notnull
    {
        var reference = sourceReference ?? "order";
        RequireCarrierIndependentKey(key, "ordering", reference + "/key");
        var handles = RequireBindings(key, [binding]);
        RequireBindingsVisible(input, handles, nameof(binding));
        var lowered = Lowerer.LowerValue(key, handles, reference + "/key").RequireValue();
        return structural.Order(
            input,
            [new RelationQueryOrderingInput(lowered.Value, direction, nullPlacement, lowered.Source)],
            source: Source(reference, "Expression-authored ordering."));
    }

    /// <summary>Applies an explicit canonical page request to a logical branch.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Logical branch to page.</param>
    /// <param name="page">Offset or keyset page semantics.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical page node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another authoring core, or a keyset continuation expression
    /// references an input-row field instead of a constant or query parameter.
    /// </exception>
    public RelationQueryNodeHandle<PageQueryNode> Page<TInput>(
        RelationQueryNodeHandle<TInput> input,
        QueryPageDefinition page,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page is KeysetPageDefinition keyset
            && keyset.After.Any(ContainsRowReference))
        {
            throw new ArgumentException(
                "Keyset continuation expressions cannot reference input-row fields; use constants or query parameters.",
                nameof(page));
        }

        var reference = sourceReference ?? "page";
        return structural.Page(input, page, source: Source(reference, "Expression-authored paging."));
    }

    /// <summary>Builds a canonical relation with an expression-authored output key and invariants.</summary>
    /// <typeparam name="TRoot">CLR root type.</typeparam>
    /// <typeparam name="TOutputNode">Canonical type of the output node.</typeparam>
    /// <typeparam name="TOutput">CLR output type.</typeparam>
    /// <typeparam name="TKey">CLR output-key type.</typeparam>
    /// <param name="id">Stable canonical relation identity.</param>
    /// <param name="name">Human-readable canonical relation name.</param>
    /// <param name="root">Root source binding.</param>
    /// <param name="output">Logical node producing relation outputs.</param>
    /// <param name="outputBinding">Typed relation-output binding.</param>
    /// <param name="key">Stable output-key expression, or <see langword="null"/> when no key is declared.</param>
    /// <param name="mode">Output cardinality relative to each root.</param>
    /// <param name="invariants">Optional output invariants lowered before the terminal commits.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>The canonical relation, validation result, and authoring provenance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="root"/> or <paramref name="outputBinding"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A handle belongs to another session, <paramref name="outputBinding"/> is not visible in
    /// <paramref name="output"/>, <paramref name="output"/> exposes more than one visible binding, an
    /// invariant is null, or invariant names repeat.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unsupported.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The key or an invariant cannot be lowered exactly, or the key contains a raw CLR temporal carrier
    /// instead of an explicitly normalized canonical scalar; no relation terminal is committed.
    /// </exception>
    public RelationQueryAuthoringResult<RelationDefinition> BuildRelation<TRoot, TOutputNode, TOutput, TKey>(
        RelationId id,
        RelationName name,
        RelationQueryExpressionValueBinding<TRoot> root,
        RelationQueryNodeHandle<TOutputNode> output,
        RelationQueryExpressionValueBinding<TOutput> outputBinding,
        Expression<Func<TOutput, TKey>>? key,
        RelationOutputMode mode = RelationOutputMode.OnePerRoot,
        IEnumerable<RelationQueryExpressionInvariant<TOutput>>? invariants = null,
        string? sourceReference = null)
        where TRoot : notnull
        where TOutputNode : LogicalQueryNode
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(outputBinding);
        RequireOwner(root);
        RequireBindingVisible(output, outputBinding, nameof(outputBinding));
        RequireSingleVisibleBinding(output, nameof(output));
        var reference = sourceReference ?? $"relation/{id.Value}";

        Expr? loweredKey = null;
        RelationQueryAuthoringSource? keySource = null;
        if (key is not null)
        {
            RequireCarrierIndependentKey(key, "relation output", reference + "/key");
            var lowered = Lowerer.LowerValue(
                key,
                [outputBinding],
                reference + "/key").RequireValue();
            loweredKey = lowered.Value;
            keySource = lowered.Source;
        }

        var authoredInvariants = invariants?.ToImmutableArray() ?? [];
        if (authoredInvariants.Any(static invariant => invariant is null))
            throw new ArgumentException("Relation invariants cannot contain null entries.", nameof(invariants));
        if (authoredInvariants.Select(static invariant => invariant.Name).Distinct(StringComparer.Ordinal).Count()
            != authoredInvariants.Length)
        {
            throw new ArgumentException("Relation invariant names must be unique.", nameof(invariants));
        }

        var canonicalInvariants = new InvariantDefinition[authoredInvariants.Length];
        var invariantSources = new RelationQueryAuthoringSource?[authoredInvariants.Length];
        for (var index = 0; index < authoredInvariants.Length; index++)
        {
            var invariant = authoredInvariants[index];
            var lowered = Lowerer.LowerValue(
                invariant.Predicate,
                [outputBinding],
                invariant.SourceReference ?? $"{reference}/invariants/{invariant.Name}").RequireValue();
            canonicalInvariants[index] = new(
                invariant.Name,
                lowered.Value,
                invariant.Message,
                invariant.Entity);
            invariantSources[index] = lowered.Source;
        }

        return structural.BuildRelation(
            id,
            name,
            root.Structural,
            output,
            RequireShape(outputBinding),
            mode,
            loweredKey,
            [.. canonicalInvariants],
            Source(reference, $"Expression-authored relation '{name.Value}'."),
            keySource,
            [.. invariantSources]);
    }

    internal void RequireBindingVisible<TInput>(
        RelationQueryNodeHandle<TInput> input,
        RelationQueryExpressionValueBinding binding,
        string parameterName)
        where TInput : LogicalQueryNode
    {
        RequireOwner(binding);
        if (!structural.IsBindingVisible(input, binding.Structural))
        {
            throw new ArgumentException(
                $"Binding '{binding.Id.Value}' is not visible in logical node '{input.Id.Value}'.",
                parameterName);
        }
    }

    internal void RequireBindingsVisible<TInput>(
        RelationQueryNodeHandle<TInput> input,
        IEnumerable<RelationQueryExpressionValueBinding> bindings,
        string parameterName)
        where TInput : LogicalQueryNode
    {
        foreach (var binding in bindings)
            RequireBindingVisible(input, binding, parameterName);
    }

    internal void RequireBindingsVisibleInEither<TLeft, TRight>(
        RelationQueryNodeHandle<TLeft> left,
        RelationQueryNodeHandle<TRight> right,
        IEnumerable<RelationQueryExpressionValueBinding> bindings,
        string parameterName)
        where TLeft : LogicalQueryNode
        where TRight : LogicalQueryNode
    {
        foreach (var binding in bindings)
        {
            RequireOwner(binding);
            var visibleOnLeft = structural.IsBindingVisible(left, binding.Structural);
            var visibleOnRight = structural.IsBindingVisible(right, binding.Structural);
            if (!visibleOnLeft && !visibleOnRight)
            {
                throw new ArgumentException(
                    $"Binding '{binding.Id.Value}' is not visible in logical nodes '{left.Id.Value}' or '{right.Id.Value}'.",
                    parameterName);
            }
        }
    }

    static bool ContainsRowReference(Expr expression) =>
        expression switch
        {
            FieldExpr or FieldRefExpr or CurrentItemExpr => true,
            UnaryExpr unary => ContainsRowReference(unary.Operand),
            BinaryExpr binary => ContainsRowReference(binary.Left) || ContainsRowReference(binary.Right),
            ConditionalExpr conditional =>
                ContainsRowReference(conditional.Test)
                || ContainsRowReference(conditional.IfTrue)
                || ContainsRowReference(conditional.IfFalse),
            CallExpr call => call.Arguments.Any(ContainsRowReference),
            AggregateExpr aggregate =>
                ContainsRowReference(aggregate.Source)
                || aggregate.GroupBy.Any(ContainsRowReference),
            _ => false
        };

    internal ImmutableArray<RelationQueryExpressionValueBinding> RequireBindings(
        LambdaExpression expression,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(bindings);
        if (expression.Parameters.Count != bindings.Count)
        {
            throw new ArgumentException(
                $"The lambda declares {expression.Parameters.Count} parameter(s), but {bindings.Count} binding(s) were supplied.",
                nameof(bindings));
        }

        var handles = ImmutableArray.CreateBuilder<RelationQueryExpressionValueBinding>(bindings.Count);
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index]
                ?? throw new ArgumentException("Expression bindings cannot contain null entries.", nameof(bindings));
            RequireOwner(binding);
            if (expression.Parameters[index].Type != binding.ClrType)
            {
                throw new ArgumentException(
                    $"Lambda parameter {index} has CLR type '{expression.Parameters[index].Type}', but binding " +
                    $"'{binding.Id.Value}' represents '{binding.ClrType}'.",
                    nameof(bindings));
            }

            handles.Add(binding);
        }

        return handles.MoveToImmutable();
    }

    internal static void RequireReturnType(
        LambdaExpression expression,
        Type expected,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(expected);
        if (expression.ReturnType != expected)
        {
            throw new ArgumentException(
                $"The lambda returns '{expression.ReturnType}', but this operation requires '{expected}'.",
                parameterName);
        }
    }
}
