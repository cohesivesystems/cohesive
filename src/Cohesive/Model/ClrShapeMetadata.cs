using System.Collections.Immutable;

namespace Cohesive.Model;

/// <summary>
/// Metadata contributed while deriving a shape graph from CLR types.
/// </summary>
public sealed record ClrShapeMetadata
{
    /// <summary>
    /// Empty metadata contribution.
    /// </summary>
    public static ClrShapeMetadata Empty { get; } = new();

    /// <summary>
    /// Optional stable root shape identifier override.
    /// </summary>
    public ShapeId? ShapeId { get; init; }

    /// <summary>
    /// Optional root shape role override.
    /// </summary>
    public string? ShapeRole { get; init; }

    /// <summary>
    /// Optional stable-named type identifier override.
    /// </summary>
    public TypeId? TypeId { get; init; }

    /// <summary>
    /// Optional field name override.
    /// </summary>
    public FieldName? FieldName { get; init; }

    /// <summary>
    /// Optional field type override.
    /// </summary>
    public TypeRef? TypeRef { get; init; }

    /// <summary>
    /// Additional named types contributed while deriving the current shape, type, or field.
    /// </summary>
    public ImmutableArray<TypeDefinition> NamedTypes { get; init; } = [];

    /// <summary>
    /// Constraints contributed to the shape, type, or field currently being built.
    /// </summary>
    public ImmutableArray<ShapeConstraint> Constraints { get; init; } = [];

    /// <summary>
    /// Annotation metadata contributed to the shape, type, or field currently being built.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; } =
        ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty;

    /// <summary>
    /// Merges a later metadata contribution over this contribution.
    /// </summary>
    public ClrShapeMetadata Merge(ClrShapeMetadata later)
    {
        ArgumentNullException.ThrowIfNull(later);

        return new()
        {
            ShapeId = later.ShapeId ?? ShapeId,
            ShapeRole = later.ShapeRole ?? ShapeRole,
            TypeId = later.TypeId ?? TypeId,
            FieldName = later.FieldName ?? FieldName,
            TypeRef = later.TypeRef ?? TypeRef,
            NamedTypes = [.. Normalize(NamedTypes), .. Normalize(later.NamedTypes)],
            Constraints = [.. Normalize(Constraints), .. Normalize(later.Constraints)],
            Annotations = AnnotationMap.Merge(
                AnnotationMap.Normalize(Annotations),
                AnnotationMap.Normalize(later.Annotations))
        };
    }

    static ImmutableArray<TypeDefinition> Normalize(ImmutableArray<TypeDefinition> namedTypes) =>
        namedTypes.IsDefault ? [] : namedTypes;

    static ImmutableArray<ShapeConstraint> Normalize(ImmutableArray<ShapeConstraint> constraints) =>
        constraints.IsDefault ? [] : constraints;
}
