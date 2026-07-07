using DurableTask.Core;
using DurableTask.Core.Query;
using DurableTask.Core.Serializing;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Task-hub host for Durable Task-backed process execution.
/// </summary>
public sealed class DurableTaskProcessHost : IAsyncDisposable
{
    readonly IOrchestrationService orchestrationService;
    readonly TaskHubWorker worker;
    readonly ILogger<DurableTaskProcessHost> logger;

    /// <summary>
    /// Creates a durable process host for a task hub.
    /// </summary>
    internal DurableTaskProcessHost(
        IOrchestrationService orchestrationService,
        string taskHubName,
        ProcessRuntimeServices runtime,
        string? hostName = null,
        DurableTaskProcessDefinitionRegistry? definitions = null,
        DurableTaskProcessOptions? options = null,
        DataConverter? dataConverter = null
        )
    {
        this.orchestrationService = Guard.RequireNotNull(orchestrationService);
        Runtime = Guard.RequireNotNull(runtime);
        TaskHubName = Guard.RequireNotNullOrWhiteSpace(taskHubName);
        HostName = string.IsNullOrWhiteSpace(hostName) ? taskHubName : hostName;
        ExecutionPlanner = new(Runtime);
        NodeExecutor = new(Runtime, ExecutionPlanner);
        Definitions = definitions ?? new DurableTaskProcessDefinitionRegistry();
        Options = options ?? new DurableTaskProcessOptions();
        DataConverter = dataConverter ?? DurableTaskProcessSerialization.CreateDataConverter();
        var resolvedLoggerFactory = Runtime.LoggerFactory;
        logger = resolvedLoggerFactory.CreateLogger<DurableTaskProcessHost>();

        if (orchestrationService is not IOrchestrationServiceClient clientService)
            throw new ArgumentException("The supplied orchestration service must also implement IOrchestrationServiceClient.", nameof(orchestrationService));

        TaskHubClient = new(clientService, DataConverter, resolvedLoggerFactory);
        Engine = new(
            client: new DurableTaskProcessHubClient(TaskHubClient),
            definitions: Definitions,
            options: Options,
            dataConverter: DataConverter,
            operationContextScopeFactory: Runtime.OperationContextScopeFactory,
            loggerFactory: resolvedLoggerFactory
        );
        ProcessExecutionRepository = orchestrationService is IOrchestrationServiceQueryClient queryClient
            ? new DurableTaskProcessExecutionRepository(queryClient, TaskHubName, DataConverter)
            : throw new ArgumentException("The supplied orchestration service must implement IOrchestrationServiceQueryClient.", nameof(orchestrationService));
        
        worker = new(orchestrationService, resolvedLoggerFactory);
        worker.ErrorPropagationMode = ErrorPropagationMode.UseFailureDetails;
        worker.AddTaskOrchestrations([
            new NameValueObjectCreator<TaskOrchestration>(
                name: Options.OrchestrationName,
                version: Options.OrchestrationVersion,
                instance: new DurableTaskProcessOrchestration(
                    planner: ExecutionPlanner,
                    definitions: Definitions,
                    dataConverter: DataConverter,
                    options: Options,
                    logger: resolvedLoggerFactory.CreateLogger<DurableTaskProcessOrchestration>()
                )
            )
        ]);
        worker.AddTaskActivities([
            new NameValueObjectCreator<TaskActivity>(
                name: Options.ActivityName,
                version: Options.ActivityVersion,
                instance: new DurableTaskExecuteProcessNodeActivity(
                    nodeExecutor: NodeExecutor,
                    definitions: Definitions,
                    dataConverter: DataConverter,
                    logger: resolvedLoggerFactory.CreateLogger<DurableTaskExecuteProcessNodeActivity>()
                )
            )
        ]);
    }

    /// <summary>
    /// Friendly host name used for start/stop logging.
    /// </summary>
    public string HostName { get; }

    /// <summary>
    /// Durable Task hub name.
    /// </summary>
    public string TaskHubName { get; }

    /// <summary>
    /// Shared process runtime used by the orchestration and node activity.
    /// </summary>
    public ProcessRuntimeServices Runtime { get; }

    /// <summary>
    /// Durable process planner used by the orchestration.
    /// </summary>
    public ProcessExecutionPlanner ExecutionPlanner { get; }

    /// <summary>
    /// Node executor used by durable activities.
    /// </summary>
    public ProcessNodeExecutor NodeExecutor { get; }
    
    /// <summary>
    /// Local process-definition registry.
    /// </summary>
    internal DurableTaskProcessDefinitionRegistry Definitions { get; }

    /// <summary>
    /// Durable adapter options.
    /// </summary>
    public DurableTaskProcessOptions Options { get; }

    /// <summary>
    /// Data converter used for orchestration and activity payloads.
    /// </summary>
    public DataConverter DataConverter { get; }

    /// <summary>
    /// Underlying task-hub client.
    /// </summary>
    public TaskHubClient TaskHubClient { get; }

    /// <summary>
    /// Process engine backed by this host's task hub.
    /// </summary>
    public DurableTaskProcessEngine Engine { get; }

    /// <summary>
    /// Process execution repository backed by this host's task hub.
    /// </summary>
    public IProcessExecutionRepository ProcessExecutionRepository { get; }

    /// <summary>
    /// Creates task-hub resources.
    /// </summary>
    public Task CreateAsync() => orchestrationService.CreateAsync();

    /// <summary>
    /// Creates task-hub resources if they do not already exist.
    /// </summary>
    public async Task CreateIfNotExistsAsync()
    {
        logger.LogInformation(
            "Ensuring durable task hub '{TaskHubName}' exists for host '{HostName}'.",
            TaskHubName,
            HostName);
        await orchestrationService.CreateIfNotExistsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes task-hub resources.
    /// </summary>
    public async Task DeleteAsync()
    {
        logger.LogInformation(
            "Deleting durable task hub '{TaskHubName}' for host '{HostName}'.",
            TaskHubName,
            HostName
            );
        await orchestrationService.DeleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the Durable Task worker.
    /// </summary>
    public async Task StartAsync()
    {
        logger.LogInformation(
            "Starting durable task host '{HostName}' for hub '{TaskHubName}'.",
            HostName,
            TaskHubName
            );
        await worker.StartAsync();
        worker.TaskActivityDispatcher.IncludeDetails = true;
        worker.TaskOrchestrationDispatcher.IncludeDetails = true;
    }

    /// <summary>
    /// Stops the Durable Task worker.
    /// </summary>
    public async Task StopAsync()
    {
        logger.LogInformation(
            "Stopping durable task host '{HostName}' for hub '{TaskHubName}'.",
            HostName,
            TaskHubName
            );
        await worker.StopAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await worker.StopAsync().ConfigureAwait(false);
            worker.Dispose();
        }
        catch (ObjectDisposedException)
        {
            logger.LogWarning("Durable Task worker was already disposed.");
        }
    }
}
