using Azure.ResourceManager;
using Azure.ResourceManager.MachineLearning;

namespace Cohesive.Adapters.AzureML;

static class AzureMLResourceLocator
{
    public static MachineLearningWorkspaceResource Workspace(
        ArmClient armClient,
        string subscriptionId,
        string resourceGroupName,
        string workspaceName)
    {
        ArgumentNullException.ThrowIfNull(armClient);
        var id = MachineLearningWorkspaceResource.CreateResourceIdentifier(
            subscriptionId,
            resourceGroupName,
            workspaceName);
        return armClient.GetMachineLearningWorkspaceResource(id);
    }
}
