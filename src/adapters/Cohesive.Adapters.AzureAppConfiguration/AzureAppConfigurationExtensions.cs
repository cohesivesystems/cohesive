using Azure.Core;
using Azure.Identity;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Hosting;

namespace Cohesive.Adapters.AzureAppConfiguration;

public sealed class AzureAppConfigurationBootstrapOptions
{
    public required string SectionPath { get; init; }

    public string? DefaultEndpointConfigurationKey { get; init; }

    public bool SkipLocalEnvironmentWhenUnspecified { get; init; }
    
    public bool UseDefaultCredential { get; init; } = true;
    
    public TokenCredential GetCredential() => 
        UseDefaultCredential ? new DefaultAzureCredential() : throw new InvalidOperationException("Azure App Configuration is not configured."); 
}

public sealed class AzureAppConfigurationRegistrationOptions
{
    public bool? Enabled { get; init; }

    public string? Endpoint { get; init; }

    public string? EndpointSetting { get; init; }

    public bool Optional { get; init; } = true;

    /// <summary>
    /// Optional labels to filter by.
    /// The substring {hostEnvironment} will be replaced with the current host environment name.
    /// </summary>
    public string[] Labels { get; init; } = [];
}

public static class AzureAppConfigurationExtensions
{
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
