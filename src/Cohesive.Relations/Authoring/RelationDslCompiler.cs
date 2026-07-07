using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;
using BinaryExpression = System.Linq.Expressions.BinaryExpression;

namespace Cohesive.Relations.Authoring;

static class RelationDslCompiler
{
    public static RelationDefinition BuildRelation(
        ShapeId from,
        ShapeId to,
        IReadOnlyList<FieldAssignment> assignments,
        Expr? when = null,
        Expr? forEach = null,
        Expr? key = null,
        Expr? entity = null,
        MappingScope scope = MappingScope.Rooted,
        IReadOnlyList<JoinDefinition>? joins = null,
        IReadOnlyList<RelationSource>? sources = null
        )
    {
        if (assignments.Count == 0)
            throw new InvalidOperationException("Relation projection must assign at least one target field.");

        var mapping = new MappingDefinition(
            id: new MappingId("map_1"),
            name: new MappingName("DefaultRelationMapping"),
            targetShapeId: to,
            assignments: [.. assignments],
            predicate: when,
            forEach: forEach,
            key: key,
            entity: entity,
            scope: scope
            );

        var relationId = new RelationId($"{from.Value}->{to.Value}");
        var relationName = new RelationName($"{from.Value}To{to.Value}");

        return new RelationDefinition(
            id: relationId,
            name: relationName,
            sources: sources is null
                ? [new RelationSource(alias: new SourceAlias("src"), shapeId: from, cardinality: SourceCardinality.Many)]
                : [.. sources],
            joins: joins is null ? [] : [.. joins],
            filter: null,
            baseRowShapeId: null,
            mappings: [mapping],
            materialization: null,
            metadata: RelationMetadata.Default,
            invariants: []
            );
    }

    public static IReadOnlyList<FieldAssignment> BuildAssignments<TTarget>(
        Expression projectionBody,
        RelExprTranslator translator
        )
    {
        return ReadProjectionBindings(typeof(TTarget), projectionBody)
            .Select(binding =>
            {
                var targetField = ResolveFieldIdentity(binding.TargetProperty);
                return new FieldAssignment(
                    targetField: targetField,
                    expr: translator.Translate(binding.Expression),
                    id: $"assign_{targetField}");
            })
            .ToArray();
    }

    public static Expr? BuildJoinWhen(IReadOnlyList<JoinDescriptor> joins)
    {
        var predicates = joins
            .Where(x => x.Kind == RelationJoinKind.Inner)
            .Select(join => Expr.Not(
                Expr.Eq(
                        Expr.RelatedField(
                        Expr.Const(join.RightSchema.Value),
                        join.LeftKeyExpression,
                        Expr.Const(join.RightKeyField)),
                    Expr.Null())))
            .ToArray();

        if (predicates.Length == 0)
            return null;

        var combined = predicates[0];
        for (var i = 1; i < predicates.Length; i++)
            combined = Expr.And(combined, predicates[i]);
        return combined;
    }

    public static (Expr LeftKey, string RightKey) ParseJoin<TLeft, TRight>(Expression<Func<TLeft, TRight, bool>> predicate)
    {
        var leftTranslator = RelExprTranslator.ForSource(predicate.Parameters[0], prefix: string.Empty);
        return ParseJoin(predicate, predicate.Parameters[1], leftTranslator);
    }

    public static (Expr LeftKey, string RightKey) ParseJoin(
        LambdaExpression predicate,
        ParameterExpression rightParameter,
        RelExprTranslator leftTranslator)
    {
        var body = RelExprTranslator.StripConvert(predicate.Body);
        if (body is not BinaryExpression { NodeType: ExpressionType.Equal } equal)
            throw new RelationDslException("Join predicate must be a binary equality expression like '(l, r) => l.Id == r.ForeignId'.");

        if (TryReadJoinSides(equal.Left, equal.Right, rightParameter, out var leftExpression, out var rightProperty)
            || TryReadJoinSides(equal.Right, equal.Left, rightParameter, out leftExpression, out rightProperty))
        {
            var leftKey = leftTranslator.Translate(leftExpression);
            return (leftKey, ResolveFieldIdentity(rightProperty));
        }

        throw new RelationDslException("Join predicate must compare a left expression to a right property access.");
    }

    static bool TryReadJoinSides(
        Expression leftCandidate,
        Expression rightCandidate,
        ParameterExpression rightParameter,
        out Expression leftExpression,
        out PropertyInfo rightProperty)
    {
        leftExpression = null!;
        rightProperty = null!;
        if (!TryReadRightProperty(rightCandidate, rightParameter, out rightProperty))
            return false;

        leftExpression = leftCandidate;
        return true;
    }

    static bool TryReadRightProperty(Expression expression, ParameterExpression rightParameter, out PropertyInfo property)
    {
        property = null!;
        var stripped = RelExprTranslator.StripConvert(expression);
        if (stripped is not MemberExpression member || member.Member is not PropertyInfo prop)
            return false;

        if (RelExprTranslator.StripConvert(member.Expression!) is not ParameterExpression parameter
            || !ReferenceEquals(parameter, rightParameter))
        {
            return false;
        }

        property = prop;
        return true;
    }

    static IReadOnlyList<ProjectionBinding> ReadProjectionBindings(Type targetType, Expression body)
    {
        body = RelExprTranslator.StripConvert(body);
        switch (body)
        {
            case MemberInitExpression memberInit:
                return memberInit.Bindings
                    .OfType<MemberAssignment>()
                    .Select(binding =>
                    {
                        if (binding.Member is not PropertyInfo property)
                            throw new RelationDslException($"Unsupported projection member '{binding.Member.Name}'.");
                        return new ProjectionBinding(property, binding.Expression);
                    })
                    .ToArray();
            case NewExpression newExpr when newExpr.Members is not null && newExpr.Members.Count == newExpr.Arguments.Count:
                return newExpr.Arguments
                    .Select((argument, index) =>
                    {
                        var member = newExpr.Members[index];
                        var property = targetType.GetProperty(member.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                       ?? throw new RelationDslException($"Projection member '{member.Name}' was not found on target type '{targetType.Name}'.");
                        return new ProjectionBinding(property, argument);
                    })
                    .ToArray();
            case NewExpression newExpr:
            {
                var parameters = newExpr.Constructor?.GetParameters()
                                 ?? throw new RelationDslException("Projection constructor arguments are not supported without parameter metadata.");
                if (parameters.Length != newExpr.Arguments.Count)
                    throw new RelationDslException("Projection constructor arguments do not match constructor parameter count.");

                return newExpr.Arguments
                    .Select((argument, index) =>
                    {
                        var parameterName = parameters[index].Name
                                            ?? throw new RelationDslException("Projection constructor parameter names are required.");
                        var property = targetType.GetProperty(parameterName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                       ?? throw new RelationDslException($"Projection constructor parameter '{parameterName}' does not map to a target property on '{targetType.Name}'.");
                        return new ProjectionBinding(property, argument);
                    })
                    .ToArray();
            }
            default:
                throw new RelationDslException("Projection body must be a member-initializer or object-constructor expression.");
        }
    }

    public static PropertyInfo ResolveProperty<T, TValue>(Expression<Func<T, TValue>> selector)
    {
        var body = RelExprTranslator.StripConvert(selector.Body);
        if (body is not MemberExpression member || member.Member is not PropertyInfo property)
            throw new RelationDslException("Selector must be a direct property access.");
        
        if (RelExprTranslator.StripConvert(member.Expression!) is not ParameterExpression parameter
            || !ReferenceEquals(parameter, selector.Parameters[0]))
        {
            throw new RelationDslException("Selector must be a direct property access on the lambda parameter.");
        }

        return property;
    }

    public static string ResolveFieldPath(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
        return attribute?.Name ?? property.Name;
    }

    public static string ResolveFieldIdentity(PropertyInfo property) => ResolveFieldPath(property);

    public static PropertyInfo[] GetReadableProperties(Type type)
    {
        return ShapeTypeInspector.GetReadableProperties(type);
    }

    sealed record ProjectionBinding(PropertyInfo TargetProperty, Expression Expression);
}
