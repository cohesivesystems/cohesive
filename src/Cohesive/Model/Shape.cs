using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Semantic shape definition.
/// </summary>
public sealed record Shape
{
    readonly ImmutableDictionary<string, FieldDefinition> fieldsByName;

    /// <summary>
    /// Creates a shape definition.
    /// </summary>
    [JsonConstructor]
    public Shape(
        ShapeId id,
        ImmutableArray<FieldDefinition> fields,
        ImmutableArray<ShapeConstraint> constraints = default,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null,
        string? role = null
        )
    {
        Id = id;
        Fields = fields.IsDefault ? [] : fields;
        Constraints = constraints.IsDefault ? [] : constraints;
        Annotations = WithRole(AnnotationMap.Normalize(annotations), role);
        fieldsByName = BuildFieldLookup(Fields, nameof(fields));
    }

    /// <summary>
    /// Stable shape id.
    /// </summary>
    public ShapeId Id { get; init; }

    /// <summary>
    /// Shape field definitions.
    /// </summary>
    public ImmutableArray<FieldDefinition> Fields { get; }

    /// <summary>
    /// Shape-level constraints.
    /// </summary>
    public ImmutableArray<ShapeConstraint> Constraints { get; init; }

    /// <summary>
    /// Optional metadata annotations.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

    /// <summary>
    /// Compares this shape with another shape using explicit value semantics.
    /// </summary>
    public bool Equals(Shape? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Id == other.Id
               && Fields.SequenceEqual(other.Fields)
               && Constraints.SequenceEqual(other.Constraints)
               && AreStructuralAnnotationsEqual(Annotations, other.Annotations);
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(Shape?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Id);

        for (var i = 0; i < Fields.Length; i++)
            hash.Add(Fields[i]);
        for (var i = 0; i < Constraints.Length; i++)
            hash.Add(Constraints[i]);
        hash.Add(GetStructuralAnnotationsHashCode(Annotations));

        return hash.ToHashCode();
    }

    /// <summary>
    /// Standard shape role.
    /// </summary>
    public string? Role => TryGetStringAnnotation(Annotations, ShapeAnnotationKeys.Role);

    /// <summary>
    /// Logical entity type represented by this shape when declared through
    /// <see cref="ShapeAnnotationKeys.EntityType"/>.
    /// </summary>
    public EntityTypeName? EntityType
    {
        get
        {
            var value = TryGetStringAnnotation(Annotations, ShapeAnnotationKeys.EntityType);
            return string.IsNullOrWhiteSpace(value) ? null : new(value);
        }
    }

    /// <summary>
    /// Returns true when this shape has the requested role.
    /// </summary>
    public bool HasRole(string role) => string.Equals(Role, role, StringComparison.Ordinal);

    /// <summary>
    /// Returns a shape carrying the supplied logical entity type, preserving every other semantic member.
    /// </summary>
    /// <param name="entityType">Logical entity type represented by this shape.</param>
    /// <returns>This shape when the annotation already matches; otherwise an annotated copy.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entityType"/> is default, or this shape already declares a different entity type.
    /// </exception>
    public Shape WithEntityType(EntityTypeName entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType.Value))
            throw new ArgumentException("An entity type name is required.", nameof(entityType));
        var annotationKey = new AnnotationKey(ShapeAnnotationKeys.EntityType);
        if (Annotations.ContainsKey(annotationKey) && EntityType is null)
        {
            throw new ArgumentException(
                $"Shape '{Id.Value}' has an invalid '{ShapeAnnotationKeys.EntityType}' annotation.",
                nameof(entityType));
        }
        if (EntityType is { } existing && existing != entityType)
        {
            throw new ArgumentException(
                $"Shape '{Id.Value}' represents entity type '{existing.Value}', not '{entityType.Value}'.",
                nameof(entityType));
        }

        if (EntityType == entityType)
            return this;

        return this with
        {
            Annotations = Annotations.SetItem(
                annotationKey,
                AnnotationValue.FromString(entityType.Value))
        };
    }

    /// <summary>
    /// Looks up a field by canonical field name.
    /// </summary>
    public bool TryGetField(string fieldName, [NotNullWhen(true)] out FieldDefinition? field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        return fieldsByName.TryGetValue(fieldName, out field);
    }

    /// <summary>
    /// Gets a field by canonical field name, throwing if the field is not present.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The field was not found.</exception>
    public FieldDefinition GetField(string fieldName)
    {
        if (TryGetField(fieldName, out var field))
            return field;

        throw new KeyNotFoundException($"Shape '{Id.Value}' does not contain field '{fieldName}'.");
    }

    static ImmutableDictionary<AnnotationKey, AnnotationValue> WithRole(
        ImmutableDictionary<AnnotationKey, AnnotationValue> annotations,
        string? role
        )
    {
        return string.IsNullOrWhiteSpace(role)
            ? annotations
            : annotations.SetItem(new(ShapeAnnotationKeys.Role), AnnotationValue.FromString(role));
    }

    static bool AreStructuralAnnotationsEqual(
        ImmutableDictionary<AnnotationKey, AnnotationValue> left,
        ImmutableDictionary<AnnotationKey, AnnotationValue> right
        ) => ShapeValueEquality.AreAnnotationsEqual(
        WithoutRoleAnnotation(left),
        WithoutRoleAnnotation(right));

    static int GetStructuralAnnotationsHashCode(ImmutableDictionary<AnnotationKey, AnnotationValue> annotations) =>
        ShapeValueEquality.GetAnnotationsHashCode(WithoutRoleAnnotation(annotations));

    static ImmutableDictionary<AnnotationKey, AnnotationValue> WithoutRoleAnnotation(ImmutableDictionary<AnnotationKey, AnnotationValue> annotations) =>
        annotations.Remove(new AnnotationKey(ShapeAnnotationKeys.Role));

    static string? TryGetStringAnnotation(ImmutableDictionary<AnnotationKey, AnnotationValue> annotations, string key)
    {
        return annotations.TryGetValue(new(key), out var annotation)
               && annotation.Value is JsonValue value
               && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    static ImmutableDictionary<string, FieldDefinition> BuildFieldLookup(ImmutableArray<FieldDefinition> fields, string paramName)
    {
        var identityMap = ImmutableDictionary.CreateBuilder<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var field in fields)
            RegisterIdentity(field.Name.Value, field, identityMap, paramName);
        return [..identityMap];
        
        static void RegisterIdentity(
            string identity,
            FieldDefinition field,
            IDictionary<string, FieldDefinition> identityMap,
            string paramName
        )
        {
            if (identityMap.TryGetValue(identity, out var existing) && !ReferenceEquals(existing, field))
            {
                throw new ArgumentException(
                    message: $"Shape contains ambiguous field identity '{identity}' between '{existing.Name.Value}' and '{field.Name.Value}'.",
                    paramName: paramName
                );
            }

            identityMap[identity] = field;
        }
    }
}
