using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Reflection helpers for CLR shape types.
/// </summary>
public static class ShapeTypeInspector
{
    static readonly ConcurrentDictionary<Type, PropertyInfo[]> ReadablePropertiesByType = new();
    static readonly ConcurrentDictionary<Type, ClrPropertyShapeMetadata[]> ShapePropertiesByType = new();
    static readonly NullabilityInfoContext NullabilityContext = new();

    /// <summary>
    /// Returns cached readable public instance properties ordered by metadata token.
    /// </summary>
    public static PropertyInfo[] GetReadableProperties(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ReadablePropertiesByType.GetOrAdd(type, static currentType =>
        {
            var allProperties = currentType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            if (allProperties.Length == 0)
                return [];

            Array.Sort(allProperties, static (left, right) => left.MetadataToken.CompareTo(right.MetadataToken));

            var readableCount = 0;
            for (var i = 0; i < allProperties.Length; i++)
            {
                var property = allProperties[i];
                if (property.GetMethod is null || property.GetMethod.IsStatic || property.GetIndexParameters().Length != 0)
                    continue;
                if (IsAlwaysIgnored(property))
                    continue;

                readableCount++;
            }

            if (readableCount == 0)
                return [];

            var readable = new PropertyInfo[readableCount];
            var writeIndex = 0;
            for (var i = 0; i < allProperties.Length; i++)
            {
                var property = allProperties[i];
                if (property.GetMethod is null || property.GetMethod.IsStatic || property.GetIndexParameters().Length != 0)
                    continue;
                if (IsAlwaysIgnored(property))
                    continue;

                readable[writeIndex++] = property;
            }

            return readable;
        });
    }

    static bool IsAlwaysIgnored(PropertyInfo property) =>
        property.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true) is
        {
            Condition: JsonIgnoreCondition.Always
        };

    /// <summary>
    /// Returns cached readable public instance properties ordered by metadata token.
    /// </summary>
    public static PropertyInfo[] GetReadableProperties<T>() => GetReadableProperties(typeof(T));

    /// <summary>
    /// Returns cached shape-oriented property metadata ordered by metadata token.
    /// </summary>
    public static ClrPropertyShapeMetadata[] GetReadablePropertyMetadata(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ShapePropertiesByType.GetOrAdd(type, static currentType =>
        {
            var properties = GetReadableProperties(currentType);
            if (properties.Length == 0)
                return [];

            var metadata = new ClrPropertyShapeMetadata[properties.Length];
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                var nullability = NullabilityContext.Create(property);
                metadata[i] = new ClrPropertyShapeMetadata(
                    property: property,
                    isOptional: IsOptional(property.PropertyType, nullability));
            }

            return metadata;
        });
    }

    static bool IsOptional(Type propertyType, NullabilityInfo nullability)
    {
        if (Nullable.GetUnderlyingType(propertyType) is not null)
            return true;

        return !propertyType.IsValueType && nullability.ReadState == NullabilityState.Nullable;
    }
}
