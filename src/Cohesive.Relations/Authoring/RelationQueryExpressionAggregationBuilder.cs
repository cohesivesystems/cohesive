using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Collects metadata-backed grouping and aggregate assignments for one canonical aggregate node.
/// </summary>
/// <typeparam name="TResult">CLR type of the aggregate output shape.</typeparam>
/// <remarks>
/// The builder is mutable, belongs to one <see cref="RelationQueryExpressionAuthoring"/> session,
/// and is only valid during its enclosing <c>Aggregate</c> callback. It never executes supplied lambdas.
/// </remarks>
public sealed class RelationQueryExpressionAggregateBuilder<TResult>
    where TResult : notnull
{
    readonly RelationQueryExpressionAuthoring owner;
    readonly string sourceReference;
    readonly Func<RelationQueryExpressionValueBinding, bool> isBindingVisible;
    readonly List<RelationQueryGroupingAssignment> groupings = [];
    readonly List<RelationQueryAggregateAssignment> aggregates = [];

    internal RelationQueryExpressionAggregateBuilder(
        RelationQueryExpressionAuthoring owner,
        string sourceReference,
        Func<RelationQueryExpressionValueBinding, bool> isBindingVisible)
    {
        this.owner = owner;
        this.sourceReference = sourceReference;
        this.isBindingVisible = isBindingVisible;
    }

    internal ImmutableArray<RelationQueryGroupingAssignment> Groupings => [.. groupings];

    internal ImmutableArray<RelationQueryAggregateAssignment> Aggregates => [.. aggregates];

    /// <summary>Adds a grouping assignment using an arbitrary-width typed key lambda.</summary>
    /// <typeparam name="TTarget">CLR type of the target result field.</typeparam>
    /// <param name="target">Direct or nested result property receiving the grouping key.</param>
    /// <param name="key">Grouping-key lambda whose parameters correspond to <paramref name="bindings"/>.</param>
    /// <param name="bindings">Visible bindings corresponding positionally to the key lambda parameters.</param>
    /// <param name="assignmentSourceReference">Optional stable producer reference for this assignment.</param>
    /// <returns>This builder for continued aggregate authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/>, <paramref name="key"/>, or <paramref name="bindings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The target is not a property path, or a key binding belongs to another session, has a mismatched
    /// CLR type, or is not visible in the aggregate input.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The key cannot be lowered exactly or returns a raw CLR temporal carrier instead of an explicitly normalized
    /// canonical scalar grouping key.
    /// </exception>
    public RelationQueryExpressionAggregateBuilder<TResult> Group<TTarget>(
        Expression<Func<TResult, TTarget>> target,
        LambdaExpression key,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string? assignmentSourceReference = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(key);
        if (key.ReturnType != typeof(TTarget))
        {
            throw new ArgumentException(
                $"Grouping key type '{key.ReturnType}' does not match target field type '{typeof(TTarget)}'.",
                nameof(key));
        }
        var reference = assignmentSourceReference ?? $"{sourceReference}/groupings/{groupings.Count}";
        RelationQueryExpressionAuthoring.RequireCarrierIndependentKey(key, "grouping", reference + "/key");
        var handles = owner.RequireBindings(key, bindings);
        RequireBindingsVisible(handles, nameof(bindings));
        var lowered = owner.ExpressionLowerer
            .LowerValue(key, handles, reference + "/key")
            .RequireValue();
        groupings.Add(new(
            owner.ResolveSelectorPath(target, nameof(target)),
            lowered.Value,
            assignmentSource: RelationQueryExpressionAuthoring.Source(reference, "Expression-authored grouping."),
            keySource: lowered.Source));
        return this;
    }

    /// <summary>Adds a grouping assignment using one typed binding.</summary>
    /// <typeparam name="TTarget">CLR type of the target result field.</typeparam>
    /// <typeparam name="TBinding">CLR type of the source binding.</typeparam>
    /// <typeparam name="TKey">CLR type of the grouping key.</typeparam>
    /// <param name="target">Direct or nested result property receiving the grouping key.</param>
    /// <param name="key">Grouping-key expression.</param>
    /// <param name="binding">Binding corresponding to the key lambda parameter.</param>
    /// <param name="assignmentSourceReference">Optional stable producer reference for this assignment.</param>
    /// <returns>This builder for continued aggregate authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/> or <paramref name="key"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The target is not a property path, or the binding belongs to another session, has a mismatched CLR
    /// type, or is not visible in the aggregate input.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The key cannot be lowered exactly or returns a raw CLR temporal carrier instead of an explicitly normalized
    /// canonical scalar grouping key.
    /// </exception>
    public RelationQueryExpressionAggregateBuilder<TResult> Group<TTarget, TBinding, TKey>(
        Expression<Func<TResult, TTarget>> target,
        Expression<Func<TBinding, TKey>> key,
        RelationQueryExpressionValueBinding<TBinding> binding,
        string? assignmentSourceReference = null)
        where TBinding : notnull =>
        Group(target, key, [binding], assignmentSourceReference);

    /// <summary>Adds an unfiltered count assignment.</summary>
    /// <typeparam name="TTarget">CLR type of the target result field.</typeparam>
    /// <param name="target">Direct or nested result property receiving the count.</param>
    /// <param name="assignmentSourceReference">Optional stable producer reference for this assignment.</param>
    /// <returns>This builder for continued aggregate authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> is not a direct or nested result property.</exception>
    public RelationQueryExpressionAggregateBuilder<TResult> Count<TTarget>(
        Expression<Func<TResult, TTarget>> target,
        string? assignmentSourceReference = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireCountTarget<TTarget>(nameof(target));
        var reference = assignmentSourceReference ?? $"{sourceReference}/aggregates/{aggregates.Count}";
        aggregates.Add(new(
            owner.ResolveSelectorPath(target, nameof(target)),
            AggregateOperator.Count,
            assignmentSource: RelationQueryExpressionAuthoring.Source(reference, "Expression-authored count.")));
        return this;
    }

    /// <summary>Adds a filtered count assignment using an arbitrary-width predicate.</summary>
    /// <typeparam name="TTarget">CLR type of the target result field.</typeparam>
    /// <param name="target">Direct or nested result property receiving the count.</param>
    /// <param name="filter">Predicate whose parameters correspond to <paramref name="bindings"/>.</param>
    /// <param name="bindings">Visible bindings corresponding positionally to the predicate parameters.</param>
    /// <param name="assignmentSourceReference">Optional stable producer reference for this assignment.</param>
    /// <returns>This builder for continued aggregate authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/>, <paramref name="filter"/>, or <paramref name="bindings"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The target is not a property path, or a filter binding belongs to another session, has a mismatched
    /// CLR type, or is not visible in the aggregate input.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The filter cannot be lowered exactly.</exception>
    public RelationQueryExpressionAggregateBuilder<TResult> Count<TTarget>(
        Expression<Func<TResult, TTarget>> target,
        LambdaExpression filter,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string? assignmentSourceReference = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireCountTarget<TTarget>(nameof(target));
        var reference = assignmentSourceReference ?? $"{sourceReference}/aggregates/{aggregates.Count}";
        RelationQueryExpressionAuthoring.RequireReturnType(filter, typeof(bool), nameof(filter));
        var handles = owner.RequireBindings(filter, bindings);
        RequireBindingsVisible(handles, nameof(bindings));
        var loweredFilter = owner.ExpressionLowerer
            .LowerValue(filter, handles, reference + "/filter")
            .RequireValue();
        aggregates.Add(new(
            owner.ResolveSelectorPath(target, nameof(target)),
            AggregateOperator.Count,
            filter: loweredFilter.Value,
            assignmentSource: RelationQueryExpressionAuthoring.Source(reference, "Expression-authored filtered count."),
            filterSource: loweredFilter.Source));
        return this;
    }

    /// <summary>Adds a value aggregate with optional independently scoped filter bindings.</summary>
    /// <typeparam name="TTarget">CLR type of the target result field.</typeparam>
    /// <param name="target">Direct or nested result property receiving the aggregate value.</param>
    /// <param name="operation">Canonical aggregate operation other than count.</param>
    /// <param name="value">Value lambda whose parameters correspond to <paramref name="valueBindings"/>.</param>
    /// <param name="valueBindings">Visible bindings corresponding positionally to the value lambda parameters.</param>
    /// <param name="filter">Optional aggregate-local predicate.</param>
    /// <param name="filterBindings">
    /// Bindings corresponding to <paramref name="filter"/>; required when a filter is supplied.
    /// </param>
    /// <param name="assignmentSourceReference">Optional stable producer reference for this assignment.</param>
    /// <returns>This builder for continued aggregate authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/>, <paramref name="value"/>, or <paramref name="valueBindings"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> is count, filter bindings are inconsistent with the filter, the target is not
    /// a property path, or an expression binding belongs to another session, has a mismatched CLR type, or
    /// is not visible in the aggregate input; or value and target types do not satisfy the exact aggregate contract.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is unsupported.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The value or filter cannot be lowered exactly.</exception>
    public RelationQueryExpressionAggregateBuilder<TResult> Value<TTarget>(
        Expression<Func<TResult, TTarget>> target,
        AggregateOperator operation,
        LambdaExpression value,
        IReadOnlyList<RelationQueryExpressionValueBinding> valueBindings,
        LambdaExpression? filter = null,
        IReadOnlyList<RelationQueryExpressionValueBinding>? filterBindings = null,
        string? assignmentSourceReference = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported aggregate operation.");
        }

        if (operation == AggregateOperator.Count)
        {
            throw new ArgumentException("Use Count to author a count assignment.", nameof(operation));
        }

        ArgumentNullException.ThrowIfNull(value);
        ValidateValueAggregateTypes<TTarget>(operation, value.ReturnType, nameof(value));
        if ((filter is null) != (filterBindings is null))
        {
            throw new ArgumentException("A filter and its binding list must be supplied together.", nameof(filterBindings));
        }

        if (filter is not null)
        {
            RelationQueryExpressionAuthoring.RequireReturnType(filter, typeof(bool), nameof(filter));
        }

        var reference = assignmentSourceReference ?? $"{sourceReference}/aggregates/{aggregates.Count}";
        var valueHandles = owner.RequireBindings(value, valueBindings);
        RequireBindingsVisible(valueHandles, nameof(valueBindings));
        var lowerer = owner.ExpressionLowerer;
        var loweredValue = lowerer.LowerValue(value, valueHandles, reference + "/value").RequireValue();
        RelationQueryExpressionLowering? loweredFilter = null;
        if (filter is not null)
        {
            var handles = owner.RequireBindings(filter, filterBindings!);
            RequireBindingsVisible(handles, nameof(filterBindings));
            loweredFilter = lowerer.LowerValue(filter, handles, reference + "/filter").RequireValue();
        }

        aggregates.Add(new(
            owner.ResolveSelectorPath(target, nameof(target)),
            operation,
            loweredValue.Value,
            loweredFilter?.Value,
            assignmentSource: RelationQueryExpressionAuthoring.Source(reference, $"Expression-authored {operation} aggregate."),
            valueSource: loweredValue.Source,
            filterSource: loweredFilter?.Source));
        return this;
    }

    static void RequireCountTarget<TTarget>(string parameterName)
    {
        if (typeof(TTarget) != typeof(long))
        {
            throw new ArgumentException(
                $"A canonical count result requires a CLR Int64 target, not '{typeof(TTarget)}'.",
                parameterName);
        }
    }

    static void ValidateValueAggregateTypes<TTarget>(
        AggregateOperator operation,
        Type valueType,
        string parameterName)
    {
        var targetType = typeof(TTarget);
        var valid = operation switch
        {
            AggregateOperator.Sum =>
                valueType == targetType && IsExactNumeric(valueType),
            AggregateOperator.Min or AggregateOperator.Max =>
                valueType == targetType && IsCanonicalComparable(valueType),
            AggregateOperator.Any or AggregateOperator.All =>
                valueType == typeof(bool) && targetType == typeof(bool),
            _ => false
        };
        if (valid)
        {
            return;
        }

        throw new ArgumentException(
            $"Aggregate operation '{operation}' over CLR value type '{valueType}' cannot populate "
            + $"target field type '{targetType}' with the canonical aggregate contract. "
            + GetAggregateTypeSuggestion(operation),
            parameterName);
    }

    static string GetAggregateTypeSuggestion(AggregateOperator operation) => operation switch
    {
        AggregateOperator.Sum =>
            "Sum requires the value and target to use the same supported exact numeric CLR type "
            + "(Byte, Int16, Int32, Int64, or Decimal).",
        AggregateOperator.Min or AggregateOperator.Max =>
            "Min and Max require the value and target to use the same supported exact numeric or String CLR type.",
        AggregateOperator.Any or AggregateOperator.All =>
            "Any and All require Boolean value and target types.",
        _ => "Use an aggregate operation with a supported canonical value and result contract."
    };

    static bool IsExactNumeric(Type type) =>
        type == typeof(byte)
        || type == typeof(short)
        || type == typeof(int)
        || type == typeof(long)
        || type == typeof(decimal);

    static bool IsCanonicalComparable(Type type) =>
        IsExactNumeric(type)
        || type == typeof(string);

    void RequireBindingsVisible(
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string parameterName)
    {
        foreach (var binding in bindings)
        {
            if (isBindingVisible(binding))
            {
                continue;
            }

            throw new ArgumentException(
                $"Binding '{binding.Id.Value}' is not visible in the aggregate input.",
                parameterName);
        }
    }

    /// <summary>Adds a value aggregate using one typed binding and optional filter.</summary>
    /// <typeparam name="TTarget">CLR type of the target result field.</typeparam>
    /// <typeparam name="TBinding">CLR type of the source binding.</typeparam>
    /// <typeparam name="TValue">CLR type of the aggregate input value.</typeparam>
    /// <param name="target">Direct or nested result property receiving the aggregate value.</param>
    /// <param name="operation">Canonical aggregate operation other than count.</param>
    /// <param name="value">Aggregate value expression.</param>
    /// <param name="binding">Binding corresponding to the value and optional filter parameter.</param>
    /// <param name="filter">Optional aggregate-local predicate over the same binding.</param>
    /// <param name="assignmentSourceReference">Optional stable producer reference for this assignment.</param>
    /// <returns>This builder for continued aggregate authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/> or <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> is count, the target is not a property path, or the binding belongs to another
    /// session, has a mismatched CLR type, or is not visible in the aggregate input; or value and target
    /// types do not satisfy the exact aggregate contract.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is unsupported.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The value or filter cannot be lowered exactly.</exception>
    public RelationQueryExpressionAggregateBuilder<TResult> Value<TTarget, TBinding, TValue>(
        Expression<Func<TResult, TTarget>> target,
        AggregateOperator operation,
        Expression<Func<TBinding, TValue>> value,
        RelationQueryExpressionValueBinding<TBinding> binding,
        Expression<Func<TBinding, bool>>? filter = null,
        string? assignmentSourceReference = null)
        where TBinding : notnull =>
        Value(
            target,
            operation,
            value,
            [binding],
            filter,
            filter is null ? null : [binding],
            assignmentSourceReference);
}

public sealed partial class RelationQueryExpressionAuthoring
{
    /// <summary>Authors one grouped or global aggregation through a typed assignment callback.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="TResult">CLR aggregate-result type.</typeparam>
    /// <param name="input">Logical branch to aggregate.</param>
    /// <param name="configure">Callback that declares grouping and aggregate assignments.</param>
    /// <param name="sourceReference">Optional stable producer reference for the aggregate node.</param>
    /// <returns>Typed aggregate-node and result-binding handles.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">No aggregate assignment was declared.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// A grouping, value, or filter expression cannot be lowered exactly.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TResult"/> cannot be represented by the configured CLR shape context.
    /// </exception>
    public RelationQueryExpressionBoundNode<AggregateQueryNode, TResult> Aggregate<TInput, TResult>(
        RelationQueryNodeHandle<TInput> input,
        Action<RelationQueryExpressionAggregateBuilder<TResult>> configure,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TResult : notnull =>
        Aggregate(input, clr.Shape<TResult>(), configure, sourceReference);

    /// <summary>Authors one aggregation using an explicit typed CLR result-shape registration.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="TResult">CLR aggregate-result type.</typeparam>
    /// <param name="input">Logical branch to aggregate.</param>
    /// <param name="resultShape">Typed deterministic or imported aggregate-result shape.</param>
    /// <param name="configure">Callback that declares grouping and aggregate assignments.</param>
    /// <param name="sourceReference">Optional stable producer reference for the aggregate node.</param>
    /// <returns>Typed aggregate-node and result-binding handles.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resultShape"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">No aggregate assignment was declared.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// A grouping, value, or filter expression cannot be lowered exactly.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TResult"/> is already bound to another semantic shape in this session.
    /// </exception>
    public RelationQueryExpressionBoundNode<AggregateQueryNode, TResult> Aggregate<TInput, TResult>(
        RelationQueryNodeHandle<TInput> input,
        RelationQueryClrShape<TResult> resultShape,
        Action<RelationQueryExpressionAggregateBuilder<TResult>> configure,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(resultShape);
        ArgumentNullException.ThrowIfNull(configure);
        var reference = sourceReference ?? $"aggregate/{StableTypeName(typeof(TResult))}";
        TrackShape(resultShape);
        var builder = new RelationQueryExpressionAggregateBuilder<TResult>(
            this,
            reference,
            binding => structural.IsBindingVisible(input, binding.Structural));
        configure(builder);
        if (builder.Aggregates.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An aggregate node requires at least one aggregate assignment.", nameof(configure));
        }

        var aggregate = structural.Aggregate(
            input,
            resultShape.Id,
            builder.Groupings,
            builder.Aggregates,
            source: Source(reference, $"Expression-authored aggregation '{StableTypeName(typeof(TResult))}'."),
            bindingSource: Source(reference + "/binding", "Aggregate result binding."));
        return new(
            aggregate.Node,
            new RelationQueryExpressionValueBinding<TResult>(
                this,
                aggregate.Binding,
                resultShape.Type,
                resultShape.Id,
                resultShape.ResolveMemberPath,
                resultShape.ResolveType,
                resultShape.IdentityOrigin == RelationQueryClrIdentityOrigin.Imported));
    }
}
