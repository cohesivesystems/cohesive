using Cohesive.Relations.Model;

namespace Cohesive.Relations.Execution;

/// <summary>
/// Planner output used by runner and explainability.
/// </summary>
public sealed record RelationPlan(
    ShapeId RootShapeId,
    IReadOnlyList<RelationPlanMapping> Mappings,
    IReadOnlyList<string> ReferencedFields
    );

/// <summary>
/// Planned projection details.
/// </summary>
public sealed record RelationPlanMapping(
    string MappingId,
    ShapeId TargetShapeId,
    IReadOnlyList<string> ReferencedFields,
    IReadOnlyList<string> AssignedFields,
    bool HasForEach
    );

/// <summary>
/// Plans relation definitions into deterministic execution metadata.
/// </summary>
public sealed class RelationPlanner
{
    /// <summary>
    /// Builds a static execution plan from IR.
    /// </summary>
    public RelationPlan Plan(RelationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var relationMappings = definition.Mappings.Where(x => x.IsRelationMapping).ToArray();
        var planMappings = relationMappings.Select(mapping =>
        {
            var referenced = CollectReferencedFields(definition.Filter, mapping).ToArray();
            var assigned = mapping.Assignments
                .Select(x => x.TargetField)
                .Distinct()
                .ToArray();
            return new RelationPlanMapping(
                MappingId: mapping.Id.Value,
                TargetShapeId: mapping.TargetShapeId,
                ReferencedFields: referenced,
                AssignedFields: assigned,
                HasForEach: mapping.ForEach is not null);
        }).ToArray();

        var allReferenced = planMappings
            .SelectMany(x => x.ReferencedFields)
            .Distinct()
            .ToArray();
        return new RelationPlan(definition.RootSourceShapeId, planMappings, allReferenced);
    }

    static IEnumerable<string> CollectReferencedFields(Expr? relationFilter, MappingDefinition mapping)
    {
        if (relationFilter is not null)
        {
            foreach (var field in CollectFields(relationFilter))
                yield return field;
        }

        if (mapping.Predicate is not null)
        {
            foreach (var field in CollectFields(mapping.Predicate))
                yield return field;
        }

        if (mapping.ForEach is not null)
        {
            foreach (var field in CollectFields(mapping.ForEach))
                yield return field;
        }

        foreach (var assignment in mapping.Assignments)
        {
            foreach (var field in CollectFields(assignment.Expr))
                yield return field;
        }
    }

    static IEnumerable<string> CollectFields(Expr relExpression)
    {
        switch (relExpression)
        {
            case FieldExpr field:
                if (field.Path.TryGetTerminalFieldIdentity(out var fieldIdentity))
                    yield return fieldIdentity;
                yield break;

            case UnaryExpr unary:
                foreach (var field in CollectFields(unary.Operand))
                    yield return field;
                yield break;

            case BinaryExpr binary:
                foreach (var field in CollectFields(binary.Left))
                    yield return field;
                foreach (var field in CollectFields(binary.Right))
                    yield return field;
                yield break;

            case ConditionalExpr conditional:
                foreach (var field in CollectFields(conditional.Test))
                    yield return field;
                foreach (var field in CollectFields(conditional.IfTrue))
                    yield return field;
                foreach (var field in CollectFields(conditional.IfFalse))
                    yield return field;
                yield break;

            case CallExpr function:
                foreach (var arg in function.Arguments)
                {
                    foreach (var field in CollectFields(arg))
                        yield return field;
                }

                yield break;
        }
    }
}
