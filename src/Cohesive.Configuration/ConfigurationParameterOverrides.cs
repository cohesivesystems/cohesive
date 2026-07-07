using System.Linq.Expressions;
using Cohesive.Model;

namespace Cohesive.Configuration;

/// <summary>
/// Builds command-line parameter overrides for hierarchical configuration types.
/// </summary>
/// <param name="prefix">The path prefix.</param>
/// <param name="separator">The field separator.</param>
/// <typeparam name="T">The hierarchical configuration type.</typeparam>
public class ConfigurationParameterOverrides<T>(string? prefix = null, char separator = ':')
{
    readonly string prefix = string.IsNullOrWhiteSpace(prefix) ? "" : prefix.EndsWith(separator) ? prefix : prefix + separator;
    readonly Dictionary<string, string?> overrides = new(StringComparer.OrdinalIgnoreCase);
        
    /// <summary>
    /// Gets the overrides.
    /// </summary>
    /// <returns></returns>
    public IReadOnlyDictionary<string, string?> Overrides => overrides;
    
    /// <summary>
    /// Adds an override for a property path if the value is not null.
    /// </summary>
    /// <param name="path">Hierarchical property path.</param>
    /// <param name="value">Override value.</param>
    public void Add(string path, object? value) =>
        AddCore(path, value);

    /// <summary>
    /// Adds an override for a property path if the value is not null.
    /// </summary>
    /// <param name="path">Hierarchical property path.</param>
    /// <param name="value">Override value.</param>
    public void Add(Expression<Func<T, object?>> path, object? value) => 
        AddCore(FieldPath.Capture(path).ToString(separator), value: value);

    /// <summary>
    /// Adds an override for a property path if the value is not null.
    /// </summary>
    /// <param name="path">Hierarchical property path.</param>
    /// <param name="value">Override value.</param>
    void AddCore(string path, object? value)
    {
        if (value?.ToString() is { } str && !string.IsNullOrWhiteSpace(str))
        {
            overrides[prefix + path] = str;
        }
    }
}
