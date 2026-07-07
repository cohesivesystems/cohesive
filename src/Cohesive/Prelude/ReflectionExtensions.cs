using System.Collections.Concurrent;
using System.Reflection;

namespace Cohesive.Prelude;

/// <summary>
/// Reflection extensions.
/// </summary>
public static class ReflectionExtensions
{
    static readonly ConcurrentDictionary<Type, PropertyInfo[]> PublicPropertyCache = new();

    /// <summary>
    /// Converts public instance properties on an object to a property value dictionary.
    /// </summary>
    /// <param name="value">The value to read.</param>
    public static IReadOnlyDictionary<string, object?> ToPropertyValueDictionary(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var properties = PublicPropertyCache.GetOrAdd(
            value.GetType(),
            static type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.GetIndexParameters().Length == 0)
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .ToArray()
        );

        var result = new Dictionary<string, object?>(properties.Length, StringComparer.Ordinal);
        foreach (var property in properties)
            result[property.Name] = property.GetValue(value);

        return result;
    }

    /// <summary>
    /// Gets all public string constants defined on a type, sorted by field name.
    /// </summary>
    /// <param name="type">The type to scan for string constants.</param>
    /// <returns>A list of tuples containing the field name and its string constant value.</returns>
    public static IReadOnlyList<(string Name, string Value)> GetStringConsts(this Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(static field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
        .Select(static field => (field.Name, (string)field.GetRawConstantValue()!))
        .OrderBy(static tuple => tuple.Name, StringComparer.Ordinal)
        .ToList();
}
