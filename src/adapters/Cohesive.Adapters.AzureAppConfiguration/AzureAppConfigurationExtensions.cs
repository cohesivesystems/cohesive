using Azure.Core;
using Azure.Identity;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Hosting;

namespace Cohesive.Adapters.AzureAppConfiguration;

/// <summary>Defines Azure app configuration bootstrap options.</summary>
public sealed class AzureAppConfigurationBootstrapOptions
{
    /// <summary>Gets the section path.</summary>
    public required string SectionPath { get; init; }

    /// <summary>Gets the default endpoint configuration key.</summary>
    public string? DefaultEndpointConfigurationKey { get; init; }

    /// <summary>Gets the skip local environment when unspecified.</summary>
    public bool SkipLocalEnvironmentWhenUnspecified { get; init; }
    
    /// <summary>Gets or sets whether the default Azure credential is used.</summary>
    public bool UseDefaultCredential { get; init; } = true;
    
    /// <summary>Gets credential.</summary>
    public TokenCredential GetCredential() => 
        UseDefaultCredential ? new DefaultAzureCredential() : throw new InvalidOperationException("Azure App Configuration is not configured."); 
}

/// <summary>Defines azure app configuration registration options.</summary>
public sealed class AzureAppConfigurationRegistrationOptions
{
    /// <summary>Gets or sets whether this registration is enabled.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Gets the endpoint.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets the endpoint setting.</summary>
    public string? EndpointSetting { get; init; }

    /// <summary>Gets or sets whether an unavailable configuration endpoint is tolerated.</summary>
    public bool Optional { get; init; } = true;

    /// <summary>
    /// Optional labels to filter by.
    /// The substring {hostEnvironment} will be replaced with the current host environment name.
    /// </summary>
    public string[] Labels { get; init; } = [];
}

/// <summary>Provides operations for azure app configuration extensions.</summary>
public static class AzureAppConfigurationExtensions
{
    /// <summary>Adds configured azure app configuration.</summary>
    public static bool AddConfiguredAzureAppConfiguration(this IConfigurationBuilder builder, IConfiguration configuration, IHostEnvironment environment, AzureAppConfigurationBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SectionPath);

        var settings = configuration.GetSection(options.SectionPath).Get<AzureAppConfigurationRegistrationOptions>() ?? new();
        if (environment.IsLocal() && settings.Enabled is null && options.SkipLocalEnvironmentWhenUnspecified)
            return false;

        var endpoint = configuration.ResolveConfiguredValue(settings.Endpoint, settings.EndpointSetting)
            ?? configuration.ResolveConfiguredValue(directValue: null, configurationKey: options.DefaultEndpointConfigurationKey);

        var enabled = settings.Enabled ?? !string.IsNullOrWhiteSpace(endpoint);
        if (!enabled)
            return false;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            if (settings.Optional)
                return false;
            
            throw new InvalidOperationException("Azure App Configuration is enabled but no endpoint was configured.");
        }

        var credential = options.GetCredential();
        var labels = ResolveLabels(settings.Labels, environment);

        builder.AddAzureAppConfiguration(config =>
        {
            config.Connect(endpoint: new(endpoint, UriKind.Absolute), credential).ConfigureKeyVault(keyVault => keyVault.SetCredential(credential));
            config.Select(keyFilter: KeyFilter.Any, labelFilter: LabelFilter.Null);
            foreach (var label in labels)
            {
                if (!string.IsNullOrWhiteSpace(label))
                    config.Select(keyFilter: KeyFilter.Any, labelFilter: label);
            }
        }, optional: settings.Optional);

        return true;
    }

    static IReadOnlyList<string> ResolveLabels(IReadOnlyList<string> configuredLabels, IHostEnvironment environment)
    {
        List<string> resolved = [];
        foreach (var configuredLabel in configuredLabels)
        {
            if (configuredLabel is null)
                continue;

            var label = configuredLabel.Replace("{hostEnvironment}", environment.EnvironmentName, StringComparison.OrdinalIgnoreCase);
            resolved.Add(label);
        }

        if (resolved.Count == 0)
            resolved.Add(environment.EnvironmentName);

        return resolved;
    }
}
