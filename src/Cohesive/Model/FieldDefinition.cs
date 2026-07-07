using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Field definition in a shape.
/// </summary>
public sealed record FieldDefinition
{
    /// <summary>
    /// Creates a field definition.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    [JsonConstructor]
    public FieldDefinition(
        FieldName name,
        TypeRef type,
        FieldCardinality cardinality = FieldCardinality.Single,
        FieldPresence presence = FieldPresence.Required,
        FieldNullability nullability = FieldNullability.NonNullable,
        FieldRole role = FieldRole.Data,
        FieldMutability mutability = FieldMutability.Mutable,
        ComputeDefinition? compute = null,
        ImmutableArray<ShapeConstraint> constraints = default,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        Name = name;
        Type = Guard.RequireNotNull(type);
        Cardinality = cardinality;
        Presence = presence;
        Nullability = nullability;
        Role = role;
        Mutability = mutability;
        Compute = compute;
        Constraints = constraints.IsDefault ? [] : constraints;
        Annotations = AnnotationMap.Normalize(annotations);

        if (role == FieldRole.Computed && Mutability != FieldMutability.Computed)
        {
            throw new ArgumentException(
                message: "Computed fields require computed mutability.",
                paramName: nameof(mutability)
                );
        }

        if (Mutability == FieldMutability.Computed && role != FieldRole.Computed)
        {
            throw new ArgumentException(
                message: "Computed mutability requires computed role.",
                paramName: nameof(role)
                );
        }

        if (Mutability == FieldMutability.Computed && Compute is null)
        {
            throw new ArgumentException(
                message: "Computed fields require a compute definition.",
                paramName: nameof(compute)
                );
        }

        if (Mutability != FieldMutability.Computed && Compute is not null)
        {
            throw new ArgumentException(
                message: "Only computed mutability fields can declare a compute definition.",
                paramName: nameof(compute)
                );
        }
    }

    /// <summary>
    /// Canonical field name and identity.
    /// </summary>
    public FieldName Name { get; init; }

    /// <summary>
    /// Field type.
    /// </summary>
    public TypeRef Type { get; init; }

    /// <summary>
    /// Field cardinality.
    /// </summary>
    public FieldCardinality Cardinality { get; init; }

    /// <summary>
    /// Presence requirement (required/optional).
    /// </summary>
    public FieldPresence Presence { get; init; }

    /// <summary>
    /// Nullability requirement.
    /// </summary>
    public FieldRole Role { get; init; }
    
    /// <summary>
    /// Nullability requirement.
    /// </summary>
    public FieldNullability Nullability { get; init; }

    /// <summary>
    /// Field mutability semantics.
    /// </summary>
    public FieldMutability Mutability { get; init; }

    /// <summary>
    /// Optional compute metadata for computed fields.
    /// </summary>
    public ComputeDefinition? Compute { get; init; }

    /// <summary>
    /// Declarative constraints.
    /// </summary>
    public ImmutableArray<ShapeConstraint> Constraints { get; init; }

    /// <summary>
    /// Optional metadata annotations.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

    /// <summary>
    /// Compares this field with another field using explicit value semantics.
    /// </summary>
    public bool Equals(FieldDefinition? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Name == other.Name
               && Type == other.Type
               && Cardinality == other.Cardinality
               && Presence == other.Presence
               && Role == other.Role
               && Nullability == other.Nullability
               && Mutability == other.Mutability
               && Compute == other.Compute
               && Constraints.SequenceEqual(other.Constraints)
               && ShapeValueEquality.AreAnnotationsEqual(Annotations, other.Annotations);
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(FieldDefinition?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Name);
        hash.Add(Type);
        hash.Add((int)Cardinality);
        hash.Add((int)Presence);
        hash.Add((int)Role);
        hash.Add((int)Nullability);
        hash.Add((int)Mutability);
        hash.Add(Compute);

        foreach (var constraint in Constraints)
            hash.Add(constraint);

        hash.Add(ShapeValueEquality.GetAnnotationsHashCode(Annotations));

        return hash.ToHashCode();
    }

    /// <summary>
    /// Returns true when <paramref name="fieldName"/> matches the canonical field name.
    /// </summary>
    public bool MatchesName(string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        return string.Equals(Name.Value, fieldName, StringComparison.Ordinal);
    }
}
