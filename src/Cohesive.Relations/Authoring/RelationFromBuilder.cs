using System.Linq.Expressions;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Intermediate typed relation builder.
/// </summary>
public sealed class RelationFromBuilder<TSource, TTarget>
{
    readonly ShapeId from;
    readonly ShapeId to;

    internal RelationFromBuilder(ShapeId from, ShapeId to)
    {
        this.from = from;
        this.to = to;
    }

    /// <summary>
    /// Defines an explicit select projection.
    /// </summary>
    /// <remarks>This method does <em>not</em> evaluate the given selector directly.
    /// It traverses the <see cref="Expression"/> tree to build an <see cref="Expr"/> tree,
    /// extracting field assignmens to form a relation.
    /// </remarks>
    public RelationDefinition Select(Expression<Func<TSource, TTarget>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var translator = RelExprTranslator.ForSource(sourceParameter: selector.Parameters[0], prefix: string.Empty);
        var assignments = RelationDslCompiler.BuildAssignments<TTarget>(projectionBody: selector.Body, translator);
        return RelationDslCompiler.BuildRelation(
            from: from,
            to: to,
            assignments: assignments
            );
    }

    /// <summary>
    /// Starts convention field mapping.
    /// </summary>
    public RelationMapFieldsBuilder<TSource, TTarget> MapFields()
        => new(from, to);

    /// <summary>
    /// Starts a typed join projection.
    /// </summary>
    public RelationJoinBuilder<TSource, TRight, TTarget> Join<TRight>(
        Expression<Func<TSource, TRight, bool>> predicate,
        RelationJoinKind kind = RelationJoinKind.Inner
    )
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new(from, to, new(typeof(TRight).Name), predicate, kind);
    }

    /// <summary>
    /// Starts a grouped projection.
    /// </summary>
    public RelationGroupedBuilder<TSource, TKey, TTarget> GroupBy<TKey>(Expression<Func<TSource, TKey>> keySelector)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        return new(from, to, keySelector);
    }
}