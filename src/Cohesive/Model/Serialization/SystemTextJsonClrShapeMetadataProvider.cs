using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Annotation keys and values used to retain System.Text.Json wire representations in a CLR-derived
/// shape graph.
/// </summary>
public static class SystemTextJsonShapeAnnotations
{
    /// <summary>Annotation containing the JSON representation of a converter-backed named type.</summary>
    public const string Representation = "serialization.systemTextJson.representation";

    /// <summary>Annotation marking a field whose JSON representation is an object-valued dictionary.</summary>
    public const string Dictionary = "serialization.systemTextJson.dictionary";

    /// <summary>Annotation mapping CLR enum member names to their serialized JSON string values.</summary>
    public const string EnumValues = "serialization.systemTextJson.enumValues";

    /// <summary>JSON string representation.</summary>
    public const string String = "string";

    /// <summary>JSON number representation.</summary>
    public const string Number = "number";

    /// <summary>JSON boolean representation.</summary>
    public const string Boolean = "boolean";

    /// <summary>Arbitrary JSON representation.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Projects System.Text.Json naming and converter metadata into a CLR-derived shape graph without
/// changing the default CLR semantic projection.
/// </summary>
public sealed class SystemTextJsonClrShapeMetadataProvider : IClrShapeMetadataProvider
{
    readonly JsonSerializerOptions options;

    /// <summary>Creates a metadata provider for the supplied serializer contract.</summary>
    /// <param name="options">Serializer options that define the JSON wire representation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public SystemTextJsonClrShapeMetadataProvider(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = new(options);
        this.options.MakeReadOnly(populateMissingResolver: true);
    }

    /// <inheritdoc />
    public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Target switch
        {
            ClrShapeMetadataTarget.Field => GetFieldMetadata(context),
            ClrShapeMetadataTarget.Shape or ClrShapeMetadataTarget.Type => GetTypeMetadata(context.ClrType),
            _ => ClrShapeMetadata.Empty
        };
    }

    ClrShapeMetadata GetFieldMetadata(ClrShapeMetadataContext context)
    {
        var property = context.Property
            ?? throw new InvalidOperationException("Field metadata requires a CLR property.");
        var annotations = IsDictionary(property.PropertyType)
            ? AnnotationMap.Create(SystemTextJsonShapeAnnotations.Dictionary, true)
            : ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty;

        return new()
        {
            FieldName = new(ResolveJsonPropertyName(property)),
            Annotations = annotations
        };
    }

    ClrShapeMetadata GetTypeMetadata(Type clrType)
    {
        var normalized = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (TryGetRepresentation(normalized, out var representation))
        {
            return new()
            {
                Annotations = AnnotationMap.Create(
                    SystemTextJsonShapeAnnotations.Representation,
                    representation)
            };
        }

        if (!normalized.IsEnum || !TryGetStringEnumValues(normalized, out var values))
            return ClrShapeMetadata.Empty;

        return new()
        {
            Annotations = AnnotationMap.Create(
                SystemTextJsonShapeAnnotations.EnumValues,
                AnnotationValue.FromObject(values))
        };
    }

    string ResolveJsonPropertyName(PropertyInfo property)
    {
        var typeInfo = options.GetTypeInfo(property.DeclaringType
            ?? throw new InvalidOperationException($"Property '{property.Name}' has no declaring type."));
        for (var i = 0; i < typeInfo.Properties.Count; i++)
        {
            var jsonProperty = typeInfo.Properties[i];
            if (jsonProperty.AttributeProvider is PropertyInfo candidate
                && candidate.Module == property.Module
                && candidate.MetadataToken == property.MetadataToken)
            {
                return jsonProperty.Name;
            }
        }

        return property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>(inherit: true)?.Name
               ?? options.PropertyNamingPolicy?.ConvertName(property.Name)
               ?? property.Name;
    }

    bool IsDictionary(Type type)
    {
        var normalized = Nullable.GetUnderlyingType(type) ?? type;
        return options.GetTypeInfo(normalized).Kind == JsonTypeInfoKind.Dictionary;
    }

    bool TryGetStringEnumValues(
        Type enumType,
        out IEnumerable<KeyValuePair<AnnotationKey, AnnotationValue>> values)
    {
        var names = Enum.GetNames(enumType);
        var result = new KeyValuePair<AnnotationKey, AnnotationValue>[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var value = Enum.Parse(enumType, names[i], ignoreCase: false);
            var serialized = JsonSerializer.SerializeToElement(value, enumType, options);
            if (serialized.ValueKind != JsonValueKind.String || serialized.GetString() is not { } text)
            {
                values = [];
                return false;
            }

            result[i] = new(new(names[i]), AnnotationValue.FromString(text));
        }

        values = result;
        return true;
    }

    static bool TryGetRepresentation(Type type, out string representation)
    {
        if (type == typeof(AnnotationKey))
        {
            representation = SystemTextJsonShapeAnnotations.String;
            return true;
        }

        if (type == typeof(AnnotationValue))
        {
            representation = SystemTextJsonShapeAnnotations.Unknown;
            return true;
        }

        if (!SingleValueWrapperJsonConverter.ScalarOnly.CanConvert(type))
        {
            representation = string.Empty;
            return false;
        }

        var properties = ShapeTypeInspector.GetReadableProperties(type);
        var overrideName = type.GetCustomAttribute<SingleValueWrapperValuePropertyAttribute>()?.PropertyName;
        var valueProperty = string.IsNullOrWhiteSpace(overrideName)
            ? properties.SingleOrDefault(static property => string.Equals(property.Name, "Value", StringComparison.Ordinal))
              ?? (properties.Length == 1 ? properties[0] : null)
            : properties.SingleOrDefault(property => string.Equals(
                property.Name,
                overrideName,
                StringComparison.Ordinal));
        representation = GetPrimitiveRepresentation(valueProperty?.PropertyType);
        return true;
    }

    static string GetPrimitiveRepresentation(Type? type)
    {
        var normalized = type is null ? null : Nullable.GetUnderlyingType(type) ?? type;
        if (normalized == typeof(string)
            || normalized == typeof(char)
            || normalized == typeof(Guid)
            || normalized == typeof(DateTime)
            || normalized == typeof(DateTimeOffset)
            || normalized == typeof(DateOnly)
            || normalized == typeof(TimeOnly))
        {
            return SystemTextJsonShapeAnnotations.String;
        }

        if (normalized == typeof(bool))
            return SystemTextJsonShapeAnnotations.Boolean;

        if (normalized is not null
            && (normalized == typeof(byte)
                || normalized == typeof(sbyte)
                || normalized == typeof(short)
                || normalized == typeof(ushort)
                || normalized == typeof(int)
                || normalized == typeof(uint)
                || normalized == typeof(long)
                || normalized == typeof(ulong)
                || normalized == typeof(float)
                || normalized == typeof(double)
                || normalized == typeof(decimal)))
        {
            return SystemTextJsonShapeAnnotations.Number;
        }

        return SystemTextJsonShapeAnnotations.Unknown;
    }
}
