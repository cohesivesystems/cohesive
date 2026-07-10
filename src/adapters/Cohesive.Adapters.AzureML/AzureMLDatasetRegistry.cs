using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.MachineLearning;
using Azure.ResourceManager.MachineLearning.Models;
using Cohesive.AI.Training;

namespace Cohesive.Adapters.AzureML;

/// <summary>
/// Registers materialized dataset assets in Azure Machine Learning workspaces or registries.
/// </summary>
public sealed class AzureMLDatasetRegistry : ITrainingDatasetRegistry
{
    readonly ArmClient armClient;
    readonly AzureMLDatasetRegistryOptions options;

    /// <summary>Initializes a new instance of the azure ml dataset registry type.</summary>
    public AzureMLDatasetRegistry(
        TokenCredential credential,
        AzureMLDatasetRegistryOptions options,
        ArmClientOptions? armClientOptions = null
        )
    {
        armClient = new(credential, defaultSubscriptionId: options.SubscriptionId, armClientOptions);
        this.options = options;
        this.options.Validate();
    }

    /// <summary>Registers a training dataset with Azure ML.</summary>
    public async ValueTask<TrainingDatasetRegistration> RegisterAsync(TrainingDatasetRegistrationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var dataType = request.IsFolder ? MachineLearningDataType.UriFolder : MachineLearningDataType.UriFile;
        var containerData = new MachineLearningDataContainerData(new(dataType: dataType));
        MachineLearningDataVersionProperties versionProperties = request.IsFolder
            ? new MachineLearningUriFolderDataVersion(dataUri: request.Location)
            : new MachineLearningUriFileDataVersion(dataUri: request.Location);

        if (request.Tags is { Count: > 0 })
        {
            foreach (var (key, value) in request.Tags)
            {
                containerData.Properties.Tags[key] = value;
                versionProperties.Tags[key] = value;
            }
        }

        if (string.IsNullOrWhiteSpace(options.RegistryName))
        {
            var workspace = GetWorkspaceResource();
            var container = await workspace
                .GetMachineLearningDataContainers()
                .CreateOrUpdateAsync(WaitUntil.Completed, name: request.AssetName, containerData, ct)
                .ConfigureAwait(false);
            var version = await container.Value
                .GetMachineLearningDataVersions()
                .CreateOrUpdateAsync(WaitUntil.Completed, version: request.AssetVersion, new(versionProperties), ct)
                .ConfigureAwait(false);
            return new(
                AssetName: request.AssetName,
                AssetVersion: request.AssetVersion,
                AssetUri: ToAzureMLUri(version.Value.Id)
                );
        }

        var registry = GetRegistryResource(options.RegistryName!);
        
        var registryContainer = await registry
            .GetMachineLearningRegistryDataContainers()
            .CreateOrUpdateAsync(WaitUntil.Completed, name: request.AssetName, data: containerData, ct)
            .ConfigureAwait(false);

        var registryVersion = await registryContainer.Value
            .GetMachineLearningRegistryDataVersions()
            .CreateOrUpdateAsync(WaitUntil.Completed, version: request.AssetVersion, data: new(versionProperties), ct)
            .ConfigureAwait(false);

        return new(
            AssetName: request.AssetName,
            AssetVersion: request.AssetVersion,
            AssetUri: ToAzureMLUri(registryVersion.Value.Id)
            );
    }

    MachineLearningWorkspaceResource GetWorkspaceResource()
    {
        var id = MachineLearningWorkspaceResource.CreateResourceIdentifier(
            options.SubscriptionId,
            options.ResourceGroupName,
            options.WorkspaceName
            );
        return armClient.GetMachineLearningWorkspaceResource(id);
    }

    MachineLearningRegistryResource GetRegistryResource(string registryName)
    {
        var id = MachineLearningRegistryResource.CreateResourceIdentifier(
            options.SubscriptionId,
            options.ResourceGroupName,
            registryName
            );
        return armClient.GetMachineLearningRegistryResource(id);
    }

    static Uri ToAzureMLUri(ResourceIdentifier resourceIdentifier) =>
        new($"azureml:{resourceIdentifier}", UriKind.Absolute);
}
