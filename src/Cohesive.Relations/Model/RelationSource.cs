namespace Cohesive.Relations.Model;

/// <summary>
/// Source alias used by relation sources and joins.
/// </summary>
public sealed record SourceAlias
{
    /// <summary>
    /// Creates a source alias value.
    /// </summary>
    public SourceAlias(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw alias text.
    /// </summary>
    public string Value { get; init; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Source cardinality.
/// </summary>
public enum SourceCardinality
{
    /// <summary>Represents the single option.</summary>
    Single = 0,
    /// <summary>Represents the many option.</summary>
    Many = 1
}

/// <summary>
/// Relation source definition.
/// </summary>
public sealed record RelationSource
{
    /// <summary>
    /// Creates a relation source definition.
    /// </summary>
    public RelationSource(SourceAlias alias, ShapeId shapeId, SourceCardinality cardinality)
    {
        Alias = Guard.RequireNotNull(alias);
        ShapeId = shapeId;
        Cardinality = cardinality;
    }

    /// <summary>
    /// Source binding alias.
    /// </summary>
    public SourceAlias Alias { get; init; }

    /// <summary>
    /// Source shape id.
    /// </summary>
    public ShapeId ShapeId { get; init; }

    /// <summary>
    /// Source cardinality semantics.
    /// </summary>
    public SourceCardinality Cardinality { get; init; }
}
