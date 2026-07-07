using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Execution options for the process engine.
/// </summary>
public sealed record ProcessEngineOptions
{
    /// <summary>
    /// Maximum effect handler attempts before dead-lettering.
    /// </summary>
    public int MaxEffectAttempts { get; init; } = 3;

    /// <summary>
    /// Initial delay for effect retries.
    /// </summary>
    public TimeSpan EffectRetryInitialDelay { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Default place name used when runs do not provide one.
    /// </summary>
    public string DefaultPlaceName { get; init; } = "default";

    /// <summary>
    /// Stale continuation snapshot behavior.
    /// </summary>
    public StaleContinuationPolicy StaleContinuationPolicy { get; init; } = StaleContinuationPolicy.Fail;
}

/// <summary>
/// Dispatches effect requests, executes handlers, and orchestrates process node execution.
/// </summary>
public sealed class ProcessEngine : IProcessEngine
{
    sealed class LocalProcessExecution(
        string processId,
        string processName,
        DateTimeOffset startedAtUtc,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        readonly Lock gate = new();
        ProcessExecutionState state = new(
            ProcessId: processId,
            ProcessName: processName,
            Status: ProcessExecutionStatus.Running,
            StartedAtUtc: startedAtUtc,
            UpdatedAtUtc: startedAtUtc,
            CompletedAtUtc: null)
        {
            Parameters = parameters
        };

        public Task<ProcessRunResult> CompletionTask { get; private set; } =
            Task.FromException<ProcessRunResult>(new InvalidOperationException("Process execution task was not initialized."));

        public ProcessExecutionState Snapshot
        {
            get
            {
                lock (gate)
                    return state;
            }
        }

        internal void Attach(Task<ProcessRunResult> completionTask)
        {
            ArgumentNullException.ThrowIfNull(completionTask);
            CompletionTask = completionTask;
        }

        internal void MarkCompleted(DateTimeOffset completedAtUtc, object? output)
        {
            lock (gate)
            {
                state = state with
                {
                    Status = ProcessExecutionStatus.Completed,
                    UpdatedAtUtc = completedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    Output = output
                };
            }
        }

        internal void MarkFailed(DateTimeOffset failedAtUtc, Exception error)
        {
            lock (gate)
            {
                state = state with
                {
                    Status = ProcessExecutionStatus.Failed,
                    UpdatedAtUtc = failedAtUtc,
                    CompletedAtUtc = failedAtUtc,
                    Error = CreateError(error),
                    FailureMessage = error.Message
                };
            }
        }

        internal void MarkCancelled(DateTimeOffset cancelledAtUtc)
        {
            lock (gate)
            {
                state = state with
                {
                    Status = ProcessExecutionStatus.Cancelled,
                    UpdatedAtUtc = cancelledAtUtc,
                    CompletedAtUtc = cancelledAtUtc
                };
            }
        }

        static ProcessExecutionError CreateError(Exception error) => new(
            ErrorType: error.GetType().FullName,
            ErrorMessage: error.Message,
            StackTrace: error.StackTrace,
            InnerError: error.InnerException is null ? null : CreateError(error.InnerException)
            );
    }

    readonly ConcurrentDictionary<string, LocalProcessExecution> executionsByProcessId = new(StringComparer.Ordinal);
    readonly ProcessRuntimeServices runtimeServices;
    readonly ProcessExecutionPlanner executionPlanner;
    readonly ProcessNodeExecutor nodeExecutor;
    readonly ILogger<ProcessEngine> logger;

    /// <summary>
    /// Creates a process engine over a pre-built runtime composition.
    /// </summary>
    public ProcessEngine(ProcessRuntimeServices runtimeServices)
    {
        this.runtimeServices = Guard.RequireNotNull(runtimeServices);
        executionPlanner = new(runtimeServices);
        nodeExecutor = new(runtimeServices, executionPlanner);
        logger = runtimeServices.LoggerFactory.CreateLogger<ProcessEngine>();
    }

    /// <summary>
    /// Registers an execution place.
    /// </summary>
    /// <exception cref="SemanticRuleViolationException"></exception>
    public ProcessEngine RegisterPlace(ProcessPlace place)
    {
        runtimeServices.RegisterPlace(place);
        return this;
    }

    /// <summary>
    /// Registers a handler binding for effect-request dispatch.
    /// </summary>
    public ProcessEngine RegisterHandler(IEffectHandlerBinding binding)
    {
        runtimeServices.RegisterHandler(binding);
        return this;
    }

    /// <summary>
    /// Registers a typed effect handler using JSON payload deserialization.
    /// </summary>
    public ProcessEngine RegisterHandler<TRequest, TResult>(IEffectHandler<TRequest, TResult> handler)
        where TRequest : IEffectRequest<TResult> 
    {
        runtimeServices.RegisterHandler(handler);
        return this;
    }

    /// <inheritdoc />
    public Task<ProcessStartResult> StartAsync(OperationContext context, ProcessDefinition process, IReadOnlyDictionary<string, object?>? parameters = null, ProcessRunOptions? runOptions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(process);
        context.ThrowIfCancellationRequested();

        using var _ = runtimeServices.PushOperationContext(context);

        var processId = string.IsNullOrWhiteSpace(runOptions?.ProcessId)
            ? Guid.NewGuid().ToString("N")
            : runOptions.ProcessId;

        runOptions = runOptions is null
            ? new() { ProcessId = processId }
            : runOptions with { ProcessId = processId };

        var startedAtUtc = context.UtcNow;
        var execution = new LocalProcessExecution(processId!, process.Name, startedAtUtc, parameters);
        if (!executionsByProcessId.TryAdd(processId!, execution))
            throw new SemanticRuleViolationException($"A process execution with id '{processId}' is already tracked by this executor instance.");

        logger.LogInformation(
            "Starting in-memory process '{ProcessName}' ({ProcessId}).",
            process.Name,
            processId);

        var executionContext = context.WithCancellationToken(CancellationToken.None);
        execution.Attach(RunTrackedExecutionAsync(
            context: executionContext,
            execution: execution,
            process: process,
            parameters: parameters,
            runOptions: runOptions)
        );

        return Task.FromResult(new ProcessStartResult(
            ProcessId: processId!,
            ProcessName: process.Name,
            StartedAtUtc: startedAtUtc
            )
        );
    }

    /// <inheritdoc />
    public async Task<ProcessExecutionState?> GetStatusAsync(OperationContext context, string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.CancellationToken.ThrowIfCancellationRequested();

        using var _ = runtimeServices.PushOperationContext(context);

        ProcessExecutionState? tracked = null;
        if (executionsByProcessId.TryGetValue(processId, out var execution))
            tracked = execution.Snapshot;

        var checkpointRepository = runtimeServices.TryGetCheckpointRepository();
        if (checkpointRepository is null)
        {
            logger.LogDebug(
                "Retrieved in-memory status for process '{ProcessId}': {Status}.",
                processId,
                tracked?.Status);
            return tracked;
        }

        var checkpoint = await checkpointRepository
            .LoadCheckpointAsync(context, processId)
            .ConfigureAwait(false);

        if (checkpoint is null)
            return tracked;

        var fromCheckpoint = new ProcessExecutionState(
            ProcessId: checkpoint.ProcessId,
            ProcessName: checkpoint.ProcessName,
            Status: checkpoint.Status,
            StartedAtUtc: checkpoint.StartedAtUtc,
            UpdatedAtUtc: checkpoint.UpdatedAtUtc,
            CompletedAtUtc: checkpoint.CompletedAtUtc
            )
        {
            Parameters = checkpoint.Parameters
        };

        if (tracked is null)
        {
            logger.LogDebug(
                "Retrieved checkpoint status for process '{ProcessId}': {Status}.",
                processId,
                fromCheckpoint.Status);
            return fromCheckpoint;
        }

        var resolved = tracked.Status is ProcessExecutionStatus.Running && fromCheckpoint.Status is ProcessExecutionStatus.Waiting
            ? fromCheckpoint
            : tracked;

        logger.LogDebug(
            "Resolved status for process '{ProcessId}' to {Status}.",
            processId,
            resolved.Status);

        return resolved;
    }

    /// <inheritdoc />
    public Task SignalAsync(OperationContext context, string processId, string signalKey, object? payload = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);
        context.CancellationToken.ThrowIfCancellationRequested();

        using var _ = runtimeServices.PushOperationContext(context);

        logger.LogInformation(
            "Publishing signal '{SignalKey}' to in-memory process '{ProcessId}'.",
            signalKey,
            processId);

        return runtimeServices
            .RequireSignalSink($"publish signal '{signalKey}'")
            .PublishAsync(context, signalKey, payload);
    }

    /// <inheritdoc />
    public async Task<ProcessRunResult> WaitForCompletionAsync(OperationContext context, string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.CancellationToken.ThrowIfCancellationRequested();

        using var _ = runtimeServices.PushOperationContext(context);

        if (!executionsByProcessId.TryGetValue(processId, out var execution))
        {
            throw new InvalidOperationException(
                $"Process execution '{processId}' is not tracked by this executor instance.");
        }

        logger.LogInformation(
            "Waiting for in-memory process '{ProcessId}' to complete.",
            processId);

        var result = await execution.CompletionTask.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "In-memory process '{ProcessId}' completed at place '{FinalPlace}'.",
            processId,
            result.FinalPlace);
        return result;
    }

    /// <summary>
    /// Executes a process definition with durable orchestration semantics.
    /// </summary>
    public async Task<ProcessRunResult> ExecuteAsync(OperationContext context, ProcessDefinition process, IReadOnlyDictionary<string, object?>? parameters = null, ProcessRunOptions? runOptions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(process);
        context.ThrowIfCancellationRequested();

        logger.LogInformation(
            "Executing in-memory process '{ProcessName}' to completion.",
            process.Name);

        try
        {
            var result = await nodeExecutor.ExecuteToCompletionAsync(context, process, parameters, runOptions).ConfigureAwait(false);
            logger.LogInformation(
                "In-memory process '{ProcessName}' ({ProcessId}) completed at place '{FinalPlace}'.",
                result.ProcessName,
                result.ProcessId,
                result.FinalPlace);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "In-memory process '{ProcessName}' failed during ExecuteAsync.",
                process.Name);
            throw;
        }
    }
    
    /// <summary>
    /// Creates an initial checkpoint for a process execution.
    /// </summary>
    public ProcessCheckpoint CreateCheckpoint(OperationContext context, ProcessDefinition process, IReadOnlyDictionary<string, object?>? parameters = null, ProcessRunOptions? runOptions = null) =>
        executionPlanner.CreateCheckpoint(context, process, parameters, runOptions);

    /// <summary>
    /// Plans the next durable execution step for the supplied checkpoint.
    /// </summary>
    public ProcessExecutionPlan PlanNextStep(OperationContext context, ProcessDefinition process, ProcessCheckpoint checkpoint) => 
        executionPlanner.PlanNextStep(context, process, checkpoint);

    /// <summary>
    /// Applies a wait payload to a checkpointed wait node.
    /// </summary>
    public ProcessCheckpoint ResumeWait(OperationContext context, ProcessDefinition process, ProcessCheckpoint checkpoint, object? resumePayload) => 
        executionPlanner.ResumeWait(context, process, checkpoint, resumePayload);

    async Task<ProcessRunResult> RunTrackedExecutionAsync(OperationContext context, LocalProcessExecution execution, ProcessDefinition process, IReadOnlyDictionary<string, object?>? parameters, ProcessRunOptions runOptions)
    {
        try
        {
            var result = await nodeExecutor.ExecuteToCompletionAsync(
                context: context,
                process: process,
                parameters: parameters,
                runOptions: runOptions
                ).ConfigureAwait(false);

            execution.MarkCompleted(context.UtcNow, result.Result);
            return result;
        }
        catch (OperationCanceledException)
        {
            execution.MarkCancelled(context.UtcNow);
            throw;
        }
        catch (Exception ex)
        {
            execution.MarkFailed(context.UtcNow, ex);
            throw;
        }
    }

}
