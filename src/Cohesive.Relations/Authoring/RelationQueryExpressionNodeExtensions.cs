using System.Linq.Expressions;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>Fluent operations on session-owned typed query branches.</summary>
public static class RelationQueryExpressionNodeExtensions
{
    /// <summary>Filters this branch through its owning authoring session, retaining its focused binding.</summary>
    /// <typeparam name="TNode">Canonical input node type.</typeparam>
    /// <typeparam name="T">Focused row type inferred from the branch.</typeparam>
    /// <param name="input">Session-owned branch to filter.</param>
    /// <param name="predicate">Expression lowered into the canonical predicate; captured query parameters must belong to the same session.</param>
    /// <returns>A filtered branch that can be filtered again, traversed or projected.</returns>
    /// <exception cref="ArgumentNullException">Input or predicate is null.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The predicate cannot be lowered or references a foreign query parameter.</exception>
    /// <remarks>Delegates to the existing session authoring operation; it does not introduce another builder or execution path.</remarks>
    public static RelationQueryExpressionBoundNode<FilterQueryNode, T> Where<TNode, T>(
        this RelationQueryExpressionBoundNode<TNode, T> input, Expression<Func<T, bool>> predicate)
        where TNode : LogicalQueryNode where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Binding.Owner.Where(input, predicate);
    }
}
