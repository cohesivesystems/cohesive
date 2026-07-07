using Microsoft.Extensions.Configuration;

namespace Cohesive.Configuration;

/// <summary>
/// Extensions for <see cref="IConfiguration"/>.
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>
    /// Gets a configuration value, favoring the direct value if provided, then falling back to the configuration key.
    /// </summary>
    /// <param name="configuration">The configuration properties.</param>
    /// <param name="directValue">The direct value to return if not null or empty.</param>
    /// <param name="configurationKey">The configuration key to resolve if the direct value is null or empty.</param>
    /// <returns>The resolved configuration value, or null if not found.</returns>
    public static string? ResolveConfiguredValue(this IConfiguration configuration, string? directValue, string? configurationKey)
    {
        if (!string.IsNullOrWhiteSpace(directValue))
            return directValue;

        if (string.IsNullOrWhiteSpace(configurationKey))
            return null;

        return configuration[configurationKey];
    }
}