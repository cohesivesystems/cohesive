using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Cohesive.Relations.Model;

/// <summary>
/// Immutable, deterministically ordered catalog of canonical semantic relationships.
/// </summary>
public sealed class RelationshipCatalog
{
    readonly ImmutableDictionary<RelationshipId, RelationshipDefinition> relationshipsById;
    readonly ImmutableDictionary<QualifiedShapeId, ImmutableArray<RelationshipDefinition>> outgoingBySource;
    readonly ImmutableDictionary<QualifiedShapeId, ImmutableArray<RelationshipDefinition>> incomingByTarget;

    /// <summary>Empty relationship catalog.</summary>
    public static RelationshipCatalog Empty { get; } = new([]);

    /// <summary>Creates an immutable relationship catalog.</summary>
    /// <param name="relationships">Canonical relationship definitions.</param>
    /// <exception cref="ArgumentException"><paramref name="relationships"/> contains a <see langword="null"/> definition.</exception>
    [JsonConstructor]
    public RelationshipCatalog(ImmutableArray<RelationshipDefinition> relationships)
    {
        var normalized = relationships.IsDefault ? [] : relationships;
        if (normalized.Any(static relationship => relationship is null))
            throw new ArgumentException("A relationship catalog cannot contain null definitions.", nameof(relationships));

        Relationships =
        [
            .. normalized
                .OrderBy(static relationship => relationship.Id.Value, StringComparer.Ordinal)
                .ThenBy(static relationship => relationship.SourceShape.GraphId.Value, StringComparer.Ordinal)
                .ThenBy(static relationship => relationship.SourceShape.ShapeId.Value, StringComparer.Ordinal)
                .ThenBy(
                    static relationship => relationship.SourceReference,
                    FieldPathOrdinalComparer.Instance)
                .ThenBy(static relationship => relationship.TargetShape.GraphId.Value, StringComparer.Ordinal)
                .ThenBy(static relationship => relationship.TargetShape.ShapeId.Value, StringComparer.Ordinal)
                .ThenBy(static relationship => TargetKeySortName(relationship.TargetKey), StringComparer.Ordinal)
                .ThenBy(static relationship => (int)relationship.SourceReferenceUniqueness)
        ];

        var byId = ImmutableDictionary.CreateBuilder<RelationshipId, RelationshipDefinition>();
        var outgoing = new Dictionary<QualifiedShapeId, List<RelationshipDefinition>>();
        var incoming = new Dictionary<QualifiedShapeId, List<RelationshipDefinition>>();
        foreach (var relationship in Relationships)
        {
            byId.TryAdd(relationship.Id, relationship);
            AddToEndpoint(outgoing, relationship.SourceShape, relationship);
            AddToEndpoint(incoming, relationship.TargetShape, relationship);
        }

        relationshipsById = byId.ToImmutable();
        outgoingBySource = FreezeEndpointIndex(outgoing);
        incomingByTarget = FreezeEndpointIndex(incoming);
    }

    /// <summary>Relationship definitions ordered by ordinal relationship identifier and semantic tie-breakers.</summary>
    public ImmutableArray<RelationshipDefinition> Relationships { get; }

    /// <summary>Number of definitions in this catalog, including invalid duplicate identifiers retained for diagnostics.</summary>
    [JsonIgnore]
    public int Count => Relationships.Length;

    /// <summary>Looks up the first deterministically ordered definition with the supplied identifier.</summary>
    /// <param name="id">Relationship identifier to resolve.</param>
    /// <param name="relationship">Resolved definition when found.</param>
    /// <returns><see langword="true"/> when the identifier is present; otherwise <see langword="false"/>.</returns>
    public bool TryGetRelationship(
        RelationshipId id,
        [MaybeNullWhen(false)] out RelationshipDefinition relationship) =>
        relationshipsById.TryGetValue(id, out relationship);

    /// <summary>Gets a relationship definition by identifier.</summary>
    /// <param name="id">Relationship identifier to resolve.</param>
    /// <returns>The resolved relationship definition.</returns>
    /// <exception cref="KeyNotFoundException">No definition has the supplied identifier.</exception>
    public RelationshipDefinition GetRelationship(RelationshipId id)
    {
        if (TryGetRelationship(id, out var relationship))
            return relationship;

        throw new KeyNotFoundException($"Relationship catalog does not contain relationship '{id.Value}'.");
    }

    /// <summary>Gets relationships whose references are held by the supplied source shape.</summary>
    /// <param name="sourceShape">Graph-qualified source endpoint.</param>
    /// <returns>Definitions ordered by ordinal relationship identifier.</returns>
    public ImmutableArray<RelationshipDefinition> GetOutgoing(QualifiedShapeId sourceShape) =>
        outgoingBySource.TryGetValue(sourceShape, out var relationships) ? relationships : [];

    /// <summary>Gets relationships that address the supplied target shape.</summary>
    /// <param name="targetShape">Graph-qualified target endpoint.</param>
    /// <returns>Definitions ordered by ordinal relationship identifier.</returns>
    public ImmutableArray<RelationshipDefinition> GetIncoming(QualifiedShapeId targetShape) =>
        incomingByTarget.TryGetValue(targetShape, out var relationships) ? relationships : [];

    static void AddToEndpoint(
        Dictionary<QualifiedShapeId, List<RelationshipDefinition>> index,
        QualifiedShapeId endpoint,
        RelationshipDefinition relationship)
    {
        if (!index.TryGetValue(endpoint, out var definitions))
        {
            definitions = [];
            index.Add(endpoint, definitions);
        }

        definitions.Add(relationship);
    }

    static ImmutableDictionary<QualifiedShapeId, ImmutableArray<RelationshipDefinition>> FreezeEndpointIndex(
        Dictionary<QualifiedShapeId, List<RelationshipDefinition>> index)
    {
        var builder = ImmutableDictionary.CreateBuilder<QualifiedShapeId, ImmutableArray<RelationshipDefinition>>();
        foreach (var (endpoint, definitions) in index)
            builder.Add(endpoint, [.. definitions]);
        return builder.ToImmutable();
    }

    static string TargetKeySortName(RelationshipTargetKey? targetKey) => targetKey switch
    {
        ObservationIdentityRelationshipTargetKey => RelationshipWireNames.ObservationIdentityTargetKey,
        null => string.Empty,
        _ => targetKey.GetType().FullName ?? targetKey.GetType().Name
    };

    sealed class FieldPathOrdinalComparer : IComparer<FieldPath>
    {
        public static FieldPathOrdinalComparer Instance { get; } = new();

        public int Compare(FieldPath left, FieldPath right)
        {
            var leftSegments = left.Segments.IsDefault
                ? ImmutableArray<FieldPathSegment>.Empty
                : left.Segments;
            var rightSegments = right.Segments.IsDefault
                ? ImmutableArray<FieldPathSegment>.Empty
                : right.Segments;
            var commonLength = Math.Min(leftSegments.Length, rightSegments.Length);
            for (var index = 0; index < commonLength; index++)
            {
                var leftSegment = leftSegments[index];
                var rightSegment = rightSegments[index];
                var kindComparison = ((int)leftSegment.Kind).CompareTo((int)rightSegment.Kind);
                if (kindComparison != 0)
                    return kindComparison;

                var segmentComparison = StringComparer.Ordinal.Compare(
                    leftSegment.Segment,
                    rightSegment.Segment);
                if (segmentComparison != 0)
                    return segmentComparison;
            }

            return leftSegments.Length.CompareTo(rightSegments.Length);
        }
    }
}
