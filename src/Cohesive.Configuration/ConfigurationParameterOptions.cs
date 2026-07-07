using System.Linq.Expressions;
using Cohesive.Model;

namespace Cohesive.Configuration;

/// <summary>
/// CLI parameter to configuration parameter mapping options.
/// </summary>
/// <param name="PropertyName">CLR property name for the mapped member.</param>
/// <param name="Path">CLR member path from the root configuration object.</param>
/// <param name="ConfigurationNameOverride">Optional configuration-space key override.</param>
/// <param name="CliName">Optional long CLI switch name.</param>
/// <param name="CliShortName">Optional short CLI switch name.</param>
/// <param name="Description">Optional help text.</param>
/// <param name="AllowedValues">Optional set of allowed raw values.</param>
/// <param name="Required">Optional required flag override.</param>
/// <param name="TimeUnit">Optional <see cref="TimeSpan"/> unit override.</param>
public sealed record ConfigurationParameterOption(
    string PropertyName,
    FieldPath Path,
    string? ConfigurationNameOverride = null,
    string? CliName = null,
    string? CliShortName = null,
    string? Description = null,
    string[]? AllowedValues = null,
    bool? Required = null,
    ConfigurationTimeUnit? TimeUnit = null
    );


/// <summary>
/// Fully resolved parameter metadata after merging attribute and expression-based configuration.
/// </summary>
/// <param name="PropertyName">CLR property name for the parameter.</param>
/// <param name="Path">CLR member path from the root configuration object.</param>
/// <param name="ConfigurationKey">Effective configuration key used to read the raw value.</param>
/// <param name="CliName">Effective long CLI switch name.</param>
/// <param name="CliShortName">Effective short CLI switch name, if any.</param>
/// <param name="Description">Resolved description text, if any.</param>
/// <param name="AllowedValues">Resolved allowed values.</param>
/// <param name="Required">Resolved required flag.</param>
/// <param name="TimeUnit">Resolved <see cref="TimeSpan"/> unit, if any.</param>
/// <param name="ParameterType">CLR type bound for the parameter.</param>
public sealed record ConfigurationParameterDescriptor(
    string PropertyName,
    FieldPath Path,
    string ConfigurationKey,
    string CliName,
    string? CliShortName,
    string? Description,
    IReadOnlyList<string> AllowedValues,
    bool Required,
    ConfigurationTimeUnit? TimeUnit,
    Type ParameterType
    );

/// <summary>
/// Collects expression-based parameter overrides and enum remapping rules for a configuration type.
/// </summary>
/// <typeparam name="T">Root typed configuration object.</typeparam>
public sealed class ConfigurationParameterOptions<T>
{
    readonly List<ConfigurationParameterOption> options = [];
    readonly Dictionary<Type, IReadOnlyDictionary<string, object>> enumMappings = [];

    /// <summary>
    /// Registered parameter mapping options.
    /// </summary>
    public IReadOnlyList<ConfigurationParameterOption> Options => options;

    internal IReadOnlyDictionary<Type, IReadOnlyDictionary<string, object>> EnumMappings => enumMappings;

    /// <summary>
    /// Starts configuring overrides for a selected property path.
    /// </summary>
    /// <param name="member">Property selector rooted at <typeparamref name="T"/>.</param>
    /// <typeparam name="TParameter">Selected property type.</typeparam>
    /// <returns>A fluent builder for the selected property path.</returns>
    public ConfigurationParameterOptionBuilder Map<TParameter>(Expression<Func<T, TParameter>> member)
    {
        ArgumentNullException.ThrowIfNull(member);
        
        var propertyChain = ExpressionExtensions.CapturePropertyChain(member);
        var path = ExpressionExtensions.CreateFieldPath(propertyChain);
        var existingIndex = options.FindIndex(option => option.Path == path);
        if (existingIndex < 0)
        {
            options.Add(new(PropertyName: propertyChain[^1].Name, Path: path));
            existingIndex = options.Count - 1;
        }

        var index = existingIndex;
        return new(update => options[index] = update(options[index]));
    }

    /// <summary>
    /// Registers custom raw-value to enum-value mappings for an enum type.
    /// </summary>
    /// <param name="mappings">Mapping table keyed by configuration-space strings.</param>
    /// <typeparam name="TEnum">Enum type to override.</typeparam>
    /// <returns>The current options instance.</returns>
    public ConfigurationParameterOptions<T> MapEnum<TEnum>(IReadOnlyDictionary<string, TEnum> mappings)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(mappings);

        enumMappings[typeof(TEnum)] = mappings.ToDictionary(
            pair => Guard.RequireNotNullOrWhiteSpace(pair.Key),
            pair => (object)pair.Value,
            StringComparer.OrdinalIgnoreCase);
        return this;
    }

    /// <summary>
    /// Registers custom raw-value to enum-value mappings for an enum type.
    /// </summary>
    /// <param name="mappings">Tuple pairs of configuration-space strings and enum values.</param>
    /// <typeparam name="TEnum">Enum type to override.</typeparam>
    /// <returns>The current options instance.</returns>
    public ConfigurationParameterOptions<T> MapEnum<TEnum>(params (string ConfigurationValue, TEnum EnumValue)[] mappings) where TEnum : struct, Enum
    {
        ArgumentException.ThrowIfNullOrEmpty(mappings, message: "Enum mappings must contain at least one entry.");
        return MapEnum(mappings.ToDictionary(pair => pair.ConfigurationValue, pair => pair.EnumValue, StringComparer.OrdinalIgnoreCase));
    }
}
