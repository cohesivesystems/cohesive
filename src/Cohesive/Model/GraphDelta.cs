using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Semantic purpose of a graph delta.
/// </summary>
public enum GraphDeltaKind
{
    /// <summary>
    /// General graph delta with no stronger purpose declared.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Party/profile-specific deviation from a base graph.
    /// </summary>
    Overlay = 1,

    /// <summary>
    /// Standard/version evolution from one graph revision to another.
    /// </summary>
    Version = 2
}

/// <summary>
/// Explicit difference between two shape graphs.
/// </summary>
public sealed record GraphDelta
{
    /// <summary>
    /// Creates a graph delta.
    /// </summary>
    [JsonConstructor]
    public GraphDelta(
        string id,
        ImmutableArray<GraphDeltaOperation> operations,
        GraphDeltaKind kind = GraphDeltaKind.Unspecified,
        GraphId? sourceGraphId = null,
        GraphId? targetGraphId = null,
        string? sourceVersion = null,
        string? targetVersion = null,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Kind = kind;
        SourceGraphId = sourceGraphId;
        TargetGraphId = targetGraphId;
        SourceVersion = sourceVersion;
        TargetVersion = targetVersion;
        Operations = operations.IsDefault ? [] : operations;
        Annotations = AnnotationMap.Normalize(annotations);
    }

    /// <summary>
    /// Stable delta identifier.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Delta purpose.
    /// </summary>
    public GraphDeltaKind Kind { get; init; }

    /// <summary>
    /// Optional source graph identity.
    /// </summary>
    public GraphId? SourceGraphId { get; init; }

    /// <summary>
    /// Optional target graph identity.
    /// </summary>
    public GraphId? TargetGraphId { get; init; }

    /// <summary>
    /// Optional source version label.
    /// </summary>
    public string? SourceVersion { get; init; }

    /// <summary>
    /// Optional target version label.
    /// </summary>
    public string? TargetVersion { get; init; }

    /// <summary>
    /// Operations that transform the source graph.
    /// </summary>
    public ImmutableArray<GraphDeltaOperation> Operations { get; init; }

    /// <summary>
    /// Optional delta metadata.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }
}

/// <summary>
/// Anchored path to a field in either a root shape or a named structural type.
/// </summary>
public sealed record GraphFieldPath
{
    /// <summary>
    /// Creates an anchored graph field path.
    /// </summary>
    [JsonConstructor]
    public GraphFieldPath(ShapeId? shapeId, TypeId? typeId, FieldPath path)
    {
        if (shapeId.HasValue == typeId.HasValue)
            throw new ArgumentException("Graph field path requires exactly one shape or type anchor.");

        ShapeId = shapeId;
        TypeId = typeId;
        Path = path;
    }

    /// <summary>
    /// Root shape anchor.
    /// </summary>
    public ShapeId? ShapeId { get; init; }

    /// <summary>
    /// Named structural type anchor.
    /// </summary>
    public TypeId? TypeId { get; init; }

    /// <summary>
    /// Field path relative to the anchor.
    /// </summary>
    public FieldPath Path { get; init; }

    /// <summary>
    /// Creates a path anchored at a root shape.
    /// </summary>
    public static GraphFieldPath ForShape(ShapeId shapeId, FieldPath path) => new(shapeId, null, path);

    /// <summary>
    /// Creates a path anchored at a named structural type.
    /// </summary>
    public static GraphFieldPath ForType(TypeId typeId, FieldPath path) => new(null, typeId, path);

    /// <inheritdoc />
    public override string ToString()
    {
        var anchor = ShapeId?.Value ?? TypeId?.Value ?? "<unknown>";
        return $"{anchor}:{Path}";
    }
}

/// <summary>
/// Base type for graph delta operations.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$operation")]
[JsonDerivedType(typeof(AddShapeOperation), "addShape")]
[JsonDerivedType(typeof(RemoveShapeOperation), "removeShape")]
[JsonDerivedType(typeof(ReplaceShapeOperation), "replaceShape")]
[JsonDerivedType(typeof(SetGraphAnnotationOperation), "setGraphAnnotation")]
[JsonDerivedType(typeof(RemoveGraphAnnotationOperation), "removeGraphAnnotation")]
[JsonDerivedType(typeof(AddNamedTypeOperation), "addNamedType")]
[JsonDerivedType(typeof(RemoveNamedTypeOperation), "removeNamedType")]
[JsonDerivedType(typeof(ReplaceNamedTypeOperation), "replaceNamedType")]
[JsonDerivedType(typeof(AddShapeFieldOperation), "addShapeField")]
[JsonDerivedType(typeof(RemoveShapeFieldOperation), "removeShapeField")]
[JsonDerivedType(typeof(ReplaceShapeFieldOperation), "replaceShapeField")]
[JsonDerivedType(typeof(AddTypeFieldOperation), "addTypeField")]
[JsonDerivedType(typeof(RemoveTypeFieldOperation), "removeTypeField")]
[JsonDerivedType(typeof(ReplaceTypeFieldOperation), "replaceTypeField")]
[JsonDerivedType(typeof(SetFieldTypeOperation), "setFieldType")]
[JsonDerivedType(typeof(SetFieldPresenceOperation), "setFieldPresence")]
[JsonDerivedType(typeof(SetFieldCardinalityOperation), "setFieldCardinality")]
[JsonDerivedType(typeof(SetFieldNullabilityOperation), "setFieldNullability")]
[JsonDerivedType(typeof(AddFieldConstraintOperation), "addFieldConstraint")]
[JsonDerivedType(typeof(RemoveFieldConstraintOperation), "removeFieldConstraint")]
[JsonDerivedType(typeof(SetShapeAnnotationOperation), "setShapeAnnotation")]
[JsonDerivedType(typeof(RemoveShapeAnnotationOperation), "removeShapeAnnotation")]
[JsonDerivedType(typeof(SetTypeAnnotationOperation), "setTypeAnnotation")]
[JsonDerivedType(typeof(RemoveTypeAnnotationOperation), "removeTypeAnnotation")]
[JsonDerivedType(typeof(SetFieldAnnotationOperation), "setFieldAnnotation")]
[JsonDerivedType(typeof(RemoveFieldAnnotationOperation), "removeFieldAnnotation")]
[JsonDerivedType(typeof(RestrictAllowedValuesOperation), "restrictAllowedValues")]
[JsonDerivedType(typeof(ExtendAllowedValuesOperation), "extendAllowedValues")]
[JsonDerivedType(typeof(AddEnumValueOperation), "addEnumValue")]
[JsonDerivedType(typeof(RemoveEnumValueOperation), "removeEnumValue")]
public abstract record GraphDeltaOperation(string? Note = null)
{
    /// <summary>
    /// Optional human-readable explanation for the operation.
    /// </summary>
    public string? Note { get; init; } = Note;
}

/// <summary>
/// Adds a root shape definition to a graph.
/// </summary>
public sealed record AddShapeOperation(Shape Shape, int? Ordinal = null, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a root shape definition from a graph.
/// </summary>
public sealed record RemoveShapeOperation(ShapeId ShapeId, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Replaces a root shape definition.
/// </summary>
public sealed record ReplaceShapeOperation(Shape Shape, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Sets or replaces a graph-level annotation.
/// </summary>
public sealed record SetGraphAnnotationOperation(AnnotationKey Key, AnnotationValue Value, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a graph-level annotation.
/// </summary>
public sealed record RemoveGraphAnnotationOperation(AnnotationKey Key, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Adds a named type definition to a graph.
/// </summary>
public sealed record AddNamedTypeOperation(TypeDefinition Type, int? Ordinal = null, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a named type definition from a graph.
/// </summary>
public sealed record RemoveNamedTypeOperation(TypeId TypeId, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Replaces a named type definition.
/// </summary>
public sealed record ReplaceNamedTypeOperation(TypeDefinition Type, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Adds a direct field to a root shape.
/// </summary>
/// <param name="ShapeId">The shape id to which the field will be added.</param>
/// <param name="Field">The field definition.</param>
/// <param name="Ordinal">Optional ordinal position for the field.</param>
/// <param name="Note">Optional human-readable explanation for the operation.</param>
public sealed record AddShapeFieldOperation(ShapeId ShapeId, FieldDefinition Field, int? Ordinal = null, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a direct field from a root shape.
/// </summary>
/// <param name="ShapeId">The shape id from which the field will be removed.</param>
/// <param name="FieldName">The field name to remove.</param>
/// <param name="ReplacementPath">Optional replacement path for the field.</param>
/// <param name="Note">Optional human-readable explanation for the operation.</param>
public sealed record RemoveShapeFieldOperation(ShapeId ShapeId, FieldName FieldName, string? ReplacementPath = null, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Replaces a direct field on a root shape.
/// </summary>
public sealed record ReplaceShapeFieldOperation(ShapeId ShapeId, FieldDefinition Field, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Adds a direct field to a named structural type.
/// </summary>
public sealed record AddTypeFieldOperation(TypeId TypeId, StructuralField Field, int? Ordinal = null, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a direct field from a named structural type.
/// </summary>
public sealed record RemoveTypeFieldOperation(TypeId TypeId, FieldName FieldName, string? ReplacementPath = null, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Replaces a direct field on a named structural type.
/// </summary>
public sealed record ReplaceTypeFieldOperation(TypeId TypeId, StructuralField Field, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Changes the type reference for an anchored field.
/// </summary>
public sealed record SetFieldTypeOperation(GraphFieldPath Target, TypeRef Type, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Changes the required/optional presence for an anchored field.
/// </summary>
public sealed record SetFieldPresenceOperation(GraphFieldPath Target, FieldPresence Presence, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Changes the single/many cardinality for an anchored field.
/// </summary>
public sealed record SetFieldCardinalityOperation(GraphFieldPath Target, FieldCardinality Cardinality, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Changes the nullability for an anchored field.
/// </summary>
public sealed record SetFieldNullabilityOperation(GraphFieldPath Target, FieldNullability Nullability, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Adds a field-level constraint.
/// </summary>
public sealed record AddFieldConstraintOperation(GraphFieldPath Target, ShapeConstraint Constraint, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a field-level constraint.
/// </summary>
public sealed record RemoveFieldConstraintOperation(GraphFieldPath Target, ShapeConstraint Constraint, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Sets or replaces a shape-level annotation.
/// </summary>
public sealed record SetShapeAnnotationOperation(ShapeId ShapeId, AnnotationKey Key, AnnotationValue Value, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a shape-level annotation.
/// </summary>
public sealed record RemoveShapeAnnotationOperation(ShapeId ShapeId, AnnotationKey Key, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Sets or replaces a named type annotation.
/// </summary>
public sealed record SetTypeAnnotationOperation(TypeId TypeId, AnnotationKey Key, AnnotationValue Value, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a named type annotation.
/// </summary>
public sealed record RemoveTypeAnnotationOperation(TypeId TypeId, AnnotationKey Key, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Sets or replaces a field annotation.
/// </summary>
public sealed record SetFieldAnnotationOperation(GraphFieldPath Target, AnnotationKey Key, AnnotationValue Value, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a field annotation.
/// </summary>
public sealed record RemoveFieldAnnotationOperation(GraphFieldPath Target, AnnotationKey Key, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Narrows the allowed values for an anchored field.
/// </summary>
public sealed record RestrictAllowedValuesOperation(GraphFieldPath Target, ImmutableArray<string> Values, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Extends the allowed values for an anchored field.
/// </summary>
public sealed record ExtendAllowedValuesOperation(GraphFieldPath Target, ImmutableArray<string> Values, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Adds a value to a named enum type.
/// </summary>
public sealed record AddEnumValueOperation(TypeId TypeId, EnumValue Value, string? Note = null) : GraphDeltaOperation(Note);

/// <summary>
/// Removes a value from a named enum type.
/// </summary>
public sealed record RemoveEnumValueOperation(TypeId TypeId, string ValueName, string? Note = null) : GraphDeltaOperation(Note);
