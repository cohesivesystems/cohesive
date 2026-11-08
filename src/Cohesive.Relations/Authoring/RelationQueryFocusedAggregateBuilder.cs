using System.Linq.Expressions;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>The default result shape for a single grouping key and sum.</summary>
/// <typeparam name="TKey">Grouping key type.</typeparam>
/// <typeparam name="TNumber">Exact numeric sum type.</typeparam>
/// <param name="Key">The value shared by the rows in this group.</param>
/// <param name="Total">The canonical sum for this group.</param>
public sealed record RelationQueryGroupedSum<TKey, TNumber>(TKey Key, TNumber Total);

/// <summary>Declares grouping and sums over one focused source binding using the canonical aggregate builder.</summary>
/// <typeparam name="TSource">Focused input row type.</typeparam>
/// <typeparam name="TResult">Aggregate output type.</typeparam>
/// <remarks>Valid only inside the enclosing Aggregate callback. Expressions are lowered, never executed.</remarks>
public sealed class RelationQueryFocusedAggregateBuilder<TSource, TResult>
    where TSource : notnull where TResult : notnull
{
    readonly RelationQueryExpressionAggregateBuilder<TResult> aggregate;
    readonly RelationQueryExpressionValueBinding<TSource> binding;

    internal RelationQueryFocusedAggregateBuilder(RelationQueryExpressionAggregateBuilder<TResult> aggregate,
        RelationQueryExpressionValueBinding<TSource> binding)
    { this.aggregate = aggregate; this.binding = binding; }

    /// <summary>Assigns an output grouping field from the focused input.</summary>
    /// <typeparam name="TTarget">Output grouping-field type.</typeparam>
    /// <typeparam name="TKey">Input key type, checked against the target by the canonical builder.</typeparam>
    /// <param name="target">Output field receiving the key.</param>
    /// <param name="key">Grouping expression over the focused input.</param>
    /// <returns>This declaration builder.</returns>
    /// <exception cref="ArgumentNullException">A required expression is null.</exception>
    /// <exception cref="ArgumentException">The selector or grouping contract is invalid.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The expression cannot be lowered exactly.</exception>
    public RelationQueryFocusedAggregateBuilder<TSource, TResult> Group<TTarget, TKey>(
        Expression<Func<TResult, TTarget>> target, Expression<Func<TSource, TKey>> key)
    {
        aggregate.Group(target, key, binding);
        return this;
    }

    /// <summary>Assigns an exact numeric sum with the existing canonical null, missing and overflow rules.</summary>
    /// <typeparam name="TTarget">Output field type.</typeparam>
    /// <typeparam name="TValue">Input numeric type; exact compatibility is checked without adding implicit coercions.</typeparam>
    /// <param name="target">Output field receiving the sum.</param>
    /// <param name="value">Numeric expression over the focused input.</param>
    /// <returns>This declaration builder.</returns>
    /// <exception cref="ArgumentNullException">A required expression is null.</exception>
    /// <exception cref="ArgumentException">The field or numeric types do not satisfy the canonical sum contract.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The expression cannot be lowered exactly.</exception>
    public RelationQueryFocusedAggregateBuilder<TSource, TResult> Sum<TTarget, TValue>(
        Expression<Func<TResult, TTarget>> target, Expression<Func<TSource, TValue>> value)
    {
        aggregate.Value(target, AggregateOperator.Sum, value, binding);
        return this;
    }
}
