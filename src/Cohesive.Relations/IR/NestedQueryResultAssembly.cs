using System.Collections.Immutable;

namespace Cohesive.Relations.IR;

/// <summary>One scalar field copied from a canonical row slot to a nested result field.</summary>
/// <param name="Source">Canonical row field path.</param>
/// <param name="Target">Canonical result field path.</param>
public sealed record QueryResultFieldMapping(FieldPath Source, FieldPath Target);

/// <summary>A distinct child collection ordered by its ordinal string identity.</summary>
/// <param name="Target">Collection field in the parent result.</param>
/// <param name="Identity">Row slot containing the child's string identity; absent/null means no child.</param>
/// <param name="Fields">Scalar child mappings. Repeated identities must have equal mapped values.</param>
public sealed record QueryResultCollectionAssembly(
    FieldPath Target, FieldPath Identity, ImmutableArray<QueryResultFieldMapping> Fields);

/// <summary>Portable assembly of a single parent and its distinct child collections from complete canonical rows.</summary>
/// <param name="Result">The sole row-result branch being assembled.</param>
/// <param name="Shape">Canonical nested output shape.</param>
/// <param name="Identity">Row slot containing the parent's required ordinal string identity.</param>
/// <param name="Fields">Parent scalar mappings, which must agree across every row.</param>
/// <param name="Collections">Child collections; each is distinct and ordinally ordered by identity.</param>
/// <remarks>Zero rows mean no parent. Multiple parent identities or conflicting copies fail closed.
/// This terminal shaping contract does not change raw row acquisition, snapshot guarantees or completeness.</remarks>
public sealed record NestedQueryResultAssembly(
    QueryResultId Result, QualifiedShapeId Shape, FieldPath Identity,
    ImmutableArray<QueryResultFieldMapping> Fields,
    ImmutableArray<QueryResultCollectionAssembly> Collections)
{
    /// <summary>Rejects empty identities, ambiguous outputs and unsupported nested paths.</summary>
    /// <exception cref="ArgumentException">The portable assembly contract is invalid.</exception>
    public void Validate()
    {
        if (Fields.IsDefaultOrEmpty || Collections.IsDefaultOrEmpty
            || Fields.Any(field => field is null)
            || Collections.Any(collection => collection is null || collection.Fields.IsDefaultOrEmpty || collection.Fields.Any(field => field is null)))
            throw new ArgumentException("Nested assembly requires parent fields and nonempty child mappings.", "definition");
        var targets = Fields.Select(field => field.Target).Concat(Collections.Select(collection => collection.Target)).ToArray();
        if (targets.Distinct().Count() != targets.Length
            || Collections.Any(collection => collection.Fields.Select(field => field.Target).Distinct().Count() != collection.Fields.Length))
            throw new ArgumentException("Nested assembly cannot repeat target fields.", "definition");

        if (string.IsNullOrWhiteSpace(Result.Value) || string.IsNullOrWhiteSpace(Shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(Shape.ShapeId.Value)
            || !Direct(Identity)
            || Fields.Any(field => !Direct(field.Source) || !Direct(field.Target))
            || Collections.Any(collection => !Direct(collection.Target) || !Direct(collection.Identity)
                || collection.Fields.Any(field => !Direct(field.Source) || !Direct(field.Target))))
            throw new ArgumentException("Nested assembly requires valid identities and direct scalar/collection fields.");
    }

    /// <summary>Checks mapping coverage and scalar types against the canonical source and result graphs.</summary>
    /// <param name="rowShape">Flat projected row contract.</param>
    /// <param name="resultGraph">Graph owning the nested result and its named child types.</param>
    /// <exception cref="ArgumentNullException">A shape or graph is null.</exception>
    /// <exception cref="ArgumentException">A mapping is missing, unknown, or type-incompatible.</exception>
    public void Validate(Shape rowShape, ShapeGraph resultGraph)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(rowShape);
        ArgumentNullException.ThrowIfNull(resultGraph);
        if (resultGraph.Id != Shape.GraphId || !resultGraph.TryGetShape(Shape, out var resultShape))
            throw new ArgumentException("Nested result shape is not supplied.");
        RequireIdentity(Identity);
        var targets = Fields.Select(field => field.Target.Segments[0].Segment!)
            .Concat(Collections.Select(collection => collection.Target.Segments[0].Segment!)).ToHashSet(StringComparer.Ordinal);
        if (!targets.SetEquals(resultShape.Fields.Select(field => field.Name.Value)))
            throw new ArgumentException("Every nested result field must be mapped exactly once.");
        foreach (var mapping in Fields)
            RequireScalar(mapping, resultShape.GetField(mapping.Target.Segments[0].Segment!).Type);
        foreach (var collection in Collections)
        {
            RequireIdentity(collection.Identity);
            var field = resultShape.GetField(collection.Target.Segments[0].Segment!);
            var element = field.Type is ArrayTypeRef array ? array.ElementType
                : field.Cardinality == FieldCardinality.Many ? field.Type : null;
            if (element is not NamedTypeRef named || !resultGraph.TryGetType(named.TypeId, out var type)
                || type is not TypeDefinition.Structural child)
                throw new ArgumentException("Nested collections require a declared structural element type.");
            if (!collection.Fields.Select(mapping => mapping.Target.Segments[0].Segment!).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(child.Fields.Select(member => member.Name.Value)))
                throw new ArgumentException("Every child result field must be mapped exactly once.");
            foreach (var mapping in collection.Fields)
                RequireScalar(mapping, child.GetField(mapping.Target.Segments[0].Segment!).Type);
        }

        void RequireIdentity(FieldPath path)
        {
            if (!rowShape.TryGetField(path.Segments[0].Segment!, out var field)
                || field.Type is not ScalarTypeRef { Kind: ScalarTypeKind.String })
                throw new ArgumentException("Nested result identity slots must be declared strings.");
        }

        void RequireScalar(QueryResultFieldMapping mapping, TypeRef target)
        {
            if (!rowShape.TryGetField(mapping.Source.Segments[0].Segment!, out var source)
                || source.Type is not ScalarTypeRef || !source.Type.Equals(target))
                throw new ArgumentException("Nested result mappings require matching scalar field types.");
        }
    }

    static bool Direct(FieldPath path) => path.Segments.Length == 1
        && path.Segments[0].TryGetFieldIdentity(out _);
}
