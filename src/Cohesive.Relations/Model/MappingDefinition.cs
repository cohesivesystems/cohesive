using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Model;

/// <summary>
/// Stable mapping identifier.
/// </summary>
public sealed record MappingId
{
    /// <summary>
    /// Creates a mapping id value.
    /// </summary>
    public MappingId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw mapping id text.
    /// </summary>
    public string Value { get; init; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable mapping name.
/// </summary>
public sealed record MappingName
{
    /// <summary>
    /// Creates a mapping name value.
    /// </summary>
    public MappingName(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw mapping name text.
    /// </summary>
    public string Value { get; init; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Mapping evaluation scope.
/// </summary>
public enum MappingScope
{
    /// <summary>Represents the rooted option.</summary>
    Rooted = 0,
    
    /// <summary>Represents the set option.</summary>
    Set = 1
}

/// <summary>
/// Mapping execution preference.
/// </summary>
public enum MappingExecutionPreference
{
    /// <summary>Represents the in memory option.</summary>
    InMemory = 0,
    
    /// <summary>Represents the materialized option.</summary>
    Materialized = 1,
    
    /// <summary>Represents the code generated option.</summary>
    CodeGenerated = 2
}

/// <summary>
/// Mapping definition between source and target shapes.
/// </summary>
public sealed record MappingDefinition
{
    /// <summary>
    /// Creates a mapping definition.
    /// </summary>
    [JsonConstructor]
    public MappingDefinition(
        MappingId id,
        MappingName name,
        ShapeId targetShapeId,
        ImmutableArray<FieldAssignment> assignments = default,
        Expr? predicate = null,
        Expr? forEach = null,
        Expr? key = null,
        Expr? entity = null,
        MappingScope scope = MappingScope.Rooted,
        MappingMetadata? metadata = null
        )
    {
        Id = Guard.RequireNotNull(id);
        Name = Guard.RequireNotNull(name);
        TargetShapeId = targetShapeId;
        Assignments = assignments.IsDefault ? [] : assignments;
        Predicate = predicate;
        ForEach = forEach;
        Key = key;
        Entity = entity;
        Scope = scope;
        Metadata = metadata ?? MappingMetadata.Default;

        if (Assignments.IsDefaultOrEmpty)
            throw new ArgumentException("Relation mapping requires at least one assignment.", nameof(assignments));
    }

    /// <summary>
    /// Mapping identifier.
    /// </summary>
    public MappingId Id { get; init; }

    /// <summary>
    /// Mapping name.
    /// </summary>
    public MappingName Name { get; init; }

    /// <summary>
    /// Target shape id.
    /// </summary>
    public ShapeId TargetShapeId { get; init; }

    /// <summary>
    /// Relation mapping field assignments.
    /// </summary>
    public ImmutableArray<FieldAssignment> Assignments { get; init; }

    /// <summary>
    /// Optional relation mapping predicate, evaluated in <see cref="RelationEvaluationContext"/>.
    /// </summary>
    public Expr? Predicate { get; init; }

    /// <summary>
    /// Optional relation mapping collection expression, evaluated in <see cref="RelationEvaluationContext"/>.
    /// </summary>
    public Expr? ForEach { get; init; }

    /// <summary>
    /// Optional emitted key expression.
    /// </summary>
    public Expr? Key { get; init; }

    /// <summary>
    /// Optional emitted entity expression.
    /// </summary>
    public Expr? Entity { get; init; }

    /// <summary>
    /// Mapping evaluation scope.
    /// </summary>
    public MappingScope Scope { get; init; }

    /// <summary>
    /// Mapping metadata.
    /// </summary>
    public MappingMetadata Metadata { get; init; }
}
