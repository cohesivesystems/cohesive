namespace Cohesive.Configuration;

/// <summary>
/// Thrown when one or more configuration parameters cannot be validated or converted.
/// </summary>
/// <param name="message">Top-level failure message.</param>
/// <param name="errors">Individual parameter errors collected during parsing.</param>
public sealed class ConfigurationParameterParseException(string message, IReadOnlyList<string> errors) : Exception(message)
{
    /// <summary>
    /// Individual parse and validation errors keyed by configuration parameter context.
    /// </summary>
    public IReadOnlyList<string> Errors { get; } = Guard.RequireNotNull(errors);
}
