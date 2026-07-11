using Cohesive.Model;

namespace Cohesive.Relations.Model;

/// <summary>
/// Query helpers for lineage and projection references.
/// </summary>
public static class RelationLineage
{
    /// <summary>
    /// Returns lineage contributions that created <paramref name="targetField"/>.
    /// </summary>
    public static IReadOnlyList<LineageContribution> Contributors(Observation observation, string targetField)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.Lineage.Fields
            .Where(x => x.TargetField == targetField)
            .SelectMany(x => x.Contributions)
            .ToArray();
    }

    /// <summary>
    /// Finds rule/assignment node ids in the definition that reference <paramref name="field"/>.
    /// </summary>
    public static IReadOnlyList<string> ReferencingNodes(RelationDefinition definition, string field)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<string> nodeIds = [];
        if (definition.Filter is not null && ReferencesField(definition.Filter, field))
            nodeIds.Add("<relation.filter>");

        foreach (var mapping in definition.Mappings)
        {
            if (mapping.Predicate is not null && ReferencesField(mapping.Predicate, field))
                nodeIds.Add($"{mapping.Id.Value}.predicate");

            if (mapping.ForEach is not null && ReferencesField(mapping.ForEach, field))
                nodeIds.Add($"{mapping.Id.Value}.forEach");

            foreach (var assignment in mapping.Assignments)
            {
                if (ReferencesField(assignment.Expr, field))
                    nodeIds.Add(assignment.Id ?? $"{mapping.Id.Value}.{assignment.TargetField}");
            }
        }

        return nodeIds.Distinct(StringComparer.Ordinal).ToArray();
    }

    static bool ReferencesField(Expr relExpression, string field)
    {
        return relExpression switch
        {
            FieldExpr fieldExpr => ReferencesFieldPath(fieldExpr.Path, field),
            UnaryExpr unary => ReferencesField(unary.Operand, field),
            BinaryExpr binary => ReferencesField(binary.Left, field) || ReferencesField(binary.Right, field),
            ConditionalExpr conditional => ReferencesField(conditional.Test, field)
                                                 || ReferencesField(conditional.IfTrue, field)
                                                 || ReferencesField(conditional.IfFalse, field),
            CallExpr call => call.Arguments.Any(x => ReferencesField(x, field)),
            _ => false
        };
    }

    static bool ReferencesFieldPath(Cohesive.Model.FieldPath path, string field)
    {
        return path.TryGetTerminalFieldIdentity(out var fieldIdentity)
               && string.Equals(fieldIdentity, field, StringComparison.Ordinal);
    }
}
