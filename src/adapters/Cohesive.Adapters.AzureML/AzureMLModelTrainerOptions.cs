namespace Cohesive.Adapters.AzureML;

/// <summary>
/// Workspace coordinates used when issuing Azure Machine Learning jobs.
/// </summary>
public sealed record AzureMLModelTrainerOptions(
    string SubscriptionId,
    string ResourceGroupName,
    string WorkspaceName
    )
{
    /// <summary>Validates the value.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResourceGroupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceName);
    }
}
