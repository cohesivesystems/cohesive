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

    /// <summary>
    /// Returns cached readable public instance properties in deterministic declaration order.
    /// </summary>
    /// <param name="type">CLR type whose readable properties are requested.</param>
    /// <returns>A cached property array with ignored, static, indexed, and unreadable properties removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    public static PropertyInfo[] GetReadableProperties(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ReadablePropertiesByType.GetOrAdd(type, static currentType =>
        {
            var allProperties = currentType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            if (allProperties.Length == 0)
                return [];

            Array.Sort(allProperties, CompareProperties);

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
    /// Returns cached readable public instance properties in deterministic declaration order.
    /// </summary>
    /// <typeparam name="T">CLR type whose readable properties are requested.</typeparam>
    /// <returns>A cached property array with ignored, static, indexed, and unreadable properties removed.</returns>
    public static PropertyInfo[] GetReadableProperties<T>() => GetReadableProperties(typeof(T));

    /// <summary>
    /// Returns cached shape-oriented property metadata in deterministic declaration order.
    /// </summary>
    /// <param name="type">CLR type whose shape metadata is requested.</param>
    /// <returns>Cached readable-property metadata including CLR nullability-derived optionality.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    public static ClrPropertyShapeMetadata[] GetReadablePropertyMetadata(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ShapePropertiesByType.GetOrAdd(type, static currentType =>
        {
            var properties = GetReadableProperties(currentType);
            if (properties.Length == 0)
                return [];

            // NullabilityInfoContext maintains mutable internal caches and is not thread-safe. The
            // completed metadata array is cached, so keep the context local to this value factory.
            NullabilityInfoContext nullabilityContext = new();
            var metadata = new ClrPropertyShapeMetadata[properties.Length];
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                var nullability = nullabilityContext.Create(property);
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

    /// <summary>
    /// Determines whether two reflection values identify the same property on the same constructed
    /// declaring CLR type.
    /// </summary>
    /// <param name="left">First property identity.</param>
    /// <param name="right">Second property identity.</param>
    /// <returns>
    /// <see langword="true"/> when the properties are equal or share a module, metadata token, and
    /// constructed declaring type; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.
    /// </exception>
    public static bool IsSameProperty(PropertyInfo left, PropertyInfo right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left == right
               || left.Module == right.Module
               && left.MetadataToken == right.MetadataToken
               && left.DeclaringType == right.DeclaringType;
    }

    static int CompareProperties(PropertyInfo left, PropertyInfo right)
    {
        var compared = StringComparer.Ordinal.Compare(
            left.DeclaringType?.Assembly.GetName().Name,
            right.DeclaringType?.Assembly.GetName().Name);
        if (compared != 0)
            return compared;

        compared = StringComparer.Ordinal.Compare(left.Module.ScopeName, right.Module.ScopeName);
        if (compared != 0)
            return compared;

        compared = StringComparer.Ordinal.Compare(
            left.DeclaringType is null ? string.Empty : ClrShapeIdentityConvention.GetTypeId(left.DeclaringType).Value,
            right.DeclaringType is null ? string.Empty : ClrShapeIdentityConvention.GetTypeId(right.DeclaringType).Value);
        if (compared != 0)
            return compared;

        compared = left.MetadataToken.CompareTo(right.MetadataToken);
        if (compared != 0)
            return compared;

        compared = StringComparer.Ordinal.Compare(left.Name, right.Name);
        if (compared != 0)
            return compared;

        return StringComparer.Ordinal.Compare(
            ClrShapeIdentityConvention.GetTypeId(left.PropertyType).Value,
            ClrShapeIdentityConvention.GetTypeId(right.PropertyType).Value);
    }
}
