using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Type definition.
/// </summary>
/// <remarks>This is the named schema type system for the shape graph referred to by <see cref="NamedTypeRef"/>.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$typeDef")]
[JsonDerivedType(typeof(Structural), "structural")]
[JsonDerivedType(typeof(Enum), "enum")]
[JsonDerivedType(typeof(Union), "union")]
[Union]
public abstract partial record TypeDefinition
{
    /// <summary>
    /// Creates a named type definition.
    /// </summary>
    protected TypeDefinition(TypeId id, string? name = null)
    {
        Id = id;
        Name = NormalizeName(name) ?? InferName(id);
    }

    /// <summary>
    /// Stable type identifier.
    /// </summary>
    public TypeId Id { get; init; }

    /// <summary>
    /// Short display name for the type.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Structural named type definition.
    /// </summary>
    public sealed record Structural : TypeDefinition
    {
        readonly ImmutableDictionary<string, StructuralField> fieldsByName;

        /// <summary>
        /// Creates a structural type definition.
        /// </summary>
        /// <param name="id">Stable named-type identity.</param>
        /// <param name="fields">Canonical structural fields.</param>
        /// <param name="constraints">Type-level semantic constraints.</param>
        /// <param name="annotations">Optional structural-type annotations.</param>
        /// <param name="name">Optional display name.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="fields"/> contains a null entry, an empty field identity, or an ambiguous field identity.
        /// </exception>
        [JsonConstructor]
        public Structural(
            TypeId id,
            ImmutableArray<StructuralField> fields,
            ImmutableArray<ShapeConstraint> constraints = default,
            ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null,
            string? name = null
            ) : base(id, name)
        {
            Fields = fields.IsDefault ? [] : fields;
            Constraints = constraints.IsDefault ? [] : constraints;
            Annotations = AnnotationMap.Normalize(annotations);
            fieldsByName = BuildFieldLookup(Fields, nameof(fields));
        }

        /// <summary>
        /// Structural fields.
        /// </summary>
        public ImmutableArray<StructuralField> Fields { get; }

        /// <summary>
        /// Type constraints.
        /// </summary>
        public ImmutableArray<ShapeConstraint> Constraints { get; init; }

        /// <summary>
        /// Optional metadata annotations.
        /// </summary>
        public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

        /// <summary>
        /// Looks up a structural field by canonical field identity.
        /// </summary>
        public bool TryGetField(string fieldName, [NotNullWhen(true)] out StructuralField? field)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
            return fieldsByName.TryGetValue(fieldName, out field);
        }

        /// <summary>
        /// Gets a structural field by canonical field identity, throwing if the field is not present.
        /// </summary>
        /// <exception cref="KeyNotFoundException">The given field was not found.</exception>
        public StructuralField GetField(string fieldName)
        {
            if (TryGetField(fieldName, out var field))
                return field;

            throw new KeyNotFoundException($"Structural type '{Id.Value}' does not contain field '{fieldName}'.");
        }

        /// <summary>
        /// Compares structural type definitions using value semantics for fields, constraints, and annotations.
        /// </summary>
        public bool Equals(Structural? other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (other is null)
                return false;

            return Id == other.Id
                   && Name == other.Name
                   && Fields.SequenceEqual(other.Fields)
                   && Constraints.SequenceEqual(other.Constraints)
                   && ShapeValueEquality.AreAnnotationsEqual(Annotations, other.Annotations);
        }

        /// <summary>
        /// Computes a hash code aligned with equality.
        /// </summary>
        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Id);
            hash.Add(Name);
            foreach (var field in Fields)
                hash.Add(field);
            foreach (var constraint in Constraints)
                hash.Add(constraint);
            hash.Add(ShapeValueEquality.GetAnnotationsHashCode(Annotations));
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Enum named type definition.
    /// </summary>
    public sealed record Enum : TypeDefinition
    {
        /// <summary>
        /// Creates an enum type definition.
        /// </summary>
        [JsonConstructor]
        public Enum(
            TypeId id,
            PrimitiveType underlying,
            ImmutableArray<EnumValue> values,
            ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null,
            string? name = null
            ) : base(id, name)
        {
            Underlying = underlying;
            Values = values.IsDefault ? [] : values;
            Annotations = AnnotationMap.Normalize(annotations);

            if (Values.IsDefaultOrEmpty)
                throw new ArgumentException(message: "Enum type requires at least one value.", paramName: nameof(values));

            var duplicateNames = Values
                .GroupBy(x => x.Name, StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateNames is not null)
            {
                throw new ArgumentException(
                    message: $"Enum type '{id.Value}' contains duplicate enum value '{duplicateNames.Key}'.",
                    paramName: nameof(values));
            }
        }

        /// <summary>
        /// Underlying primitive type.
        /// </summary>
        public PrimitiveType Underlying { get; init; }

        /// <summary>
        /// Enum values.
        /// </summary>
        public ImmutableArray<EnumValue> Values { get; init; }

        /// <summary>
        /// Optional metadata annotations.
        /// </summary>
        public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

        /// <summary>
        /// Compares enum type definitions using value semantics for values and annotations.
        /// </summary>
        public bool Equals(Enum? other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (other is null)
                return false;

            return Id == other.Id
                   && Name == other.Name
                   && Underlying == other.Underlying
                   && Values.SequenceEqual(other.Values)
                   && ShapeValueEquality.AreAnnotationsEqual(Annotations, other.Annotations);
        }

        /// <summary>
        /// Computes a hash code aligned with <see cref="Equals(Enum?)"/>.
        /// </summary>
        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Id);
            hash.Add(Name);
            hash.Add(Underlying);
            foreach (var value in Values)
                hash.Add(value);
            hash.Add(ShapeValueEquality.GetAnnotationsHashCode(Annotations));
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Discriminated union named type definition.
    /// </summary>
    public sealed record Union : TypeDefinition
    {
        /// <summary>
        /// Creates a union type definition.
        /// </summary>
        [JsonConstructor]
        public Union(
            TypeId id,
            UnionDiscriminator discriminator,
            ImmutableArray<UnionCase> cases,
            ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null,
            string? name = null
            ) : base(id, name)
        {
            Discriminator = discriminator;
            Cases = cases.IsDefault ? [] : cases;
            Annotations = AnnotationMap.Normalize(annotations);

            if (Cases.IsDefaultOrEmpty)
                throw new ArgumentException(message: "Union type requires at least one case.", paramName: nameof(cases));

            var duplicateCaseByName = Cases.TryGetDuplicateByKey(x => x.Name, StringComparer.Ordinal);
            if (duplicateCaseByName is not null)
            {
                throw new ArgumentException(
                    message: $"Union type '{id.Value}' contains duplicate case '{duplicateCaseByName.Name}'.",
                    paramName: nameof(cases)
                    );
            }
        }

        /// <summary>
        /// Discriminator metadata.
        /// </summary>
        public UnionDiscriminator Discriminator { get; init; }

        /// <summary>
        /// Union cases.
        /// </summary>
        public ImmutableArray<UnionCase> Cases { get; init; }

        /// <summary>
        /// Optional metadata annotations.
        /// </summary>
        public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

        /// <summary>
        /// Compares union type definitions using value semantics for cases and annotations.
        /// </summary>
        public bool Equals(Union? other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (other is null)
                return false;

            return Id == other.Id
                   && Name == other.Name
                   && Discriminator == other.Discriminator
                   && Cases.SequenceEqual(other.Cases)
                   && ShapeValueEquality.AreAnnotationsEqual(Annotations, other.Annotations);
        }

        /// <summary>
        /// Computes a hash code aligned with <see cref="Equals(Union?)"/>.
        /// </summary>
        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Id);
            hash.Add(Name);
            hash.Add(Discriminator);
            foreach (var unionCase in Cases)
                hash.Add(unionCase);
            hash.Add(ShapeValueEquality.GetAnnotationsHashCode(Annotations));
            return hash.ToHashCode();
        }
    }

    static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    static string InferName(TypeId id)
    {
        var value = id.Value;
        const string clrTypePrefix = "clr:type:";
        if (value.StartsWith(clrTypePrefix, StringComparison.Ordinal))
            value = value[clrTypePrefix.Length..];

        var genericStart = value.IndexOf('<');
        if (genericStart >= 0)
            value = value[..genericStart];

        var separator = value.LastIndexOfAny(['.', '+', ':', '/', '#']);
        if (separator >= 0 && separator + 1 < value.Length)
            value = value[(separator + 1)..];

        return string.IsNullOrWhiteSpace(value) ? id.Value : value;
    }

    static ImmutableDictionary<string, StructuralField> BuildFieldLookup(ImmutableArray<StructuralField> fields, string paramName)
    {
        var identityMap = ImmutableDictionary.CreateBuilder<string, StructuralField>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field is null)
                throw new ArgumentException("Structural fields cannot contain null entries.", paramName);
            if (string.IsNullOrWhiteSpace(field.Name.Value))
                throw new ArgumentException("Structural fields must have non-empty identities.", paramName);
            RegisterIdentity(field.Name.Value, field, identityMap, paramName);
        }

        return [..identityMap];
    }

    static void RegisterIdentity(
        string identity,
        StructuralField field,
        IDictionary<string, StructuralField> fieldsById,
        string paramName
        )
    {
        if (fieldsById.TryGetValue(identity, out var existing) && !ReferenceEquals(existing, field))
        {
            throw new ArgumentException(
                message: $"Structural type contains ambiguous field identity '{identity}' between '{existing.Name.Value}' and '{field.Name.Value}'.",
                paramName: paramName);
        }

        fieldsById[identity] = field;
    }
}

/// <summary>
/// Field in a structural named type definition.
/// </summary>
public sealed record StructuralField
{
    /// <summary>
    /// Creates a structural field definition.
    /// </summary>
    [JsonConstructor]
    public StructuralField(
        FieldName name,
        TypeRef type,
        FieldCardinality cardinality = FieldCardinality.Single,
        FieldPresence presence = FieldPresence.Required,
        FieldNullability nullability = FieldNullability.NonNullable,
        FieldRole role = FieldRole.Data,
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
        Constraints = constraints.IsDefault ? [] : constraints;
        Annotations = AnnotationMap.Normalize(annotations);
    }

    /// <summary>
    /// Canonical field identity.
    /// </summary>
    public FieldName Name { get; init; }

    /// <summary>
    /// Field type reference.
    /// </summary>
    public TypeRef Type { get; init; }

    /// <summary>
    /// Field cardinality.
    /// </summary>
    public FieldCardinality Cardinality { get; init; }

    /// <summary>
    /// Field presence requirement.
    /// </summary>
    public FieldPresence Presence { get; init; }

    /// <summary>
    /// Field nullability requirement.
    /// </summary>
    public FieldNullability Nullability { get; init; }

    /// <summary>
    /// Field semantic role.
    /// </summary>
    public FieldRole Role { get; init; }

    /// <summary>
    /// Field constraints.
    /// </summary>
    public ImmutableArray<ShapeConstraint> Constraints { get; init; }

    /// <summary>
    /// Optional metadata annotations.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

    /// <summary>
    /// Compares structural fields using value semantics for constraints and annotations.
    /// </summary>
    public bool Equals(StructuralField? other)
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
               && Role == other.Role
               && Constraints.SequenceEqual(other.Constraints)
               && ShapeValueEquality.AreAnnotationsEqual(Annotations, other.Annotations);
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(StructuralField?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Name);
        hash.Add(Type);
        hash.Add((int)Cardinality);
        hash.Add((int)Presence);
        hash.Add((int)Nullability);
        hash.Add((int)Role);
        foreach (var constraint in Constraints)
            hash.Add(constraint);
        hash.Add(ShapeValueEquality.GetAnnotationsHashCode(Annotations));
        return hash.ToHashCode();
    }

    /// <summary>
    /// Returns true when <paramref name="fieldIdentity"/> matches the canonical field name.
    /// </summary>
    public bool MatchesIdentity(string fieldIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldIdentity);
        return string.Equals(Name.Value, fieldIdentity, StringComparison.Ordinal);
    }
}

/// <summary>
/// Enum value metadata.
/// </summary>
/// <param name="Name">Stable enum member name.</param>
/// <param name="Value">Optional serialized value when it differs from <paramref name="Name"/>.</param>
/// <param name="Label">Optional human-readable display label.</param>
/// <param name="Description">Optional free-form description.</param>
public sealed record EnumValue(string Name, string? Value = null, string? Label = null, string? Description = null);

/// <summary>
/// One union case definition.
/// </summary>
public sealed record UnionCase
{
    /// <summary>
    /// Creates a union case definition.
    /// </summary>
    [JsonConstructor]
    public UnionCase(string name, TypeRef type, string? discriminatorValue = null)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Type = Guard.RequireNotNull(type);
        DiscriminatorValue = discriminatorValue ?? name;
    }

    /// <summary>
    /// Case name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Case payload type.
    /// </summary>
    public TypeRef Type { get; init; }

    /// <summary>
    /// Discriminator literal.
    /// </summary>
    public string DiscriminatorValue { get; init; }
}

/// <summary>
/// Union discriminator metadata.
/// </summary>
public sealed record UnionDiscriminator
{
    /// <summary>
    /// Creates union discriminator metadata.
    /// </summary>
    [JsonConstructor]
    public UnionDiscriminator(string fieldName, PrimitiveType type = PrimitiveType.String)
    {
        FieldName = Guard.RequireNotNullOrWhiteSpace(fieldName);
        Type = type;
    }

    /// <summary>
    /// Discriminator field name.
    /// </summary>
    public string FieldName { get; init; }

    /// <summary>
    /// Discriminator primitive type.
    /// </summary>
    public PrimitiveType Type { get; init; }
}
