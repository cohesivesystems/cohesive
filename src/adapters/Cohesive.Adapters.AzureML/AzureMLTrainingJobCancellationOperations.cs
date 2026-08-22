using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.MachineLearning;
using Cohesive.AI.Training;

namespace Cohesive.Adapters.AzureML;

internal interface IAzureMLTrainingJobCancellationOperations
{
    ValueTask<TrainingJobState?> ObserveAsync(string jobId, CancellationToken ct);

    ValueTask RequestCancellationAsync(string jobId, CancellationToken ct);
}

sealed class AzureMLTrainingJobCancellationOperations(
    ArmClient armClient,
    AzureMLModelTrainerOptions options) : IAzureMLTrainingJobCancellationOperations
{
    readonly ArmClient armClient = armClient ?? throw new ArgumentNullException(nameof(armClient));
    readonly AzureMLModelTrainerOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<TrainingJobState?> ObserveAsync(
        string jobId,
        CancellationToken ct)
    {
        var existing = await GetWorkspaceResource()
            .GetMachineLearningJobs()
            .GetIfExistsAsync(jobId, ct)
            .ConfigureAwait(false);
        return existing.HasValue
            ? AzureMLModelTrainer.CreateTrainingJobState(jobId, existing.Value!.Data.Properties)
            : null;
    }

    public async ValueTask RequestCancellationAsync(
        string jobId,
        CancellationToken ct)
    {
        var resourceId = MachineLearningJobResource.CreateResourceIdentifier(
            options.SubscriptionId,
            options.ResourceGroupName,
            options.WorkspaceName,
            jobId);
        var job = armClient.GetMachineLearningJobResource(resourceId);
        _ = await job.CancelAsync(WaitUntil.Started, ct).ConfigureAwait(false);
    }

    MachineLearningWorkspaceResource GetWorkspaceResource() => AzureMLResourceLocator.Workspace(
        armClient,
        options.SubscriptionId,
        options.ResourceGroupName,
        options.WorkspaceName);
}
