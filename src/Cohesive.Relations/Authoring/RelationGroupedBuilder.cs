using System.Linq.Expressions;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Typed grouped projection builder.
/// </summary>
public sealed class RelationGroupedBuilder<TSource, TKey, TTarget>
{
    readonly ShapeId from;
    readonly ShapeId to;
    readonly Expression<Func<TSource, TKey>> keySelector;

    internal RelationGroupedBuilder(ShapeId from, ShapeId to, Expression<Func<TSource, TKey>> keySelector)
    {
        this.from = from;
        this.to = to;
        this.keySelector = keySelector;
    }

    /// <summary>
    /// Defines grouped projection mapping.
    /// </summary>
    public RelationDefinition Select(Expression<Func<RelationGroup<TKey, TSource>, TTarget>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var keyTranslator = RelExprTranslator.ForSource(
            keySelector.Parameters[0],
            prefix: $"{ExprFieldRoots.CurrentItem}{FieldPath.Separator}");
        var keyExpression = keyTranslator.Translate(keySelector.Body);
        var forEach = Expr.Call(ExprFunctionNames.GroupByRows, Expr.Call(ExprFunctionNames.SourceRows), keyExpression);

        var projectionTranslator = RelExprTranslator.ForGroup(selector.Parameters[0]);
        var assignments = RelationDslCompiler.BuildAssignments<TTarget>(selector.Body, projectionTranslator);
        var groupKey = Expr.Field($"{ExprFieldRoots.CurrentItem}{FieldPath.Separator}Id");

        return RelationDslCompiler.BuildRelation(
            from: from,
            to: to,
            assignments: assignments,
            forEach: forEach,
            key: groupKey,
            entity: groupKey,
            scope: MappingScope.Set
            );
    }
}
