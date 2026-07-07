using System.Collections.Immutable;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Stable ontology concept definition with generic lexical and property metadata.
/// </summary>
/// <example>
/// <code>
/// var concept = new Concept(
///     conceptId: "shipment.weight",
///     label: "Shipment Weight",
///     lexicalForms: ["gross weight", "wt"],
///     properties:
///     [
///         ("kind", "measure"),
///         ("valueCategory", "quantity"),
///         ("unit", "kg")
///     ]);
/// </code>
/// </example>
public sealed record Concept : IEquatable<Concept>
{
    /// <summary>
    /// Creates one ontology concept.
    /// </summary>
    public Concept(
        string conceptId,
        string? label = null,
        ImmutableArray<string> lexicalForms = default,
        ImmutableDictionary<string, string>? properties = null
    )
    {
        ConceptId = Guard.RequireNotNullOrWhiteSpace(conceptId);
        Label = NormalizeOptional(label);
        LexicalForms = NormalizeLexicalForms(lexicalForms);
        Properties = NormalizeProperties(properties);
    }

    /// <summary>
    /// Stable concept identifier.
    /// </summary>
    public string ConceptId { get; init; }

    /// <summary>
    /// Optional preferred label.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Optional alternate lexical forms, such as aliases or abbreviations.
    /// </summary>
    public ImmutableArray<string> LexicalForms { get; init; }

    /// <summary>
    /// Optional string-valued concept properties.
    /// </summary>
    public ImmutableDictionary<string, string> Properties { get; init; }

    /// <summary>
    /// Tries to resolve one normalized property value.
    /// </summary>
    public bool TryGetProperty(string propertyName, out string value)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            value = string.Empty;
            return false;
        }

        return Properties.TryGetValue(propertyName.Trim(), out value!);
    }

    /// <summary>
    /// Value equality based on semantic field contents and normalized collection members.
    /// </summary>
    public bool Equals(Concept? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return string.Equals(ConceptId, other.ConceptId, StringComparison.Ordinal)
               && string.Equals(Label, other.Label, StringComparison.Ordinal)
               && SequenceEqual(LexicalForms, other.LexicalForms, StringComparer.OrdinalIgnoreCase)
               && DictionaryEqual(Properties, other.Properties);
    }

    /// <summary>
    /// Hash code based on semantic field contents and normalized collection members.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(ConceptId, StringComparer.Ordinal);
        hash.Add(Label, StringComparer.Ordinal);
        AddSequenceHash(ref hash, LexicalForms, StringComparer.OrdinalIgnoreCase);
        AddDictionaryHash(ref hash, Properties);
        return hash.ToHashCode();
    }

    static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    static ImmutableArray<string> NormalizeLexicalForms(ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty)
            return [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> normalized = [];
        foreach (var raw in values)
        {
            var value = raw?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (!seen.Add(value))
                continue;
            normalized.Add(value);
        }

        return [.. normalized];
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

    static bool SequenceEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right, IEqualityComparer<T> comparer)
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (!comparer.Equals(left[i], right[i]))
                return false;
        }

        return true;
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

    static void AddSequenceHash<T>(ref HashCode hash, ImmutableArray<T> values, IEqualityComparer<T> comparer)
    {
        foreach (var value in values)
            hash.Add(value, comparer);
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