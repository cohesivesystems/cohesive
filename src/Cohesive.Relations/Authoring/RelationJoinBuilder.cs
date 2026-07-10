using System.Linq.Expressions;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Join semantics for typed relation authoring.
/// </summary>
public enum RelationJoinKind
{
    /// <summary>Represents the inner option.</summary>
    Inner = 0,
    /// <summary>Represents the left option.</summary>
    Left = 1
}

sealed record JoinDescriptor(
    SourceAlias LeftAlias,
    SourceAlias RightAlias,
    ShapeId RightSchema,
    Expr LeftKeyExpression,
    string RightKeyField,
    RelationJoinKind Kind
)
{
    public JoinDefinition ToDefinition()
    {
        return new JoinDefinition(
            left: LeftAlias,
            right: RightAlias,
            kind: Kind switch
            {
                RelationJoinKind.Inner => JoinKind.Inner,
                RelationJoinKind.Left => JoinKind.Left,
                _ => throw new InvalidOperationException($"Unsupported join kind '{Kind}'.")
            },
            on: Expr.Eq(
                LeftKeyExpression,
                Expr.Field($"{RightAlias.Value}.{RightKeyField}")));
    }

    public RelationSource ToSource() => new(
        alias: RightAlias,
        shapeId: RightSchema,
        cardinality: SourceCardinality.Many);
}

/// <summary>
/// Typed join projection builder.
/// </summary>
public sealed class RelationJoinBuilder<TSource, TRight, TTarget>
{
    readonly ShapeId from;
    readonly ShapeId to;
    readonly JoinDescriptor join;

    internal RelationJoinBuilder(
        ShapeId from,
        ShapeId to,
        ShapeId rightSchema,
        Expression<Func<TSource, TRight, bool>> predicate,
        RelationJoinKind kind
        )
    {
        this.from = from;
        this.to = to;
        var parsed = RelationDslCompiler.ParseJoin(predicate);
        join = new JoinDescriptor(
            LeftAlias: new SourceAlias("src"),
            RightAlias: new SourceAlias("j1"),
            RightSchema: rightSchema,
            LeftKeyExpression: parsed.LeftKey,
            RightKeyField: parsed.RightKey,
            Kind: kind);
    }

    /// <summary>
    /// Adds a second joined shape.
    /// </summary>
    public RelationJoinBuilder<TSource, TRight, TRight2, TTarget> Join<TRight2>(
        Expression<Func<TSource, TRight, TRight2, bool>> predicate,
        RelationJoinKind kind = RelationJoinKind.Inner)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new(from, to, join, new(typeof(TRight2).Name), predicate, kind);
    }

    /// <summary>
    /// Defines a typed join projection.
    /// </summary>
    public RelationDefinition Select(Expression<Func<TSource, TRight, TTarget>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var translator = RelExprTranslator.ForSourceWithJoins(
            sourceParameter: selector.Parameters[0],
            joins:
            [
                (selector.Parameters[1], join.RightSchema, join.LeftKeyExpression)
            ]);
        var assignments = RelationDslCompiler.BuildAssignments<TTarget>(selector.Body, translator);

        return RelationDslCompiler.BuildRelation(
            from: from,
            to: to,
            assignments: assignments,
            when: RelationDslCompiler.BuildJoinWhen([join]),
            joins: [join.ToDefinition()],
            sources:
            [
                new RelationSource(new SourceAlias("src"), from, SourceCardinality.Many),
                join.ToSource()
            ]);
    }
}

/// <summary>
/// Typed two-join projection builder.
/// </summary>
public sealed class RelationJoinBuilder<TSource, TRight1, TRight2, TTarget>
{
    readonly ShapeId from;
    readonly ShapeId to;
    readonly JoinDescriptor firstJoin;
    readonly JoinDescriptor secondJoin;

    internal RelationJoinBuilder(
        ShapeId from,
        ShapeId to,
        JoinDescriptor firstJoin,
        ShapeId rightSchema,
        Expression<Func<TSource, TRight1, TRight2, bool>> predicate,
        RelationJoinKind kind
        )
    {
        this.from = from;
        this.to = to;
        this.firstJoin = firstJoin;

        var translator = RelExprTranslator.ForSourceWithJoins(
            sourceParameter: predicate.Parameters[0],
            joins:
            [
                (predicate.Parameters[1], firstJoin.RightSchema, firstJoin.LeftKeyExpression)
            ]);
        var parsed = RelationDslCompiler.ParseJoin(
            predicate: predicate,
            rightParameter: predicate.Parameters[2],
            leftTranslator: translator);
        secondJoin = new JoinDescriptor(
            LeftAlias: new SourceAlias("src"),
            RightAlias: new SourceAlias("j2"),
            RightSchema: rightSchema,
            LeftKeyExpression: parsed.LeftKey,
            RightKeyField: parsed.RightKey,
            Kind: kind);
    }

    /// <summary>
    /// Defines a typed two-join projection.
    /// </summary>
    public RelationDefinition Select(Expression<Func<TSource, TRight1, TRight2, TTarget>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var translator = RelExprTranslator.ForSourceWithJoins(
            sourceParameter: selector.Parameters[0],
            joins:
            [
                (selector.Parameters[1], firstJoin.RightSchema, firstJoin.LeftKeyExpression),
                (selector.Parameters[2], secondJoin.RightSchema, secondJoin.LeftKeyExpression)
            ]);
        var assignments = RelationDslCompiler.BuildAssignments<TTarget>(selector.Body, translator);

        return RelationDslCompiler.BuildRelation(
            from: from,
            to: to,
            assignments: assignments,
            when: RelationDslCompiler.BuildJoinWhen([firstJoin, secondJoin]),
            joins: [firstJoin.ToDefinition(), secondJoin.ToDefinition()],
            sources:
            [
                new RelationSource(new SourceAlias("src"), from, SourceCardinality.Many),
                firstJoin.ToSource(),
                secondJoin.ToSource()
            ]);
    }
}
