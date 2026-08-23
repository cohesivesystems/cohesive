using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model.Authoring;

/// <summary>
/// Default mapping from CLR types to semantic type references using deterministic serialized property identities.
/// </summary>
public sealed class DefaultClrTypeRefMapper : IClrTypeRefMapper
{
    /// <summary>
    /// Maps the supplied CLR type to a semantic type reference using available nullability metadata.
    /// </summary>
    /// <remarks>
    /// Types declaring <see cref="PortableJsonValueAttribute"/> retain their explicit JSON contract. Structural
    /// object fields use <see cref="JsonPropertyNameAttribute"/> when present and otherwise use the CLR property
    /// name. Fields are ordered ordinally by that semantic name. Unsupported, recursive, polymorphic, or ambiguous
    /// CLR shapes produce an <see cref="OpaqueRuntimeTypeRef"/> carrying a type-inference diagnostic.
    /// </remarks>
    /// <param name="clrType">CLR type to project into a portable semantic type reference.</param>
    /// <param name="nullability">Optional reflection nullability metadata for the mapped occurrence.</param>
    /// <returns>A portable type reference, or a diagnostic-bearing opaque reference when inference is unsafe.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is <see langword="null"/>.</exception>
    public TypeRef Map(Type clrType, NullabilityInfo? nullability)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return MapInternal(clrType, nullability, []);
    }

    static TypeRef MapInternal(Type clrType, NullabilityInfo? nullability, HashSet<Type> mapPath)
    {
        var unwrapped = Nullable.GetUnderlyingType(nullableType: clrType) ?? clrType;
        if (TryMapScalarTypeKind(unwrapped, out var scalarKind))
            return new ScalarTypeRef(scalarKind);

        if (unwrapped.IsEnum)
            return new EnumTypeRef(name: unwrapped.Name, members: [.. Enum.GetNames(enumType: unwrapped)]);

        if (PortableJsonValueAttribute.TryGetKind(unwrapped, out var portableJsonKind))
            return new JsonTypeRef(portableJsonKind);

        if (TryMapJsonType(unwrapped, out var jsonType))
            return jsonType;

        if (TryMapJsonSingleValueWrapperType(unwrapped, nullability, mapPath, out var singleValueWrapperType))
            return singleValueWrapperType;

        if (TryGetStructuredQuantityRepresentationType(type: unwrapped, representationType: out var representationType))
        {
            if (!TryMapScalarTypeKind(clrType: representationType, kind: out var representationKind))
            {
                return Opaque(
                    unwrapped,
                    TypeInferenceDiagnosticReasons.UnsupportedQuantityRepresentation,
                    $"Structured quantity representation type '{representationType.FullName ?? representationType.Name}' is not a supported scalar.");
            }

            return new QuantityTypeRef(quantity: unwrapped.Name, baseKind: representationKind);
        }

        if (TryGetKeyValuePairTypes(
                type: unwrapped,
                nullability: nullability,
                keyType: out var keyType,
                keyNullability: out var keyNullability,
                valueType: out var valueType,
                valueNullability: out var valueNullability))
        {
            return new ObjectTypeRef(
            [
                new(name: "Key",
                    type: MapInternal(clrType: keyType, nullability: keyNullability, mapPath: mapPath),
                    presence: IsOptional(keyType, keyNullability) ? FieldPresence.Optional : FieldPresence.Required,
                    nullability: IsOptional(keyType, keyNullability)
                        ? FieldNullability.Nullable
                        : FieldNullability.NonNullable
                    ),
                new(name: "Value",
                    type: MapInternal(clrType: valueType, nullability: valueNullability, mapPath: mapPath),
                    presence: IsOptional(valueType, valueNullability) ? FieldPresence.Optional : FieldPresence.Required,
                    nullability: IsOptional(valueType, valueNullability)
                        ? FieldNullability.Nullable
                        : FieldNullability.NonNullable
                    )
            ]);
        }

        if (IsDictionaryType(unwrapped))
        {
            return Opaque(
                unwrapped,
                TypeInferenceDiagnosticReasons.UnsupportedDictionary,
                "CLR dictionary types require a map/object-keyed type reference that is not available yet.");
        }

        if (TryGetEnumerableElementType(type: unwrapped, nullability: nullability, elementType: out var elementType, elementNullability: out var elementNullability))
            return new ArrayTypeRef(ElementType: MapInternal(clrType: elementType, nullability: elementNullability, mapPath: mapPath));

        if (unwrapped == typeof(object))
            return Opaque(
                unwrapped,
                TypeInferenceDiagnosticReasons.ObjectRuntimeType,
                "System.Object does not expose a stable structural type.");

        if (IsJsonPolymorphicType(unwrapped))
        {
            return Opaque(
                unwrapped,
                TypeInferenceDiagnosticReasons.PolymorphicType,
                "Polymorphic CLR types require named type definitions and references before they can be represented structurally.");
        }

        if (unwrapped.IsAbstract || unwrapped.IsInterface)
        {
            return Opaque(
                unwrapped,
                TypeInferenceDiagnosticReasons.AbstractType,
                "Abstract/interface CLR types cannot be represented structurally without a concrete type set.");
        }

        if (!mapPath.Add(unwrapped))
        {
            return Opaque(
                unwrapped,
                TypeInferenceDiagnosticReasons.RecursiveType,
                "Recursive CLR types require named type definitions and references before they can be represented structurally.");
        }

        try
        {
            var properties = ShapeTypeInspector.GetReadableProperties(unwrapped)
                .Select(static property => (
                    Property: property,
                    Name: GetSerializedMemberName(property)))
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .ToArray();

            if (properties.Length == 0)
            {
                return Opaque(
                    unwrapped,
                    TypeInferenceDiagnosticReasons.NoReadableProperties,
                    "The CLR type has no readable public instance properties to infer from.");
            }

            if (properties.Select(static property => property.Name).Distinct(StringComparer.Ordinal).Count()
                != properties.Length)
            {
                return Opaque(
                    unwrapped,
                    TypeInferenceDiagnosticReasons.AmbiguousSerializedProperty,
                    "The CLR type maps more than one readable property to the same serialized field name.");
            }

            return new ObjectTypeRef(
                [.. properties.Select(x =>
                {
                    var propertyNullability = CreateNullabilityOrNull(x.Property);
                    return new ObjectFieldTypeDef(
                        name: x.Name,
                        type: MapInternal(
                            clrType: x.Property.PropertyType,
                            nullability: propertyNullability,
                            mapPath: mapPath
                            ),
                        presence: IsOptional(x.Property.PropertyType, propertyNullability)
                            ? FieldPresence.Optional
                            : FieldPresence.Required,
                        nullability: IsOptional(x.Property.PropertyType, propertyNullability)
                            ? FieldNullability.Nullable
                            : FieldNullability.NonNullable
                            );
                })]);
        }
        finally
        {
            mapPath.Remove(unwrapped);
        }
    }

    static OpaqueRuntimeTypeRef Opaque(Type type, string reason, string? message = null) =>
        new(type.FullName ?? type.Name, new TypeInferenceDiagnostic(reason: reason, message: message));

    /// <summary>Returns the deterministic JSON field identity of a reflected member.</summary>
    /// <remarks>
    /// An explicit <see cref="JsonPropertyNameAttribute"/> value is authoritative; otherwise the CLR member name is
    /// returned. Serializer naming policies are intentionally excluded because they are ambient configuration rather
    /// than durable semantic metadata.
    /// </remarks>
    /// <param name="member">Reflected CLR member whose serialized identity is requested.</param>
    /// <returns>The explicit JSON property name, or the CLR member name when no attribute is present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> is <see langword="null"/>.</exception>
    public static string GetSerializedMemberName(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name ?? member.Name;
    }

    static bool TryMapJsonType(Type type, out TypeRef typeRef)
    {
        if (type == typeof(JsonObject))
        {
            typeRef = new JsonTypeRef(JsonTypeKind.Object);
            return true;
        }

        if (type == typeof(JsonArray))
        {
            typeRef = new JsonTypeRef(JsonTypeKind.Array);
            return true;
        }

        if (type == typeof(JsonValue))
        {
            typeRef = new JsonTypeRef(JsonTypeKind.Any);
            return true;
        }

        if (typeof(JsonNode).IsAssignableFrom(type)
            || type == typeof(JsonElement)
            || type == typeof(JsonDocument)
            || type == typeof(AnnotationValue))
        {
            typeRef = new JsonTypeRef(JsonTypeKind.Any);
            return true;
        }

        typeRef = null!;
        return false;
    }

    static bool TryMapJsonSingleValueWrapperType(
        Type type,
        NullabilityInfo? nullability,
        HashSet<Type> mapPath,
        out TypeRef typeRef)
    {
        var converterAttribute = type.GetCustomAttribute<JsonConverterAttribute>(inherit: true);
        if (converterAttribute?.ConverterType != typeof(SingleValueWrapperJsonConverter))
        {
            typeRef = null!;
            return false;
        }

        var valuePropertyName = type.GetCustomAttribute<SingleValueWrapperValuePropertyAttribute>(inherit: true)?.PropertyName ?? "Value";
        var valueProperty = type.GetProperty(valuePropertyName, BindingFlags.Public | BindingFlags.Instance);
        if (valueProperty is null
            || valueProperty.GetMethod is null
            || valueProperty.GetMethod.IsStatic
            || valueProperty.GetIndexParameters().Length != 0)
        {
            typeRef = Opaque(
                type,
                TypeInferenceDiagnosticReasons.InvalidSingleValueWrapper,
                "The CLR type declares the single-value wrapper JSON converter but does not expose a readable value property.");
            return true;
        }

        typeRef = MapInternal(
            clrType: valueProperty.PropertyType,
            nullability: CreateNullabilityOrNull(valueProperty) ?? nullability,
            mapPath: mapPath);
        return true;
    }

    static bool IsDictionaryType(Type type)
    {
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
            return true;

        if (type.IsGenericType)
        {
            var genericTypeDefinition = type.GetGenericTypeDefinition();
            if (genericTypeDefinition == typeof(Dictionary<,>)
                || genericTypeDefinition == typeof(IReadOnlyDictionary<,>)
                || genericTypeDefinition == typeof(IDictionary<,>))
            {
                return true;
            }
        }

        return type.GetInterfaces().Any(static x =>
            x.IsGenericType
            && (x.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                || x.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
    }

    static bool IsJsonPolymorphicType(Type type) =>
        type.GetCustomAttribute<JsonPolymorphicAttribute>(inherit: true) is not null
        || type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: true).Any();

    static bool TryGetEnumerableElementType(
        Type type,
        NullabilityInfo? nullability,
        out Type elementType,
        out NullabilityInfo? elementNullability)
    {
        if (type == typeof(string))
        {
            elementType = typeof(void);
            elementNullability = null;
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType() ?? typeof(void);
            elementNullability = nullability?.ElementType;
            return elementType != typeof(void);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            elementType = type.GetGenericArguments()[0];
            elementNullability = nullability?.GenericTypeArguments.FirstOrDefault();
            return true;
        }

        var enumerableInterface = type
            .GetInterfaces()
            .FirstOrDefault(predicate: x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerableInterface is not null)
        {
            elementType = enumerableInterface.GetGenericArguments()[0];
            elementNullability = nullability?.GenericTypeArguments.FirstOrDefault();
            return true;
        }

        elementType = typeof(void);
        elementNullability = null;
        return false;
    }

    static bool TryGetStructuredQuantityRepresentationType(Type type, out Type representationType)
    {
        var structuredQuantityInterface = type.GetInterfaces()
            .FirstOrDefault(x =>
                x.IsGenericType
                && x.GetGenericTypeDefinition() == typeof(IStructuredQuantity<,,>)
                && x.GetGenericArguments()[0] == type);

        if (structuredQuantityInterface is null)
        {
            representationType = typeof(void);
            return false;
        }

        representationType = structuredQuantityInterface.GetGenericArguments()[2];
        return true;
    }

    static bool TryGetKeyValuePairTypes(
        Type type,
        NullabilityInfo? nullability,
        out Type keyType,
        out NullabilityInfo? keyNullability,
        out Type valueType,
        out NullabilityInfo? valueNullability)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            var arguments = type.GetGenericArguments();
            keyType = arguments[0];
            valueType = arguments[1];
            keyNullability = nullability?.GenericTypeArguments.ElementAtOrDefault(0);
            valueNullability = nullability?.GenericTypeArguments.ElementAtOrDefault(1);
            return true;
        }

        keyType = typeof(void);
        keyNullability = null;
        valueType = typeof(void);
        valueNullability = null;
        return false;
    }

    /// <summary>Attempts to map a CLR scalar type to its canonical semantic scalar kind.</summary>
    /// <remarks>
    /// Nullable value types are unwrapped before matching. The mapping is shared by structural CLR authoring
    /// surfaces so scalar contracts remain aligned across blocks.
    /// </remarks>
    /// <param name="clrType">CLR type to classify.</param>
    /// <param name="kind">Receives the canonical scalar kind when the type is supported.</param>
    /// <returns><see langword="true"/> when <paramref name="clrType"/> has a canonical scalar mapping.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is <see langword="null"/>.</exception>
    public static bool TryMapScalarTypeKind(Type clrType, out ScalarTypeKind kind)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        var unwrapped = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (unwrapped == typeof(string))
        {
            kind = ScalarTypeKind.String;
            return true;
        }

        if (unwrapped == typeof(int) || unwrapped == typeof(short) || unwrapped == typeof(byte))
        {
            kind = ScalarTypeKind.Int32;
            return true;
        }

        if (unwrapped == typeof(long))
        {
            kind = ScalarTypeKind.Int64;
            return true;
        }

        if (unwrapped == typeof(decimal) || unwrapped == typeof(float) || unwrapped == typeof(double))
        {
            kind = ScalarTypeKind.Decimal;
            return true;
        }

        if (unwrapped == typeof(bool))
        {
            kind = ScalarTypeKind.Bool;
            return true;
        }

        if (unwrapped == typeof(Guid))
        {
            kind = ScalarTypeKind.Guid;
            return true;
        }

        if (unwrapped == typeof(DateOnly))
        {
            kind = ScalarTypeKind.Date;
            return true;
        }

        if (unwrapped == typeof(DateTime))
        {
            kind = ScalarTypeKind.DateTime;
            return true;
        }

        if (unwrapped == typeof(DateTimeOffset))
        {
            kind = ScalarTypeKind.Instant;
            return true;
        }

        if (unwrapped == typeof(byte[]))
        {
            kind = ScalarTypeKind.Bytes;
            return true;
        }

        kind = default;
        return false;
    }

    static NullabilityInfo? CreateNullabilityOrNull(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        try
        {
            return new NullabilityInfoContext().Create(property);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static bool IsOptional(Type clrType, NullabilityInfo? nullability)
    {
        if (Nullable.GetUnderlyingType(clrType) is not null)
            return true;

        return !clrType.IsValueType && nullability?.ReadState == NullabilityState.Nullable;
    }
}
