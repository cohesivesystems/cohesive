using System.Text.Json;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Hydration;

/// <summary>
/// Builds a field-selective hydration plan from a relation definition.
/// </summary>
public sealed class RelationHydrationPlanner
{
    readonly RelationPlanner planner;

    /// <summary>
    /// Creates a hydration planner.
    /// </summary>
    public RelationHydrationPlanner(RelationPlanner? planner = null)
    {
        this.planner = planner ?? new();
    }

    /// <summary>
    /// Computes root/related field requirements from relation IR.
    /// </summary>
    public RelationHydrationPlan Plan(RelationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var projectionPlan = planner.Plan(definition);
        var relatedBySchema = new Dictionary<string, RelatedAccumulator>(StringComparer.Ordinal);

        if (definition.Filter is not null)
            CollectFromExpression(definition.Filter, relatedBySchema);

        foreach (var mapping in definition.Mappings)
        {
            if (mapping.Predicate is not null)
                CollectFromExpression(mapping.Predicate, relatedBySchema);
            if (mapping.ForEach is not null)
                CollectFromExpression(mapping.ForEach, relatedBySchema);
            foreach (var assignment in mapping.Assignments)
                CollectFromExpression(assignment.Expr, relatedBySchema);
        }

        var related = relatedBySchema
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Value.Build())
            .ToArray();

        return new(
            RootSchema: definition.RootSourceShapeId,
            RootFields: [..projectionPlan.ReferencedFields.OrderBy(x => x, StringComparer.Ordinal)],
            Related: related
            );
    }

    static void CollectFromExpression(Expr relExpression, IDictionary<string, RelatedAccumulator> relatedBySchema)
    {
        switch (relExpression)
        {
            case CallExpr function:
                if (string.Equals(function.Function, "relatedField", StringComparison.Ordinal)
                    && TryReadRelatedFieldBinding(function, out var schema, out var fieldName, out var lookupExpression))
                {
                    if (!relatedBySchema.TryGetValue(schema.Value, out var accumulator))
                    {
                        accumulator = new(schema);
                        relatedBySchema[schema.Value] = accumulator;
                    }

                    accumulator.AddField(fieldName);
                    accumulator.AddLookupExpression(lookupExpression);
                }

                foreach (var argument in function.Arguments)
                    CollectFromExpression(argument, relatedBySchema);
                break;

            case UnaryExpr unary:
                CollectFromExpression(unary.Operand, relatedBySchema);
                break;

            case BinaryExpr binary:
                CollectFromExpression(binary.Left, relatedBySchema);
                CollectFromExpression(binary.Right, relatedBySchema);
                break;

            case ConditionalExpr conditional:
                CollectFromExpression(conditional.Test, relatedBySchema);
                CollectFromExpression(conditional.IfTrue, relatedBySchema);
                CollectFromExpression(conditional.IfFalse, relatedBySchema);
                break;
        }
    }

    static bool TryReadRelatedFieldBinding(
        CallExpr functionRel,
        out ShapeId schema,
        out string fieldName,
        out Expr lookupRelExpression)
    {
        schema = default;
        fieldName = string.Empty;
        lookupRelExpression = Expr.Null();

        if (functionRel.Arguments.Length != 3)
            return false;

        if (!TryReadConstantString(functionRel.Arguments[0], out var schemaText)
            || string.IsNullOrWhiteSpace(schemaText))
            return false;

        if (!TryReadConstantString(functionRel.Arguments[2], out var fieldNameText)
            || string.IsNullOrWhiteSpace(fieldNameText))
            return false;

        schema = new ShapeId(schemaText);
        fieldName = fieldNameText;
        lookupRelExpression = functionRel.Arguments[1];
        return true;
    }

    static bool TryReadConstantString(Expr relExpression, out string? value)
    {
        value = null;
        if (relExpression is not ConstantExpr constant)
            return false;

        if (constant.Value.Kind != ObservationValueKind.String)
            return false;

        value = constant.Value.GetString();
        return value is not null;
    }

    sealed class RelatedAccumulator(ShapeId schema)
    {
        readonly HashSet<string> fields = new(StringComparer.Ordinal);
        readonly Dictionary<string, Expr> lookupExpressions = new(StringComparer.Ordinal);

        ShapeId Schema { get; } = schema;

        public void AddField(string fieldName) => fields.Add(fieldName);

        public void AddLookupExpression(Expr relExpression)
        {
            var key = JsonSerializer.Serialize(relExpression);
            lookupExpressions.TryAdd(key, relExpression);
        }

        public RelatedHydrationPlan Build()
        {
            return new RelatedHydrationPlan(
                Schema: Schema,
                Fields: fields.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                LookupKeyExpressions: lookupExpressions.Values.ToArray());
        }
    }
}
