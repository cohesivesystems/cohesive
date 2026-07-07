using Microsoft.Extensions.Configuration;

namespace Cohesive.Configuration;

/// <summary>
/// Configures how configuration profiles are located and composed of an <see cref="IConfiguration"/> tree.
/// </summary>
public sealed class ConfigurationProfileOptions
{
    /// <summary>
    /// Gets or initializes the configuration section path that contains named profiles.
    /// </summary>
    public string ProfilesSectionPath { get; init; } = "Profiles";

    /// <summary>
    /// Gets or initializes the configuration key that selects the active profile.
    /// </summary>
    public string ActiveProfileKey { get; init; } = "ActiveProfile";

    /// <summary>
    /// Gets or initializes the child key used to express profile inheritance.
    /// </summary>
    public string ExtendsKey { get; init; } = "Extends";

    /// <summary>
    /// Gets or initializes the fallback profile name used when no active profile is configured.
    /// </summary>
    public string DefaultActiveProfile { get; init; } = "default";
}

/// <summary>
/// Describes the environment and configuration profile chain applied to the live configuration graph.
/// </summary>
/// <param name="EnvironmentName">The current host environment name.</param>
/// <param name="ActiveProfile">The selected active profile name.</param>
/// <param name="AppliedProfiles">The ordered profile chain that was applied, from base to most specific.</param>
public sealed record ConfigurationProfileContext(
    string EnvironmentName,
    string ActiveProfile,
    IReadOnlyList<string> AppliedProfiles
    );

/// <summary>
/// Represents the resolved output of a configuration profile selection before it is attached to a live configuration manager.
/// </summary>
public sealed class ConfigurationProfileResolution
{
    /// <summary>
    /// Initializes a resolved configuration profile result.
    /// </summary>
    /// <param name="activeProfile">The selected active profile name.</param>
    /// <param name="appliedProfiles">The ordered profile chain that was resolved, from base to most specific.</param>
    /// <param name="values">The flattened configuration values contributed by the resolved profile chain.</param>
    public ConfigurationProfileResolution(string activeProfile, IReadOnlyList<string> appliedProfiles, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeProfile);
        ArgumentNullException.ThrowIfNull(appliedProfiles);
        ArgumentNullException.ThrowIfNull(values);
        ActiveProfile = activeProfile;
        AppliedProfiles = appliedProfiles;
        Values = values;
    }

    /// <summary>
    /// Gets the selected active profile name.
    /// </summary>
    public string ActiveProfile { get; }

    /// <summary>
    /// Gets the ordered profile chain that was resolved, from base to most specific.
    /// </summary>
    public IReadOnlyList<string> AppliedProfiles { get; }

    /// <summary>
    /// Gets the flattened configuration values contributed by the resolved profile chain.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Values { get; }

    /// <summary>
    /// Creates a runtime context object for the specified host environment.
    /// </summary>
    /// <param name="environmentName">The host environment name associated with the applied configuration.</param>
    /// <returns>A context describing the applied profile chain for the given environment.</returns>
    public ConfigurationProfileContext CreateContext(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        return new(
            EnvironmentName: environmentName,
            ActiveProfile: ActiveProfile,
            AppliedProfiles: AppliedProfiles
            );
    }
}

/// <summary>
/// Exposes the bootstrap-time profiled configuration view used to register additional providers before the final profile overlay is applied.
/// </summary>
public sealed class ConfigurationProfileBootstrapContext
{
    /// <summary>
    /// Initializes a bootstrap context for two-phase profile composition.
    /// </summary>
    /// <param name="resolution">The bootstrap profile resolution.</param>
    /// <param name="configuration">A configuration view with the bootstrap profile overlay applied.</param>
    public ConfigurationProfileBootstrapContext(ConfigurationProfileResolution resolution, IConfigurationRoot configuration)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(configuration);
        Resolution = resolution;
        Configuration = configuration;
    }

    /// <summary>
    /// Gets the bootstrap profile resolution.
    /// </summary>
    public ConfigurationProfileResolution Resolution { get; }

    /// <summary>
    /// Gets a bootstrap configuration view that includes the resolved profile overlay.
    /// </summary>
    public IConfigurationRoot Configuration { get; }
}
