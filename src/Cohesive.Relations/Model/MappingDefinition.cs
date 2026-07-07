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

    public override string ToString() => Value;
}

/// <summary>
/// Mapping direction semantics.
/// </summary>
public enum MappingDirection
{
    SourceToTarget = 0,
    TargetToSource = 1,
    Bidirectional = 2
}

/// <summary>
/// Mapping semantic kind.
/// </summary>
public enum MappingKind
{
    Relation = 0,
    Object = 1
}

/// <summary>
/// Mapping evaluation scope.
/// </summary>
public enum MappingScope
{
    Rooted = 0,
    Set = 1
}

/// <summary>
/// Nested object mapping reference.
/// </summary>
public sealed record NestedMapping
{
    /// <summary>
    /// Creates a nested mapping.
    /// </summary>
    public NestedMapping(FieldPath source, FieldPath target, MappingId nestedMappingId)
    {
        Source = source;
        Target = target;
        NestedMappingId = Guard.RequireNotNull(nestedMappingId);
    }

    /// <summary>
    /// Source object path.
    /// </summary>
    public FieldPath Source { get; init; }

    /// <summary>
    /// Target object path.
    /// </summary>
    public FieldPath Target { get; init; }

    /// <summary>
    /// Nested mapping id.
    /// </summary>
    public MappingId NestedMappingId { get; init; }
}

/// <summary>
/// Collection mapping reference.
/// </summary>
public sealed record CollectionMapping
{
    /// <summary>
    /// Creates a collection mapping.
    /// </summary>
    public CollectionMapping(
        FieldPath sourceCollection,
        FieldPath targetCollection,
        MappingId itemMappingId
        )
    {
        SourceCollection = sourceCollection;
        TargetCollection = targetCollection;
        ItemMappingId = Guard.RequireNotNull(itemMappingId);
    }

    /// <summary>
    /// Source collection path.
    /// </summary>
    public FieldPath SourceCollection { get; init; }

    /// <summary>
    /// Target collection path.
    /// </summary>
    public FieldPath TargetCollection { get; init; }

    /// <summary>
    /// Mapping used per collection item.
    /// </summary>
    public MappingId ItemMappingId { get; init; }
}

/// <summary>
/// Mapping execution preference.
/// </summary>
public enum MappingExecutionPreference
{
    InMemory = 0,
    Materialized = 1,
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
    MappingDefinition(
        MappingId id,
        MappingName name,
        MappingKind kind,
        ShapeId? sourceShapeId,
        ShapeId targetShapeId,
        ImmutableArray<FieldAssignment> assignments = default,
        Expr? predicate = null,
        Expr? forEach = null,
        Expr? key = null,
        Expr? entity = null,
        MappingScope scope = MappingScope.Rooted,
        MappingDirection direction = MappingDirection.SourceToTarget,
        ImmutableArray<NestedMapping> nestedMappings = default,
        ImmutableArray<CollectionMapping> collectionMappings = default,
        MappingMetadata? metadata = null
        )
    {
        Id = Guard.RequireNotNull(id);
        Name = Guard.RequireNotNull(name);
        Kind = kind;
        SourceShapeId = sourceShapeId;
        TargetShapeId = targetShapeId;
        Assignments = assignments.IsDefault ? [] : assignments;
        Predicate = predicate;
        ForEach = forEach;
        Key = key;
        Entity = entity;
        Scope = scope;
        Direction = direction;
        NestedMappings = nestedMappings.IsDefault ? [] : nestedMappings;
        CollectionMappings = collectionMappings.IsDefault ? [] : collectionMappings;
        Metadata = metadata ?? MappingMetadata.Default;

        if (Kind == MappingKind.Relation)
        {
            if (Assignments.IsDefaultOrEmpty)
                throw new ArgumentException("Relation mapping requires at least one assignment.", nameof(assignments));
            return;
        }

        if (SourceShapeId is null)
            throw new ArgumentException("Object mapping requires a source shape id.", nameof(sourceShapeId));
    }

    /// <summary>
    /// Creates a relation mapping definition.
    /// </summary>
    public MappingDefinition(
        MappingId id,
        MappingName name,
        ShapeId targetShapeId,
        ImmutableArray<FieldAssignment> assignments,
        Expr? predicate = null,
        Expr? forEach = null,
        Expr? key = null,
        Expr? entity = null,
        MappingScope scope = MappingScope.Rooted,
        MappingMetadata? metadata = null
        )
        : this(
            id: id,
            name: name,
            kind: MappingKind.Relation,
            sourceShapeId: null,
            targetShapeId: targetShapeId,
            assignments: assignments,
            predicate: predicate,
            forEach: forEach,
            key: key,
            entity: entity,
            scope: scope,
            direction: MappingDirection.SourceToTarget,
            nestedMappings: [],
            collectionMappings: [],
            metadata: metadata)
    { }

    /// <summary>
    /// Mapping identifier.
    /// </summary>
    public MappingId Id { get; init; }

    /// <summary>
    /// Mapping name.
    /// </summary>
    public MappingName Name { get; init; }

    /// <summary>
    /// Mapping semantic kind.
    /// </summary>
    public MappingKind Kind { get; init; }

    /// <summary>
    /// Source shape id for object mappings.
    /// </summary>
    public ShapeId? SourceShapeId { get; init; }

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
    /// Mapping direction for object mappings.
    /// </summary>
    public MappingDirection Direction { get; init; }
    
    /// <summary>
    /// Nested object mappings.
    /// </summary>
    public ImmutableArray<NestedMapping> NestedMappings { get; init; }

    /// <summary>
    /// Collection item mappings.
    /// </summary>
    public ImmutableArray<CollectionMapping> CollectionMappings { get; init; }
    
    /// <summary>
    /// Mapping metadata.
    /// </summary>
    public MappingMetadata Metadata { get; init; }

    /// <summary>
    /// True when this is a relation mapping.
    /// </summary>
    [JsonIgnore]
    public bool IsRelationMapping => Kind == MappingKind.Relation;

    /// <summary>
    /// True when this is an object mapping.
    /// </summary>
    [JsonIgnore]
    public bool IsObjectMapping => Kind == MappingKind.Object;

    /// <summary>
    /// True when object mapping may be evaluated in both directions.
    /// </summary>
    [JsonIgnore]
    public bool IsBidirectional => IsObjectMapping && Direction is MappingDirection.Bidirectional;
}
