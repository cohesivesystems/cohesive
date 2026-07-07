namespace Cohesive.Host.Cli;

/// <summary>
/// Fluent builder used to configure a positional CLI argument mapped to a configuration property.
/// </summary>
public sealed class CliArgumentBuilder
{
    readonly Action<Func<CliCommandArgument, CliCommandArgument>> update;

    internal CliArgumentBuilder(Action<Func<CliCommandArgument, CliCommandArgument>> update)
    {
        this.update = Guard.RequireNotNull(update);
    }

    /// <summary>
    /// Overrides the positional argument name shown in help.
    /// </summary>
    /// <param name="name">Argument name to display in command help.</param>
    /// <returns>The current builder.</returns>
    public CliArgumentBuilder WithName(string name)
    {
        update(argument => argument with { Name = Guard.RequireNotNullOrWhiteSpace(name) });
        return this;
    }

    /// <summary>
    /// Overrides the positional argument description shown in help.
    /// </summary>
    /// <param name="description">Description text for the positional argument.</param>
    /// <returns>The current builder.</returns>
    public CliArgumentBuilder WithDescription(string description)
    {
        update(argument => argument with { Description = Guard.RequireNotNullOrWhiteSpace(description) });
        return this;
    }
}

sealed record CliCommandArgument(
    string PropertyName,
    FieldPath Path,
    string? Name = null,
    string? Description = null
    );
