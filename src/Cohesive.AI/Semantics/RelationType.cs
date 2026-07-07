using System.Collections.Immutable;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Relation-type definition used by ontology morphisms.
/// </summary>
/// <example>
/// <code>
/// var relationType = new RelationType(
///     relationTypeId: StandardRelationTypeIds.SubConceptOf,
///     label: "SubConceptOf"
/// );
/// </code>
/// </example>
public sealed record RelationType : IEquatable<RelationType>
{
    /// <summary>
    /// Creates one relation type.
    /// </summary>
    public RelationType(
        string relationTypeId,
        string? label = null,
        ImmutableDictionary<string, string>? properties = null
        )
    {
        RelationTypeId = Guard.RequireNotNullOrWhiteSpace(relationTypeId).Trim();
        Label = NormalizeOptional(label) ?? RelationTypeId;
        Properties = NormalizeProperties(properties);
    }

    /// <summary>
    /// Stable relation-type identifier.
    /// </summary>
    public string RelationTypeId { get; init; }

    /// <summary>
    /// Optional display label.
    /// </summary>
    public string Label { get; init; }

    /// <summary>
    /// Optional string-valued relation-type properties.
    /// </summary>
    public ImmutableDictionary<string, string> Properties { get; init; }

    /// <summary>
    /// Value equality based on semantic field contents and normalized collection members.
    /// </summary>
    public bool Equals(RelationType? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return string.Equals(RelationTypeId, other.RelationTypeId, StringComparison.Ordinal)
               && string.Equals(Label, other.Label, StringComparison.Ordinal)
               && DictionaryEqual(Properties, other.Properties);
    }

    /// <summary>
    /// Hash code based on semantic field contents and normalized collection members.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(RelationTypeId, StringComparer.Ordinal);
        hash.Add(Label, StringComparer.Ordinal);
        AddDictionaryHash(ref hash, Properties);
        return hash.ToHashCode();
    }

    static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    static ImmutableDictionary<string, string> NormalizeProperties(ImmutableDictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0)
            return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

        var normalized = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, rawValue) in properties)
        {
            var key = rawKey?.Trim();
            var value = rawValue?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            normalized[key] = value;
        }

        return normalized.ToImmutable();
    }

    static bool DictionaryEqual(ImmutableDictionary<string, string> left, ImmutableDictionary<string, string> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, leftValue) in left)
        {
            if (!right.TryGetValue(key, out var rightValue))
                return false;
            if (!string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    static void AddDictionaryHash(ref HashCode hash, ImmutableDictionary<string, string> values)
    {
        foreach (var (key, value) in values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(key, StringComparer.OrdinalIgnoreCase);
            hash.Add(value, StringComparer.Ordinal);
        }
    }

    public ConceptRelation CreateRelation(string sourceConceptId, string targetConceptId) => 
        new(sourceConceptId: sourceConceptId, targetConceptId: targetConceptId, relationTypeId: RelationTypeId);
}

/// <summary>
/// Standard relation-type identifiers used by the default ontology vocabulary.
/// </summary>
public static class StandardRelationTypeIds
{
    public const string SubConceptOf = "subconcept_of";
    public const string EquivalentTo = "equivalent_to";
    public const string DisjointWith = "disjoint_with";
    public const string PartOf = "part_of";
    public const string HasProperty = "has_property";
    public const string DerivedFrom = "derived_from";
    public const string RelatedTo = "related_to";
    public const string Requires = "requires";
}

/// <summary>
/// Default relation-type vocabulary and global laws used by the ontology layer.
/// </summary>
public static class StandardRelationTypes
{
    /// <summary>
    /// Creates the default relation-type definitions.
    /// </summary>
    public static ImmutableDictionary<string, RelationType> CreateDefaults()
    {
        return ImmutableDictionary.CreateRange<string, RelationType>(StringComparer.Ordinal,
        [
            KeyValuePair.Create(StandardRelationTypeIds.SubConceptOf, new RelationType(StandardRelationTypeIds.SubConceptOf, "SubConceptOf")),
            KeyValuePair.Create(StandardRelationTypeIds.EquivalentTo, new RelationType(StandardRelationTypeIds.EquivalentTo, "EquivalentTo")),
            KeyValuePair.Create(StandardRelationTypeIds.DisjointWith, new RelationType(StandardRelationTypeIds.DisjointWith, "DisjointWith")),
            KeyValuePair.Create(StandardRelationTypeIds.PartOf, new RelationType(StandardRelationTypeIds.PartOf, "PartOf")),
            KeyValuePair.Create(StandardRelationTypeIds.HasProperty, new RelationType(StandardRelationTypeIds.HasProperty, "HasProperty")),
            KeyValuePair.Create(StandardRelationTypeIds.DerivedFrom, new RelationType(StandardRelationTypeIds.DerivedFrom, "DerivedFrom")),
            KeyValuePair.Create(StandardRelationTypeIds.RelatedTo, new RelationType(StandardRelationTypeIds.RelatedTo, "RelatedTo")),
            KeyValuePair.Create(StandardRelationTypeIds.Requires, new RelationType(StandardRelationTypeIds.Requires, "Requires"))
        ]);
    }

    /// <summary>
    /// Creates the default global laws for the standard relation-type vocabulary.
    /// </summary>
    public static ImmutableArray<OntologyRule> CreateDefaultRules()
    {
        return
        [
            new RelationLawRule(
                relationTypeId: StandardRelationTypeIds.SubConceptOf,
                flags: RelationLawFlags.Reflexive | RelationLawFlags.Antisymmetric | RelationLawFlags.Transitive
                ),
            new RelationLawRule(
                relationTypeId: StandardRelationTypeIds.EquivalentTo,
                flags: RelationLawFlags.Reflexive | RelationLawFlags.Symmetric | RelationLawFlags.Transitive
                ),
            new RelationLawRule(
                relationTypeId: StandardRelationTypeIds.DisjointWith,
                flags: RelationLawFlags.Irreflexive | RelationLawFlags.Symmetric
                ),
            new RelationLawRule(
                relationTypeId: StandardRelationTypeIds.PartOf,
                flags: RelationLawFlags.Transitive
                )
        ];
    }
}
