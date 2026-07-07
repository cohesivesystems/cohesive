using DurableTask.AzureStorage;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.DurableTask.AzureStorage;

/// <summary>
/// Convenience helpers for hosting durable processes on Azure Storage-backed task hubs.
/// </summary>
public static class AzureStorageDurableTaskProcessHostFactory
{
    /// <summary>
    /// Creates a durable process host from Azure Storage settings.
    /// </summary>
    public static DurableTaskProcessHost Create(
        AzureStorageOrchestrationServiceSettings settings,
        ProcessRuntimeServices runtime,
        string? hostName = null,
        DurableTaskProcessDefinitionRegistry? definitions = null,
        DurableTaskProcessOptions? options = null
        )
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtime);
        var taskHubName = !string.IsNullOrWhiteSpace(settings.TaskHubName)
            ? settings.TaskHubName
            : throw new ArgumentException("Azure Storage durable host requires a configured task hub name.", nameof(settings));

        settings.LoggerFactory ??= runtime.LoggerFactory;
        
        return new(
            orchestrationService: new AzureStorageOrchestrationService(settings),
            taskHubName: taskHubName,
            runtime: runtime,
            hostName: hostName,
            definitions: definitions,
            options: options
            );
    }

    /// <summary>
    /// Creates a durable process host from an Azure Storage connection string and task hub name.
    /// </summary>
    public static DurableTaskProcessHost Create(
        string connectionString,
        string taskHubName,
        ProcessRuntimeServices runtime,
        string? hostName = null,
        DurableTaskProcessDefinitionRegistry? definitions = null,
        DurableTaskProcessOptions? options = null
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskHubName);

        return Create(
            settings: BuildSettings(
                connectionString: connectionString,
                taskHubName: taskHubName
                ),
            runtime: runtime,
            hostName: hostName,
            definitions: definitions,
            options: options
            );
    }

    static AzureStorageOrchestrationServiceSettings BuildSettings(
        string connectionString,
        string taskHubName
        )
    {
        var settings = new AzureStorageOrchestrationServiceSettings
        {
            TaskHubName = taskHubName,
            StorageAccountClientProvider = new(connectionString: connectionString)
        };
        
        return settings;
    }
}
