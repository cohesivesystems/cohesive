using System.Diagnostics.CodeAnalysis;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace Cohesive.Adapters.AzureML;

/// <summary>
/// Configuration options for an Azure ML resource.
/// </summary>
public sealed record AzureMLOptions
{
    public const string DefaultName = "Default";
    
    /// <summary>
    /// The Azure subscription ID.
    /// </summary>
    public string? SubscriptionId { get; init; }

    public string? ResourceGroupName { get; init; }

    /// <summary>
    /// The name of the Azure ML workspace.
    /// </summary>
    public string? WorkspaceName { get; init; }

    /// <summary>
    /// The name of the Azure ML artifact registry (TODO: move to module config).
    /// </summary>
    public string? RegistryName { get; init; }

    public bool UseDefaultAzureCredential { get; init; } = true;

    public TokenCredential? GetTokenCredential() => 
        UseDefaultAzureCredential ? new DefaultAzureCredential() : null;
    
    [MemberNotNullWhen(true, nameof(SubscriptionId))]
    [MemberNotNullWhen(true, nameof(ResourceGroupName))]
    [MemberNotNullWhen(true, nameof(WorkspaceName))]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SubscriptionId) && !string.IsNullOrWhiteSpace(ResourceGroupName) && !string.IsNullOrWhiteSpace(WorkspaceName);
    
    AzureMLDatasetRegistryOptions? TryGetDatasetRegistryOptions() => 
        IsConfigured ? new(SubscriptionId: SubscriptionId, ResourceGroupName: ResourceGroupName, WorkspaceName: WorkspaceName, RegistryName: RegistryName) : null;
    
    public AzureMLDatasetRegistry GetRegistry()
    {
        var credential = GetTokenCredential() ?? throw new InvalidOperationException("Azure ML options are not configured.");
        var options = TryGetDatasetRegistryOptions() ?? throw new InvalidOperationException("Azure ML options are not configured.");
        return new(credential, options);
    }

    public AzureMLModelTrainer GetModelTrainer()
    {
        var credential = GetTokenCredential() ?? throw new InvalidOperationException("Azure ML options are not configured.");
        if (!IsConfigured)
            throw new InvalidOperationException("Azure ML options are not configured.");
        var options = new AzureMLModelTrainerOptions(SubscriptionId: SubscriptionId, ResourceGroupName: ResourceGroupName, WorkspaceName: WorkspaceName);
        return new(credential, options);
    }
}

public static class AzureMLOptionsExtensions
{
    static string? NormalizeAzureMLName(string? name) =>
        IsDefaultAzureMLName(name) ? AzureMLOptions.DefaultName : name;

    static bool IsDefaultAzureMLName(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || string.Equals(name, AzureMLOptions.DefaultName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, Options.DefaultName, StringComparison.Ordinal);

    public static KeyValuePair<string, AzureMLOptions> GetRequiredOptions(this IReadOnlyDictionary<string, AzureMLOptions>? optionsByName, string? name)
    {
        var normalizedName = NormalizeAzureMLName(name) ?? throw new InvalidOperationException("Azure ML profile name must be configured.");
        if (optionsByName is not null && optionsByName.TryGetValue(normalizedName, out var options) && options.IsConfigured)
            return new(normalizedName, options);

        throw new InvalidOperationException($"Azure ML profile '{normalizedName}' is not configured.");
    }
}
