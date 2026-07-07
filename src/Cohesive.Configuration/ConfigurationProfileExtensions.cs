using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cohesive.Configuration;

/// <summary>
/// Adds profile resolution and overlay composition helpers to configuration builders and managers.
/// </summary>
public static class ConfigurationProfileExtensions
{
    extension(IConfiguration configuration)
    {
        /// <summary>
        /// Resolves the active profile, inheritance chain, and flattened overlay values from a configuration tree.
        /// </summary>
        /// <param name="options">Optional profile resolution settings. When omitted, the default profile key names are used.</param>
        /// <returns>A resolution object containing the active profile, applied profile chain, and flattened values.</returns>
        public ConfigurationProfileResolution ResolveConfigurationProfile(ConfigurationProfileOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            options ??= new();
            var activeProfile = ResolveActiveProfile(configuration, options);
            var profilesSection = configuration.GetSection(options.ProfilesSectionPath);
            if (!profilesSection.Exists())
                return new(activeProfile: activeProfile, appliedProfiles: [], values: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

            var profileNames = ResolveProfiles(profilesSection, activeProfile, options.ExtendsKey);
            var appliedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var profileName in profileNames)
            {
                var profileSection = profilesSection.GetSection(profileName);
                if (!profileSection.Exists())
                    throw new InvalidOperationException($"Configuration profile '{profileName}' was not found under '{options.ProfilesSectionPath}'.");

                FlattenProfile(profileSection, options.ExtendsKey, appliedValues);
            }

            return new(activeProfile: activeProfile, appliedProfiles: profileNames, values: appliedValues);
        }

        /// <summary>
        /// Creates a read-only configuration view by overlaying a resolved profile onto an existing configuration tree.
        /// </summary>
        /// <param name="resolution">The resolved profile values to overlay.</param>
        /// <returns>A new configuration root that includes the base configuration followed by the profile overlay.</returns>
        public IConfigurationRoot CreateProfiledConfiguration(ConfigurationProfileResolution resolution)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(resolution);

            var builder = new ConfigurationBuilder().AddConfiguration(configuration);
            if (resolution.Values.Count > 0)
                builder.Add(new ConfigurationProfileSource(resolution));

            return builder.Build();
        }
    }

    /// <summary>
    /// Applies a configuration profile overlay to a live <see cref="IConfigurationManager"/>, optionally running bootstrap registration logic against a bootstrap-profiled view first.
    /// </summary>
    /// <param name="configuration">The live configuration manager that should receive the final profile overlay.</param>
    /// <param name="environment">The host environment associated with the resulting profile context.</param>
    /// <param name="options">Profile resolution settings.</param>
    /// <param name="configureBootstrap">
    /// Optional bootstrap callback invoked after an initial profile resolution. Use this to register providers that depend on profile-derived settings
    /// before the final profile resolution is performed and applied to the live configuration manager.
    /// </param>
    /// <returns>A context describing the environment, active profile, and applied profile chain after the final overlay is attached.</returns>
    public static ConfigurationProfileContext AddConfigurationProfile(this IConfigurationManager configuration, IHostEnvironment environment, ConfigurationProfileOptions? options, Action<ConfigurationProfileBootstrapContext>? configureBootstrap = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        
        if (configureBootstrap is not null)
        {
            var bootstrapResolution = configuration.ResolveConfigurationProfile(options);
            var bootstrapConfiguration = configuration.CreateProfiledConfiguration(bootstrapResolution);
            configureBootstrap(new(resolution: bootstrapResolution, configuration: bootstrapConfiguration));
        }

        var resolution = configuration.ResolveConfigurationProfile(options);
        if (resolution.Values.Count > 0)
            configuration.Add(new ConfigurationProfileSource(resolution));

        return resolution.CreateContext(environmentName: environment.EnvironmentName);
    }

    static string ResolveActiveProfile(IConfiguration configuration, ConfigurationProfileOptions options) => 
        configuration[options.ActiveProfileKey] ?? options.DefaultActiveProfile;

    static IReadOnlyList<string> ResolveProfiles(IConfigurationSection profilesSection, string activeProfile, string extendsKey)
    {
        ArgumentNullException.ThrowIfNull(profilesSection);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(extendsKey);

        List<string> ordered = [];
        HashSet<string> resolved = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> resolving = new(StringComparer.OrdinalIgnoreCase);

        ResolveProfile(activeProfile, profilesSection, extendsKey, ordered, resolved, resolving);
        return ordered;
    }

    static void ResolveProfile(
        string profileName,
        IConfigurationSection profilesSection,
        string extendsKey,
        List<string> ordered,
        HashSet<string> resolved,
        HashSet<string> resolving)
    {
        if (resolved.Contains(profileName))
            return;

        if (!resolving.Add(profileName))
            throw new InvalidOperationException($"Configuration profiles contain a cycle involving '{profileName}'.");

        var profileSection = profilesSection.GetSection(profileName);
        if (!profileSection.Exists())
            throw new InvalidOperationException($"Configuration profile '{profileName}' was not found.");

        var extendsSection = profileSection.GetSection(extendsKey);
        var bases = extendsSection.GetChildren()
            .Select(static child => child.Value)
            .WhereNotNullOrWhiteSpace()
            .ToList();

        if (!string.IsNullOrWhiteSpace(extendsSection.Value))
            bases.Add(extendsSection.Value);

        foreach (var baseProfile in bases)
            ResolveProfile(baseProfile, profilesSection, extendsKey, ordered, resolved, resolving);

        resolving.Remove(profileName);
        resolved.Add(profileName);
        ordered.Add(profileName);
    }

    static void FlattenProfile(IConfigurationSection profileSection, string extendsKey, IDictionary<string, string?> values)
    {
        foreach (var child in profileSection.GetChildren())
        {
            if (string.Equals(child.Key, extendsKey, StringComparison.OrdinalIgnoreCase))
                continue;

            FlattenSection(child, child.Key, values);
        }
    }

    static void FlattenSection(IConfigurationSection section, string path, IDictionary<string, string?> values)
    {
        var children = section.GetChildren().ToArray();
        if (children.Length == 0)
        {
            values[path] = section.Value;
            return;
        }

        foreach (var child in children)
            FlattenSection(child, $"{path}:{child.Key}", values);
    }
}
