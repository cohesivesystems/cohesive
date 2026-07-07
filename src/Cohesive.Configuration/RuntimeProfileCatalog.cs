namespace Cohesive.Configuration;

public sealed record RuntimeHostContext<TSettings>(
    ConfigurationProfileContext Profile,
    TSettings Settings
);

/// <summary>
/// Describes the typed runtime settings produced by resolving a built-in runtime profile.
/// </summary>
public sealed record RuntimeProfileResolution<TSettings>(
    ConfigurationProfileContext Context,
    TSettings Settings
);

public sealed record RuntimeProfileDefinition<TSettings>(
    string Name,
    IReadOnlyList<string> Extends,
    Func<TSettings, TSettings> Apply
);

/// <summary>
/// A catalog of built-in runtime profiles.
/// </summary>
/// <typeparam name="TSettings">The type of settings object produced by the runtime profile.</typeparam>
public class RuntimeProfileCatalog<TSettings>(
    IReadOnlyDictionary<string, RuntimeProfileDefinition<TSettings>> profiles
    )
{
    List<RuntimeProfileDefinition<TSettings>> ResolveProfile(string profileName)
    {
        List<RuntimeProfileDefinition<TSettings>> ordered = [];
        HashSet<string> resolved = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> resolving = new(StringComparer.OrdinalIgnoreCase);
        ResolveProfile(profileName, ordered, resolved, resolving);
        return ordered;
    }

    void ResolveProfile(string profileName, List<RuntimeProfileDefinition<TSettings>> ordered, HashSet<string> resolved, HashSet<string> resolving)
    {
        if (resolved.Contains(profileName))
            return;

        if (!profiles.TryGetValue(profileName, out var profile))
            throw new InvalidOperationException($"Runtime profile '{profileName}' was not found.");

        if (!resolving.Add(profile.Name))
            throw new InvalidOperationException($"Runtime profiles contain a cycle involving '{profile.Name}'.");

        foreach (var baseProfile in profile.Extends)
            ResolveProfile(baseProfile, ordered, resolved, resolving);

        resolving.Remove(profile.Name);
        resolved.Add(profile.Name);
        ordered.Add(profile);
    }
    
    public RuntimeProfileResolution<TSettings> Resolve(ConfigurationProfileContext context, string defaultProfile, Func<string, TSettings> settingsFactory) =>
        Resolve(context.ActiveProfile, defaultProfile, context.EnvironmentName, settingsFactory);
    
    /// <summary>
    /// Resolves a built-in runtime profile by name.
    /// </summary>
    public RuntimeProfileResolution<TSettings> Resolve(string? activeProfile, string defaultProfile, string environmentName, Func<string, TSettings> settingsFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        var profileName = string.IsNullOrWhiteSpace(activeProfile) ? defaultProfile : activeProfile;
        var ordered = ResolveProfile(profileName);
        var settings = settingsFactory(environmentName);
        foreach (var profile in ordered)
            settings = profile.Apply(settings);

        return new(
            Context: new(
                EnvironmentName: environmentName,
                ActiveProfile: ordered[^1].Name,
                AppliedProfiles: [..ordered.Select(static profile => profile.Name)]
            ),
            Settings: settings
        );
    }

    /// <summary>
    /// Attempts to resolve a built-in runtime profile by name.
    /// </summary>
    public bool TryResolve(string? activeProfile, string defaultProfile, string environmentName, Func<string, TSettings> settingsFactory, out RuntimeProfileResolution<TSettings>? resolution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        var profileName = string.IsNullOrWhiteSpace(activeProfile) ? defaultProfile : activeProfile.Trim();
        if (!profiles.ContainsKey(profileName))
        {
            resolution = null;
            return false;
        }

        resolution = Resolve(activeProfile: profileName, defaultProfile: defaultProfile, environmentName: environmentName, settingsFactory);
        return true;
    }
}

public class RuntimeProfileCatalogBuilder<TSettings>
{
    readonly Dictionary<string, RuntimeProfileDefinition<TSettings>> profiles = [];

    public RuntimeProfileCatalog<TSettings> Build() => new(profiles);
    
    public RuntimeProfileCatalogBuilder<TSettings> Add(RuntimeProfileDefinition<TSettings> profile)
    {
        if (!profiles.TryAdd(profile.Name, profile))
            throw new InvalidOperationException($"Duplicate runtime profile '{profile.Name}'.");
        
        return this;
    }
}
