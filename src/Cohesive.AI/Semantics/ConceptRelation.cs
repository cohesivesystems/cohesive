using System.Collections.Immutable;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Typed morphism between two ontology concepts.
/// </summary>
/// <example>
/// <code>
/// var relation = new ConceptRelation(
///     sourceConceptId: "date.pickup.requested",
///     targetConceptId: "date.pickup.type",
///     relationTypeId: StandardRelationTypeIds.SubConceptOf
/// );
/// </code>
/// </example>
public sealed record ConceptRelation : IEquatable<ConceptRelation>
{
    /// <summary>
    /// Creates one concept relation.
    /// </summary>
    public ConceptRelation(
        string sourceConceptId,
        string targetConceptId,
        string relationTypeId,
        double weight = 1d,
        ImmutableDictionary<string, string>? properties = null
        )
    {
        SourceConceptId = Guard.RequireNotNullOrWhiteSpace(sourceConceptId).Trim();
        TargetConceptId = Guard.RequireNotNullOrWhiteSpace(targetConceptId).Trim();
        RelationTypeId = Guard.RequireNotNullOrWhiteSpace(relationTypeId).Trim();
        Weight = ValidateWeight(weight);
        Properties = NormalizeProperties(properties);
    }

    /// <summary>
    /// Source concept id.
    /// </summary>
    public string SourceConceptId { get; init; }

    /// <summary>
    /// Target concept id.
    /// </summary>
    public string TargetConceptId { get; init; }

    /// <summary>
    /// Relation type id.
    /// </summary>
    public string RelationTypeId { get; init; }

    /// <summary>
    /// Optional relation weight.
    /// </summary>
    public double Weight { get; init; }

    /// <summary>
    /// Optional string-valued relation properties.
    /// </summary>
    public ImmutableDictionary<string, string> Properties { get; init; }

    /// <summary>
    /// Value equality based on semantic field contents and normalized collection members.
    /// </summary>
    public bool Equals(ConceptRelation? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return string.Equals(SourceConceptId, other.SourceConceptId, StringComparison.Ordinal)
               && string.Equals(TargetConceptId, other.TargetConceptId, StringComparison.Ordinal)
               && string.Equals(RelationTypeId, other.RelationTypeId, StringComparison.Ordinal)
               && Weight.Equals(other.Weight)
               && DictionaryEqual(Properties, other.Properties);
    }

    /// <summary>
    /// Hash code based on semantic field contents and normalized collection members.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(SourceConceptId, StringComparer.Ordinal);
        hash.Add(TargetConceptId, StringComparer.Ordinal);
        hash.Add(RelationTypeId, StringComparer.Ordinal);
        hash.Add(Weight);
        AddDictionaryHash(ref hash, Properties);
        return hash.ToHashCode();
    }

    static double ValidateWeight(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Relation weight must be finite.");
        return value;
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
}
