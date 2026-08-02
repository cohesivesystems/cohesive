using Cohesive.Model;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Compilation;

/// <summary>Shared semantic predicates over exact fields in a compiled Relations plan.</summary>
public static class RelationQueryFieldSemantics
{
    /// <summary>Determines whether a field is one single string-valued identity field.</summary>
    /// <param name="plan">Exact compiled plan whose shape provenance defines the field.</param>
    /// <param name="field">Field reference to classify.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="field"/> names a top-level, single-cardinality,
    /// string-valued identity field in <paramref name="plan"/>; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static bool IsSingleStringIdentityField(
        CompiledRelationQueryPlan plan,
        RelationQueryFieldReference field)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (field.Path.Segments.Length != 1
            || !field.Path.Segments[0].TryGetFieldIdentity(out var fieldName))
        {
            return false;
        }

        var graph = plan.Provenance.ShapeDocuments
            .SingleOrDefault(document => document.Graph.Id == field.Shape.GraphId)
            ?.Graph;
        var shape = graph?.TryGetShape(field.Shape);
        return shape is not null
            && shape.TryGetField(fieldName, out var definition)
            && definition.Role == FieldRole.Identity
            && definition.Cardinality == FieldCardinality.Single
            && definition.Type is ScalarTypeRef { Kind: ScalarTypeKind.String };
    }
}
