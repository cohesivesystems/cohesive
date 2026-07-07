namespace Cohesive.Configuration;

/// <summary>
/// Declares configuration metadata for a bound property.
/// </summary>
/// <param name="keyOverride">
/// Optional configuration-space name to use instead of the CLR property name.
/// </param>
/// <remarks>
/// Attribute metadata is merged with expression-based overrides supplied through
/// <see cref="ConfigurationParameterOptions{T}"/>. When both are present, the explicit
/// expression-based override wins.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ConfigurationParameterAttribute(string? keyOverride = null) : Attribute
{
    static readonly ConfigurationTimeUnit UnspecifiedTimeUnit = (ConfigurationTimeUnit)(-1);

    /// <summary>
    /// Overrides the key segment used in configuration providers.
    /// </summary>
    public string? KeyOverride { get; init; } = keyOverride;

    /// <summary>
    /// Overrides the generated long CLI switch name, for example <c>--config-value</c>.
    /// </summary>
    public string? CliKey { get; init; }

    /// <summary>
    /// Sets the short CLI switch name, for example <c>-c</c>.
    /// </summary>
    public string? CliShortKey { get; init; }

    /// <summary>
    /// Describes the parameter for help and metadata output.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Restricts valid raw values to the supplied set.
    /// </summary>
    public string[]? AllowedValues { get; init; }

    /// <summary>
    /// Indicates whether the parameter must be present in configuration.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Specifies the unit used when converting integer configuration values into a <see cref="TimeSpan"/>.
    /// </summary>
    /// <remarks>
    /// When unset, raw values for <see cref="TimeSpan"/> parameters are parsed using the framework's
    /// standard <see cref="TimeSpan"/> text formats instead of integer-unit conversion.
    /// </remarks>
    public ConfigurationTimeUnit TimeUnit { get; init; } = UnspecifiedTimeUnit;

    /// <summary>
    /// Returns the configured time unit when one was explicitly supplied.
    /// </summary>
    /// <returns>
    /// The configured time unit, or <see langword="null"/> when the attribute leaves the unit unspecified.
    /// </returns>
    internal ConfigurationTimeUnit? GetTimeUnitOrNull() =>
        Enum.IsDefined(TimeUnit) ? TimeUnit : null;
}


/// <summary>
/// Unit used when converting integer configuration values into <see cref="TimeSpan"/> instances.
/// </summary>
public enum ConfigurationTimeUnit
{
    /// <summary>
    /// Treat the raw integer value as milliseconds.
    /// </summary>
    Milliseconds,

    /// <summary>
    /// Treat the raw integer value as seconds.
    /// </summary>
    Seconds,

    /// <summary>
    /// Treat the raw integer value as minutes.
    /// </summary>
    Minutes,

    /// <summary>
    /// Treat the raw integer value as hours.
    /// </summary>
    Hours
}
