using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Serialization;

/// <summary>Stable structural-path policies for set-like arrays in canonical relation documents.</summary>
internal static class RelationCanonicalJsonArrayOrderings
{
    const string EvaluationDefinitionPrefix = "/compilation/definitionDocument/definition";

    internal static CanonicalJsonArrayOrdering Definition(CanonicalJsonArrayPath path) =>
        ResolveDefinition(path.Value);

    internal static CanonicalJsonArrayOrdering Draft(CanonicalJsonArrayPath path)
    {
        var value = path.Value.AsSpan();
        if (value.StartsWith("/input", StringComparison.Ordinal))
        {
            var logical = ResolveLogicalQuery(value["/input".Length..]);
            if (logical.Kind != CanonicalJsonArrayOrderingKind.Sequence)
                return logical;
        }

        return value switch
        {
            _ when value.Equals("/projection/assignments", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("id"),
            _ when value.Equals("/projection/assignments/*/candidates", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("id"),
            _ when value.Equals("/invariants", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("name"),
            _ when value.Equals("/projection/assignments/*/resolution/candidateIds", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.StringSet,
            _ when value.Equals("/projection/assignments/*/resolution/reasons", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.StringSet,
            _ => CanonicalJsonArrayOrdering.Sequence
        };
    }

    internal static CanonicalJsonArrayOrdering Evaluation(CanonicalJsonArrayPath path)
    {
        var value = path.Value.AsSpan();
        if (value.StartsWith(EvaluationDefinitionPrefix, StringComparison.Ordinal))
        {
            var definition = ResolveDefinition(value[EvaluationDefinitionPrefix.Length..]);
            if (definition.Kind != CanonicalJsonArrayOrderingKind.Sequence)
                return definition;
        }

        return value.Equals("/suppliedRoots/observations", StringComparison.Ordinal)
            ? CanonicalJsonArrayOrdering.ObjectSet("id")
            : CanonicalJsonArrayOrdering.Sequence;
    }

    internal static CanonicalJsonArrayOrdering ShapeGraph(CanonicalJsonArrayPath path) =>
        path.Value is "/shapes" or "/namedTypes"
            ? CanonicalJsonArrayOrdering.ObjectSet("id")
            : CanonicalJsonArrayOrdering.Sequence;

    internal static CanonicalJsonArrayOrdering RelationshipCatalog(CanonicalJsonArrayPath _)
    {
        // RelationshipCatalog establishes a total canonical order itself and deliberately retains duplicate
        // identifiers for validation, so the generic writer must preserve that normalized sequence.
        return CanonicalJsonArrayOrdering.Sequence;
    }

    static CanonicalJsonArrayOrdering ResolveDefinition(ReadOnlySpan<char> path)
    {
        if (path.StartsWith("/body", StringComparison.Ordinal))
        {
            var logical = ResolveLogicalQuery(path["/body".Length..]);
            if (logical.Kind != CanonicalJsonArrayOrderingKind.Sequence)
                return logical;
        }

        return path switch
        {
            _ when path.Equals("/results", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("id"),
            _ when path.Equals("/invariants", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("name"),
            _ => CanonicalJsonArrayOrdering.Sequence
        };
    }

    static CanonicalJsonArrayOrdering ResolveLogicalQuery(ReadOnlySpan<char> path) =>
        path switch
        {
            _ when path.Equals("/nodes", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("id"),
            _ when path.Equals("/parameters", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("id"),
            _ when path.Equals("/nodes/*/assignments", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("id"),
            _ when path.Equals("/nodes/*/groupings", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("id"),
            _ when path.Equals("/nodes/*/aggregates", StringComparison.Ordinal) =>
                CanonicalJsonArrayOrdering.ObjectSet("id"),
            _ => CanonicalJsonArrayOrdering.Sequence
        };
}
