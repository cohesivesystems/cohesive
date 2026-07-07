namespace Cohesive.Adapters.AzureML;

/// <summary>
/// Coordinates used when registering dataset assets in Azure Machine Learning.
/// </summary>
public sealed record AzureMLDatasetRegistryOptions(
    string SubscriptionId,
    string ResourceGroupName,
    string WorkspaceName,
    string? RegistryName = null
    )
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResourceGroupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceName);
    }
}
