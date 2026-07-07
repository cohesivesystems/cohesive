using DurableTask.AzureStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.DurableTask.AzureStorage;

/// <summary>
/// Registration builder for an Azure Storage-backed durable worker.
/// </summary>
public sealed class AzureStorageDurableTaskWorkerBuilder
{
    readonly List<Action<IServiceProvider, ProcessRuntimeServices>> runtimeConfigurators = [];
    readonly List<Action<IServiceProvider, AzureStorageOrchestrationServiceSettings>> azureStorageConfigurators = [];

    /// <summary>
    /// Friendly host name used for start/stop logging.
    /// </summary>
    public string? HostName { get; private set; }

    /// <summary>
    /// Optional process-definition registry override.
    /// </summary>
    internal DurableTaskProcessDefinitionRegistry? Definitions { get; private set; }

    /// <summary>
    /// Optional durable process options override.
    /// </summary>
    public DurableTaskProcessOptions? ProcessOptions { get; private set; }

    /// <summary>
    /// Optional runtime factory override. When unspecified, an in-memory runtime is created.
    /// </summary>
    public Func<IServiceProvider, ProcessRuntimeServices>? RuntimeFactory { get; private set; }

    /// <summary>
    /// Sets a friendly host name for logging.
    /// </summary>
    public AzureStorageDurableTaskWorkerBuilder WithHostName(string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        HostName = hostName;
        return this;
    }

    /// <summary>
    /// Sets the task hub name directly.
    /// </summary>
    public AzureStorageDurableTaskWorkerBuilder WithTaskHubName(string taskHubName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskHubName);
        azureStorageConfigurators.Add((_, settings) => settings.TaskHubName = taskHubName);
        return this;
    }

    /// <summary>
    /// Overrides durable process options.
    /// </summary>
    public AzureStorageDurableTaskWorkerBuilder WithProcessOptions(DurableTaskProcessOptions options)
    {
        ProcessOptions = Guard.RequireNotNull(options);
        return this;
    }

    /// <summary>
    /// Overrides the process-definition registry.
    /// </summary>
    public AzureStorageDurableTaskWorkerBuilder WithDefinitions(DurableTaskProcessDefinitionRegistry definitions)
    {
        Definitions = Guard.RequireNotNull(definitions);
        return this;
    }

    /// <summary>
    /// Overrides the runtime factory used to create the underlying process runtime.
    /// </summary>
    public AzureStorageDurableTaskWorkerBuilder UseRuntimeFactory(Func<IServiceProvider, ProcessRuntimeServices> runtimeFactory)
    {
        RuntimeFactory = Guard.RequireNotNull(runtimeFactory);
        return this;
    }

    /// <summary>
    /// Configures the underlying process runtime, including handler registration.
    /// </summary>
    public AzureStorageDurableTaskWorkerBuilder ConfigureRuntime(Action<IServiceProvider, ProcessRuntimeServices> configure)
    {
        runtimeConfigurators.Add(Guard.RequireNotNull(configure));
        return this;
    }

    /// <summary>
    /// Configures the Azure Storage orchestration service settings.
    /// </summary>
    public AzureStorageDurableTaskWorkerBuilder ConfigureAzureStorage(Action<IServiceProvider, AzureStorageOrchestrationServiceSettings> configure)
    {
        azureStorageConfigurators.Add(Guard.RequireNotNull(configure));
        return this;
    }

    internal DurableTaskProcessHost Build(IServiceProvider sp)
    {
        var runtime = (RuntimeFactory ?? CreateDefaultRuntime)(sp);
        foreach (var configureRuntime in runtimeConfigurators)
        {
            configureRuntime(sp, runtime);
        }

        var settings = new AzureStorageOrchestrationServiceSettings
        {
            LoggerFactory = runtime.LoggerFactory
        };
        
        foreach (var configureAzureStorage in azureStorageConfigurators)
        {
            configureAzureStorage(sp, settings);
        }

        var configuredHostName = string.IsNullOrWhiteSpace(HostName) ? "durable worker" : HostName;
        if (string.IsNullOrWhiteSpace(settings.TaskHubName))
        {
            throw new InvalidOperationException($"Azure Storage {configuredHostName} requires a configured task hub name.");
        }

        var hostName = string.IsNullOrWhiteSpace(HostName) ? settings.TaskHubName : HostName;
        if (settings.StorageAccountClientProvider is null)
        {
            throw new InvalidOperationException($"Azure Storage durable worker '{hostName}' requires a configured storage account client provider.");
        }

        return AzureStorageDurableTaskProcessHostFactory.Create(
            settings: settings,
            runtime: runtime,
            hostName: hostName,
            definitions: Definitions,
            options: ProcessOptions
            );
    }

    static ProcessRuntimeServices CreateDefaultRuntime(IServiceProvider serviceProvider) => new(
        transitionHost: new DeclarativeTransitionHost(),
        entityRepository: new InMemoryProcessStorageAdapter(),
        deadLetterSink: new InMemoryProcessDeadLetterSink(),
        operationContextScopeFactory: serviceProvider.GetService<IOperationContextScopeFactory>(),
        loggerFactory: serviceProvider.GetService<ILoggerFactory>()
        );
}
