using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Base type for domain type references.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(NamedTypeRef), "named")]
[JsonDerivedType(typeof(OpaqueRuntimeTypeRef), "opaque")]
[JsonDerivedType(typeof(ScalarTypeRef), "scalar")]
[JsonDerivedType(typeof(EnumTypeRef), "enum")]
[JsonDerivedType(typeof(EntityReferenceTypeRef), "entityRef")]
[JsonDerivedType(typeof(ArrayTypeRef), "array")]
[JsonDerivedType(typeof(ObjectTypeRef), "object")]
[JsonDerivedType(typeof(QuantityTypeRef), "quantity")]
[JsonDerivedType(typeof(JsonTypeRef), "json")]
[Union]
public abstract partial record TypeRef;

/// <summary>
/// Named type reference via <see cref="TypeId"/>.
/// </summary>
/// <remarks>This is an identity-bearing version of <see cref="ObjectTypeRef"/> resolved via <see cref="ShapeGraph"/>.</remarks>
public sealed record NamedTypeRef : TypeRef
{
    /// <summary>
    /// Creates a named type reference.
    /// </summary>
    [JsonConstructor]
    public NamedTypeRef(TypeId typeId)
    {
        TypeId = typeId;
    }

    /// <summary>
    /// Stable type id.
    /// </summary>
    public TypeId TypeId { get; init; }

}

/// <summary>
/// Opaque runtime type reference used when semantics are known but portable typing is unavailable.
/// </summary>
public sealed record OpaqueRuntimeTypeRef : TypeRef
{
    /// <summary>
    /// Creates an opaque runtime type reference.
    /// </summary>
    [JsonConstructor]
    public OpaqueRuntimeTypeRef(string runtimeType, TypeInferenceDiagnostic? inferenceDiagnostic = null)
    {
        RuntimeType = Guard.RequireNotNullOrWhiteSpace(runtimeType);
        InferenceDiagnostic = inferenceDiagnostic;
    }

    /// <summary>
    /// Runtime type identity.
    /// </summary>
    public string RuntimeType { get; init; }

    /// <summary>
    /// Optional diagnostic explaining why CLR type inference emitted an opaque reference.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TypeInferenceDiagnostic? InferenceDiagnostic { get; init; }

    /// <summary>
    /// Compares opaque references by semantic runtime type identity only.
    /// </summary>
    public bool Equals(OpaqueRuntimeTypeRef? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return string.Equals(RuntimeType, other.RuntimeType, StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(OpaqueRuntimeTypeRef?)"/>.
    /// </summary>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(RuntimeType);
}

/// <summary>
/// Diagnostic captured when CLR type inference falls back to an opaque runtime type reference.
/// </summary>
public sealed record TypeInferenceDiagnostic
{
    /// <summary>
    /// Creates a type inference diagnostic.
    /// </summary>
    [JsonConstructor]
    public TypeInferenceDiagnostic(string reason, string? message = null)
    {
        Reason = Guard.RequireNotNullOrWhiteSpace(reason);
        Message = string.IsNullOrWhiteSpace(message) ? null : message;
    }

    /// <summary>
    /// Stable machine-readable fallback reason.
    /// </summary>
    public string Reason { get; init; }

    /// <summary>
    /// Optional human-readable explanation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

/// <summary>
/// Standard reasons emitted by CLR type inference when it falls back to opacity.
/// </summary>
public static class TypeInferenceDiagnosticReasons
{
    /// <summary>
    /// The type is <see cref="object"/> and carries no structural semantic information.
    /// </summary>
    public const string ObjectRuntimeType = "objectRuntimeType";

    /// <summary>
    /// The type recursively references a CLR type already being mapped.
    /// </summary>
    public const string RecursiveType = "recursiveType";

    /// <summary>
    /// The type uses polymorphic JSON metadata that cannot be represented as a structural type reference.
    /// </summary>
    public const string PolymorphicType = "polymorphicType";

    /// <summary>
    /// The type is abstract or an interface without a dedicated semantic mapping.
    /// </summary>
    public const string AbstractType = "abstractType";

    /// <summary>
    /// The type is a dictionary and the current type reference model has no map shape.
    /// </summary>
    public const string UnsupportedDictionary = "unsupportedDictionary";

    /// <summary>
    /// The type uses a structured quantity representation that cannot be mapped to a supported scalar.
    /// </summary>
    public const string UnsupportedQuantityRepresentation = "unsupportedQuantityRepresentation";

    /// <summary>
    /// The type declares the single-value wrapper JSON converter but does not expose a valid value property.
    /// </summary>
    public const string InvalidSingleValueWrapper = "invalidSingleValueWrapper";

    /// <summary>
    /// The enum declares a custom JSON converter whose complete wire-member set cannot be inferred safely.
    /// </summary>
    public const string UnsupportedEnumConverter = "unsupportedEnumConverter";

    /// <summary>Multiple enum members map to the same canonical JSON string.</summary>
    public const string AmbiguousSerializedEnumMember = "ambiguousSerializedEnumMember";

    /// <summary>
    /// The type has no readable public instance properties.
    /// </summary>
    public const string NoReadableProperties = "noReadableProperties";

    /// <summary>Multiple readable properties map to the same serialized field name.</summary>
    public const string AmbiguousSerializedProperty = "ambiguousSerializedProperty";
}

/// <summary>
/// JSON-compatible value type reference.
/// </summary>
public sealed record JsonTypeRef : TypeRef
{
    /// <summary>
    /// Creates a JSON-compatible type reference.
    /// </summary>
    [JsonConstructor]
    public JsonTypeRef(JsonTypeKind kind = JsonTypeKind.Any)
    {
        Kind = kind;
    }

    /// <summary>
    /// JSON value shape accepted by this type reference.
    /// </summary>
    public JsonTypeKind Kind { get; init; }
}

/// <summary>
/// JSON value shapes representable by <see cref="JsonTypeRef"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JsonTypeKind
{
    /// <summary>
    /// Any non-null JSON value.
    /// </summary>
    Any = 0,

    /// <summary>
    /// JSON object value.
    /// </summary>
    Object = 1,

    /// <summary>
    /// JSON array value.
    /// </summary>
    Array = 2,

    /// <summary>
    /// JSON string value.
    /// </summary>
    String = 3,

    /// <summary>
    /// JSON numeric value.
    /// </summary>
    Number = 4,

    /// <summary>
    /// JSON boolean value.
    /// </summary>
    Boolean = 5
}

/// <summary>
/// Built-in scalar type kinds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScalarTypeKind
{
    /// <summary>Represents the bool option.</summary>
    Bool = 0,
    /// <summary>Represents the int32 option.</summary>
    Int32 = 1,
    /// <summary>Represents the int64 option.</summary>
    Int64 = 2,
    /// <summary>Represents the decimal option.</summary>
    Decimal = 3,
    /// <summary>Represents the string option.</summary>
    String = 4,
    /// <summary>Represents the guid option.</summary>
    Guid = 5,
    /// <summary>Represents the date option.</summary>
    Date = 6,
    /// <summary>Represents the date time option.</summary>
    DateTime = 7,
    /// <summary>Represents the instant option.</summary>
    Instant = 8,
    /// <summary>Represents the bytes option.</summary>
    Bytes = 9
}

/// <summary>
/// Scalar type reference.
/// </summary>
public sealed record ScalarTypeRef : TypeRef
{
    /// <summary>
    /// Creates a scalar type reference.
    /// </summary>
    [JsonConstructor]
    public ScalarTypeRef(ScalarTypeKind kind, PrimitiveFormat format = PrimitiveFormat.None)
    {
        Kind = kind;
        Format = format;
    }

    /// <summary>
    /// Scalar type kind.
    /// </summary>
    public ScalarTypeKind Kind { get; init; }

    /// <summary>
    /// Optional scalar format metadata.
    /// </summary>
    public PrimitiveFormat Format { get; init; }
}

/// <summary>
/// Enum type definition with allowed members.
/// </summary>
public sealed record EnumTypeRef : TypeRef
{
    /// <summary>
    /// Creates an enum type definition.
    /// </summary>
    [JsonConstructor]
    public EnumTypeRef(string name, ImmutableArray<string> members)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(value: name);
        Members = members.IsDefault ? ImmutableArray<string>.Empty : members;
        if (Members.IsDefaultOrEmpty)
            throw new ArgumentException(message: "Enum type requires at least one member.");
    }

    /// <summary>
    /// Enum type name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Allowed enum member names.
    /// </summary>
    public ImmutableArray<string> Members { get; init; }

    /// <summary>
    /// Compares enum type references using value semantics for members.
    /// </summary>
    public bool Equals(EnumTypeRef? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Name == other.Name
               && Members.SequenceEqual(other.Members);
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(EnumTypeRef?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Name, StringComparer.Ordinal);
        foreach (var member in Members)
            hash.Add(member, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Reference to another entity type by name.
/// </summary>
public sealed record EntityReferenceTypeRef : TypeRef
{
    /// <summary>Creates a reference to a logical entity type.</summary>
    /// <param name="entity">Stable non-empty target entity type name.</param>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is default.</exception>
    [JsonConstructor]
    public EntityReferenceTypeRef(EntityTypeName entity)
    {
        if (string.IsNullOrWhiteSpace(entity.Value))
            throw new ArgumentException("A referenced entity type name is required.", nameof(entity));

        Entity = entity;
    }

    /// <summary>Stable target entity type name.</summary>
    public EntityTypeName Entity { get; init; }
}

/// <summary>
/// Array type reference.
/// </summary>
public sealed record ArrayTypeRef(TypeRef ElementType) : TypeRef;

/// <summary>
/// Inline object type composed of named fields.
/// </summary>
public sealed record ObjectTypeRef : TypeRef
{
    /// <summary>
    /// Creates an inline object type.
    /// </summary>
    /// <param name="fields">The inline object's fields, or an empty collection for an empty object type.</param>
    [JsonConstructor]
    public ObjectTypeRef(ImmutableArray<ObjectFieldTypeDef> fields)
    {
        Fields = fields.IsDefault ? ImmutableArray<ObjectFieldTypeDef>.Empty : fields;
    }

    /// <summary>
    /// Inline field definitions for the object.
    /// </summary>
    public ImmutableArray<ObjectFieldTypeDef> Fields { get; init; }

    /// <summary>
    /// Compares object type references using value semantics for fields.
    /// </summary>
    public bool Equals(ObjectTypeRef? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Fields.SequenceEqual(other.Fields);
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(ObjectTypeRef?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (var field in Fields)
            hash.Add(field);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Named field within an inline object type.
/// </summary>
public sealed record ObjectFieldTypeDef
{
    /// <summary>
    /// Creates an inline object field definition.
    /// </summary>
    /// <param name="name">Stable field name.</param>
    /// <param name="type">Element or single-value semantic type.</param>
    /// <param name="cardinality">Whether the field is single or many-valued.</param>
    /// <param name="presence">Whether the field must be present.</param>
    /// <param name="nullability">Whether an explicitly present field value may be null.</param>
    /// <param name="annotations">Optional semantic field annotations.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="type"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains only whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="presence"/>, <paramref name="cardinality"/>, or <paramref name="nullability"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ObjectFieldTypeDef(
        string name,
        TypeRef type,
        FieldCardinality cardinality = FieldCardinality.Single,
        FieldPresence presence = FieldPresence.Required,
        FieldNullability nullability = FieldNullability.NonNullable,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        if (!Enum.IsDefined(cardinality))
            throw new ArgumentOutOfRangeException(nameof(cardinality), cardinality, "Unsupported field cardinality.");
        if (!Enum.IsDefined(presence))
            throw new ArgumentOutOfRangeException(nameof(presence), presence, "Unsupported field presence.");
        if (!Enum.IsDefined(nullability))
            throw new ArgumentOutOfRangeException(nameof(nullability), nullability, "Unsupported field nullability.");

        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Type = Guard.RequireNotNull(type);
        Cardinality = cardinality;
        Presence = presence;
        Nullability = nullability;
        Annotations = AnnotationMap.Normalize(annotations);
    }

    /// <summary>
    /// Field name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Field type.
    /// </summary>
    public TypeRef Type { get; init; }

    /// <summary>
    /// Whether the field is single or many-valued.
    /// </summary>
    public FieldCardinality Cardinality { get; init; }

    /// <summary>
    /// Required/optional indicator.
    /// </summary>
    public FieldPresence Presence { get; init; }

    /// <summary>
    /// Whether an explicitly present field value may be null.
    /// </summary>
    public FieldNullability Nullability { get; init; }

    /// <summary>
    /// Optional metadata annotations for inline object fields.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

    /// <summary>
    /// Compares object field definitions using value semantics for annotations.
    /// </summary>
    public bool Equals(ObjectFieldTypeDef? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Name == other.Name
               && EqualityComparer<TypeRef>.Default.Equals(Type, other.Type)
               && Cardinality == other.Cardinality
               && Presence == other.Presence
               && Nullability == other.Nullability
               && ShapeValueEquality.AreAnnotationsEqual(Annotations, other.Annotations);
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(ObjectFieldTypeDef?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(Type);
        hash.Add((int)Cardinality);
        hash.Add((int)Presence);
        hash.Add((int)Nullability);
        hash.Add(ShapeValueEquality.GetAnnotationsHashCode(Annotations));
        return hash.ToHashCode();
    }
}

/// <summary>
/// Structured quantity type reference (for example, Distance backed by Decimal base values).
/// </summary>
public sealed record QuantityTypeRef : TypeRef
{
    /// <summary>
    /// Creates a structured quantity type definition.
    /// </summary>
    [JsonConstructor]
    public QuantityTypeRef(string quantity, ScalarTypeKind baseKind = ScalarTypeKind.Decimal)
    {
        Quantity = Guard.RequireNotNullOrWhiteSpace(value: quantity);
        BaseKind = baseKind;
    }

    /// <summary>
    /// Quantity type name.
    /// </summary>
    public string Quantity { get; init; }

    /// <summary>
    /// Scalar kind used for serialized canonical base values.
    /// </summary>
    public ScalarTypeKind BaseKind { get; init; }
}
