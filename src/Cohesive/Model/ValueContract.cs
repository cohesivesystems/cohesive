using System.Globalization;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Portable type, shape, cardinality, presence, and nullability constraints for one semantic value.
/// </summary>
public sealed record ValueContract
{
    /// <summary>Creates a portable semantic value contract.</summary>
    /// <param name="type">Known element or single-value type, or <see langword="null"/> when unknown.</param>
    /// <param name="shape">Known graph-qualified shape, or <see langword="null"/> for unshaped or unresolved values.</param>
    /// <param name="cardinality">Whether the value is single or many-valued.</param>
    /// <param name="presence">Whether the value is required to be present.</param>
    /// <param name="nullability">Whether an explicitly present value may be null.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cardinality"/>, <paramref name="presence"/>, or <paramref name="nullability"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="shape"/> is a default or incomplete qualified identity.
    /// </exception>
    [JsonConstructor]
    public ValueContract(
        TypeRef? type = null,
        QualifiedShapeId? shape = null,
        FieldCardinality cardinality = FieldCardinality.Single,
        FieldPresence presence = FieldPresence.Required,
        FieldNullability nullability = FieldNullability.NonNullable)
    {
        if (!Enum.IsDefined(cardinality))
            throw new ArgumentOutOfRangeException(nameof(cardinality), cardinality, "Unsupported value cardinality.");
        if (!Enum.IsDefined(presence))
            throw new ArgumentOutOfRangeException(nameof(presence), presence, "Unsupported value presence.");
        if (!Enum.IsDefined(nullability))
            throw new ArgumentOutOfRangeException(nameof(nullability), nullability, "Unsupported value nullability.");
        if (shape is { } candidateIdentity
            && (string.IsNullOrWhiteSpace(candidateIdentity.GraphId.Value)
                || string.IsNullOrWhiteSpace(candidateIdentity.ShapeId.Value)))
        {
            throw new ArgumentException(
                "A known value shape requires non-empty graph and shape identifiers.",
                nameof(shape));
        }
        Type = type;
        Shape = shape;
        Cardinality = cardinality;
        Presence = presence;
        Nullability = nullability;
    }

    /// <summary>Known element or single-value type.</summary>
    public TypeRef? Type { get; }

    /// <summary>Known graph-qualified shape.</summary>
    public QualifiedShapeId? Shape { get; }

    /// <summary>Whether the value is single or many-valued.</summary>
    public FieldCardinality Cardinality { get; }

    /// <summary>Whether the value is required to be present.</summary>
    public FieldPresence Presence { get; }

    /// <summary>Whether an explicitly present value may be null.</summary>
    public FieldNullability Nullability { get; }

    /// <summary>
    /// Gets the effective type, wrapping the element type in <see cref="ArrayTypeRef"/> for a many-valued contract.
    /// </summary>
    /// <returns>The effective type, or <see langword="null"/> when the type is unknown.</returns>
    public TypeRef? GetEffectiveType() => Type is null
        ? null
        : Cardinality == FieldCardinality.Many
            ? new ArrayTypeRef(Type)
            : Type;

    /// <summary>Tests whether a portable constant satisfies this value contract.</summary>
    /// <param name="value">Constant value to test.</param>
    /// <returns>
    /// <see langword="true"/> when presence, nullability, and every locally resolvable type constraint are satisfied;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool IsSatisfiedByConstant(ObservationValue value)
    {
        if (value.Kind == ObservationValueKind.Undefined)
            return Presence == FieldPresence.Optional;
        if (value.Kind == ObservationValueKind.Null)
            return Nullability == FieldNullability.Nullable;
        return GetEffectiveType() is not { } type
            || ValueContractSemantics.Evaluate(type, value) != ValueConstantCompatibility.Incompatible;
    }

    /// <summary>Creates a value contract from a semantic field definition.</summary>
    /// <param name="field">Field whose type and value guarantees are copied.</param>
    /// <returns>A value contract preserving the field type, cardinality, presence, and nullability.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="field"/> has no semantic type.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="field"/> has an unsupported cardinality, presence, or nullability value.
    /// </exception>
    public static ValueContract FromField(FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Type is null)
            throw new ArgumentException("A field value contract requires a semantic type.", nameof(field));
        return new(field.Type, cardinality: field.Cardinality, presence: field.Presence, nullability: field.Nullability);
    }

    /// <summary>Creates an object-value contract from a semantic shape.</summary>
    /// <param name="shape">Shape whose fields form the object type.</param>
    /// <param name="qualifiedShape">Optional graph-qualified identity for <paramref name="shape"/>.</param>
    /// <returns>An object-value contract derived from the shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="qualifiedShape"/> is incomplete or identifies a different local shape than
    /// <paramref name="shape"/>, or
    /// <paramref name="shape"/> contains a field with invalid identity or value-contract metadata.
    /// </exception>
    public static ValueContract FromShape(Shape shape, QualifiedShapeId? qualifiedShape = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ValidateShape(shape, nameof(shape));
        if (qualifiedShape is { } identity
            && (string.IsNullOrWhiteSpace(identity.GraphId.Value)
                || string.IsNullOrWhiteSpace(identity.ShapeId.Value)))
        {
            throw new ArgumentException(
                "A qualified shape requires non-empty graph and shape identifiers.",
                nameof(qualifiedShape));
        }
        if (qualifiedShape is { } qualifiedIdentity && qualifiedIdentity.ShapeId != shape.Id)
        {
            throw new ArgumentException(
                $"Qualified shape identity '{qualifiedIdentity}' does not identify shape '{shape.Id.Value}'.",
                nameof(qualifiedShape));
        }

        return new(
            type: new ObjectTypeRef(
            [
                .. shape.Fields.Select(static field => new ObjectFieldTypeDef(
                    name: field.Name.Value,
                    type: field.Type,
                    presence: field.Presence,
                    annotations: field.Annotations,
                    cardinality: field.Cardinality,
                    nullability: field.Nullability))
            ]),
            shape: qualifiedShape);
    }

    /// <summary>
    /// Compares portable contracts by their complete persisted semantic state.
    /// </summary>
    /// <param name="other">Contract to compare.</param>
    /// <returns><see langword="true"/> when the persisted semantic constraints are equal.</returns>
    public bool Equals(ValueContract? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return EqualityComparer<TypeRef?>.Default.Equals(Type, other.Type)
            && Nullable.Equals(Shape, other.Shape)
            && Cardinality == other.Cardinality
            && Presence == other.Presence
            && Nullability == other.Nullability;
    }

    /// <summary>Computes a hash code from persisted semantic state.</summary>
    /// <returns>A hash code aligned with <see cref="Equals(ValueContract?)"/>.</returns>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Type);
        hash.Add(Shape);
        hash.Add(Cardinality);
        hash.Add(Presence);
        hash.Add(Nullability);
        return hash.ToHashCode();
    }

    static void ValidateShape(Shape shape, string parameterName)
    {
        foreach (var field in shape.Fields)
        {
            if (field.Type is null)
            {
                throw new ArgumentException(
                    $"Shape '{shape.Id.Value}' field '{field.Name.Value}' has no semantic type.",
                    parameterName);
            }
            if (!Enum.IsDefined(field.Cardinality))
            {
                throw new ArgumentException(
                    $"Shape '{shape.Id.Value}' field '{field.Name.Value}' has unsupported cardinality '{((int)field.Cardinality).ToString(CultureInfo.InvariantCulture)}'.",
                    parameterName);
            }
            if (!Enum.IsDefined(field.Presence))
            {
                throw new ArgumentException(
                    $"Shape '{shape.Id.Value}' field '{field.Name.Value}' has unsupported presence '{((int)field.Presence).ToString(CultureInfo.InvariantCulture)}'.",
                    parameterName);
            }
            if (!Enum.IsDefined(field.Nullability))
            {
                throw new ArgumentException(
                    $"Shape '{shape.Id.Value}' field '{field.Name.Value}' has unsupported nullability '{((int)field.Nullability).ToString(CultureInfo.InvariantCulture)}'.",
                    parameterName);
            }
        }
    }
}
