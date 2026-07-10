using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Relations.Model;

/// <summary>
/// Stable relation identifier.
/// </summary>
public sealed record RelationId
{
    /// <summary>
    /// Creates a relation id value.
    /// </summary>
    public RelationId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw relation id text.
    /// </summary>
    public string Value { get; init; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable relation name.
/// </summary>
public sealed record RelationName
{
    /// <summary>
    /// Creates a relation name value.
    /// </summary>
    public RelationName(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw relation name text.
    /// </summary>
    public string Value { get; init; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Relation-level metadata and optimization hints.
/// </summary>
public sealed record RelationMetadata
{
    /// <summary>
    /// Creates relation metadata.
    /// </summary>
    public RelationMetadata(
        bool allowCodegen,
        bool deterministic,
        ImmutableDictionary<string, string>? hints = null
        )
    {
        AllowCodegen = allowCodegen;
        Deterministic = deterministic;
        Hints = hints ?? ImmutableDictionary<string, string>.Empty;
    }

    /// <summary>
    /// True when code-generation is allowed for this relation.
    /// </summary>
    public bool AllowCodegen { get; init; }

    /// <summary>
    /// True when relation evaluation is deterministic.
    /// </summary>
    public bool Deterministic { get; init; }

    /// <summary>
    /// Optional implementation hints.
    /// </summary>
    public ImmutableDictionary<string, string> Hints { get; init; }

    /// <summary>
    /// Conservative default metadata.
    /// </summary>
    public static RelationMetadata Default { get; } = new(
        allowCodegen: false,
        deterministic: true);
}

/// <summary>
/// Canonical relation definition.
/// </summary>
public sealed record RelationDefinition
{
    /// <summary>
    /// Creates a relation definition.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public RelationDefinition(
        RelationId id,
        RelationName name,
        ImmutableArray<RelationSource> sources,
        ImmutableArray<JoinDefinition> joins = default,
        Expr? filter = null,
        ShapeId? baseRowShapeId = null,
        ImmutableArray<MappingDefinition> mappings = default,
        MaterializationSpec? materialization = null,
        RelationMetadata? metadata = null,
        ImmutableArray<InvariantDefinition> invariants = default
        )
    {
        Id = Guard.RequireNotNull(id);
        Name = Guard.RequireNotNull(name);
        Sources = sources.IsDefault ? [] : sources;
        Joins = joins.IsDefault ? [] : joins;
        Filter = filter;
        BaseRowShapeId = baseRowShapeId;
        Mappings = mappings.IsDefault ? [] : mappings;
        Materialization = materialization;
        Metadata = metadata ?? RelationMetadata.Default;
        Invariants = invariants.IsDefault ? [] : invariants;

        if (Sources.IsDefaultOrEmpty)
            throw new ArgumentException(message: "Relation requires at least one source.", paramName: nameof(sources));
        
        if (Mappings.IsDefaultOrEmpty)
            throw new ArgumentException(
                message: "Relation requires at least one mapping definition.",
                paramName: nameof(mappings)
                );
        
        if (Mappings.Any(x => !x.IsRelationMapping))
            throw new ArgumentException(
                message: "Relation mappings must be relation mapping definitions.",
                paramName: nameof(mappings)
                );
        
        var duplicateSourceByAlias = Sources.TryGetDuplicateByKey(x => x.Alias.Value, StringComparer.Ordinal);
        if (duplicateSourceByAlias is not null)
            throw new ArgumentException(
                message: $"Relation '{Name.Value}' contains duplicate source alias '{duplicateSourceByAlias.Alias.Value}'.",
                paramName: nameof(sources)
                );

        ValidateJoinAliases(Sources, Joins);
    }

    /// <summary>
    /// Stable relation identifier.
    /// </summary>
    public RelationId Id { get; init; }

    /// <summary>
    /// Human-readable relation name.
    /// </summary>
    public RelationName Name { get; init; }

    /// <summary>
    /// Root semantic sources.
    /// </summary>
    public ImmutableArray<RelationSource> Sources { get; init; }

    /// <summary>
    /// Semantic joins.
    /// </summary>
    public ImmutableArray<JoinDefinition> Joins { get; init; }

    /// <summary>
    /// Optional global predicate over the relation rowset.
    /// </summary>
    public Expr? Filter { get; init; }

    /// <summary>
    /// Optional canonical intermediate row shape.
    /// </summary>
    public ShapeId? BaseRowShapeId { get; init; }

    /// <summary>
    /// Mappings produced from this relation.
    /// </summary>
    public ImmutableArray<MappingDefinition> Mappings { get; init; }

    /// <summary>
    /// Optional materialization policy.
    /// </summary>
    public MaterializationSpec? Materialization { get; init; }

    /// <summary>
    /// Compilation and optimization metadata.
    /// </summary>
    public RelationMetadata Metadata { get; init; }

    /// <summary>
    /// Optional projection invariants.
    /// </summary>
    public ImmutableArray<InvariantDefinition> Invariants { get; init; }

    /// <summary>
    /// True when this relation is materialized.
    /// </summary>
    [JsonIgnore]
    public bool IsMaterialized => Materialization is { IsEnabled: true };

    /// <summary>
    /// First source shape id, used as root in rooted execution.
    /// </summary>
    [JsonIgnore]
    public ShapeId RootSourceShapeId => Sources[0].ShapeId;

    static void ValidateJoinAliases(
        IReadOnlyList<RelationSource> sources,
        IReadOnlyList<JoinDefinition> joins)
    {
        if (joins.Count == 0)
            return;

        var aliases = sources
            .Select(x => x.Alias.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var join in joins)
        {
            if (!aliases.Contains(join.Left.Value))
                throw new ArgumentException(
                    message: $"Join left alias '{join.Left.Value}' is not declared in relation sources.",
                    paramName: nameof(joins));
            if (!aliases.Contains(join.Right.Value))
                throw new ArgumentException(
                    message: $"Join right alias '{join.Right.Value}' is not declared in relation sources.",
                    paramName: nameof(joins));
        }
    }
}
