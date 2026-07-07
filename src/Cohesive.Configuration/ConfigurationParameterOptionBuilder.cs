namespace Cohesive.Configuration;

/// <summary>
/// Fluent builder used to override parameter metadata discovered from attributes and CLR shape.
/// </summary>
public sealed class ConfigurationParameterOptionBuilder(Action<Func<ConfigurationParameterOption, ConfigurationParameterOption>> update)
{
    readonly Action<Func<ConfigurationParameterOption, ConfigurationParameterOption>> update = Guard.RequireNotNull(update);

    /// <summary>
    /// Overrides the key segment used in configuration providers.
    /// </summary>
    /// <param name="nameOverride">Configuration-space key segment to use.</param>
    /// <returns>The current builder.</returns>
    public ConfigurationParameterOptionBuilder WithNameOverride(string nameOverride)
    {
        update(option => option with { ConfigurationNameOverride = Guard.RequireNotNullOrWhiteSpace(nameOverride) });
        return this;
    }

    /// <summary>
    /// Overrides the generated long CLI switch name.
    /// </summary>
    /// <param name="cliName">CLI switch name, with or without the leading <c>--</c>.</param>
    /// <returns>The current builder.</returns>
    public ConfigurationParameterOptionBuilder WithCliName(string cliName)
    {
        update(option => option with { CliName = Guard.RequireNotNullOrWhiteSpace(cliName) });
        return this;
    }

    /// <summary>
    /// Sets the short CLI switch name.
    /// </summary>
    /// <param name="cliShortName">Short switch name, with or without the leading <c>-</c>.</param>
    /// <returns>The current builder.</returns>
    public ConfigurationParameterOptionBuilder WithCliShortName(string cliShortName)
    {
        update(option => option with { CliShortName = Guard.RequireNotNullOrWhiteSpace(cliShortName) });
        return this;
    }

    /// <summary>
    /// Sets the parameter description.
    /// </summary>
    /// <param name="description">Description text for help and metadata output.</param>
    /// <returns>The current builder.</returns>
    public ConfigurationParameterOptionBuilder WithDescription(string description)
    {
        update(option => option with { Description = Guard.RequireNotNullOrWhiteSpace(description) });
        return this;
    }

    /// <summary>
    /// Restricts valid raw configuration values to the supplied set.
    /// </summary>
    /// <param name="allowedValues">Allowed raw values.</param>
    /// <returns>The current builder.</returns>
    public ConfigurationParameterOptionBuilder WithAllowedValues(IReadOnlyList<string> allowedValues)
    {
        ArgumentException.ThrowIfNullOrEmpty(allowedValues, message: "Allowed values must contain at least one value.");
        update(option => option with { AllowedValues = [.. allowedValues.Select(value => Guard.RequireNotNullOrWhiteSpace(value))] });
        return this;
    }
    
    /// <summary>
    /// Restricts valid raw configuration values to the supplied set.
    /// </summary>
    /// <param name="allowedValues">Allowed raw values.</param>
    /// <returns>The current builder.</returns>
    public ConfigurationParameterOptionBuilder WithAllowedValues(params string[] allowedValues) =>
        WithAllowedValues((IReadOnlyList<string>)allowedValues);
    
    /// <summary>
    /// Sets the unit used when converting integer values into a <see cref="TimeSpan"/>.
    /// </summary>
    /// <param name="timeUnit">Unit used for integer <see cref="TimeSpan"/> inputs.</param>
    /// <returns>The current builder.</returns>
    public ConfigurationParameterOptionBuilder WithTimeUnit(ConfigurationTimeUnit timeUnit)
    {
        update(option => option with { TimeUnit = timeUnit });
        return this;
    }

    /// <summary>
    /// Marks the parameter as required or optional.
    /// </summary>
    /// <param name="required">Whether the parameter must be present.</param>
    /// <returns>The current builder.</returns>
    public ConfigurationParameterOptionBuilder IsRequired(bool required = true)
    {
        update(option => option with { Required = required });
        return this;
    }
}
