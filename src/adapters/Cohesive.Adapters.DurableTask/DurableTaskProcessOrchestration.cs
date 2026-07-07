using DurableTask.Core;
using DurableTask.Core.Serializing;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.DurableTask;

sealed class DurableTaskProcessOrchestration : TaskOrchestration<ProcessRunResult, DurableTaskProcessRequest, DurableTaskProcessSignal, DurableTaskProcessOrchestrationStatus>
{
    readonly ProcessExecutionPlanner planner;
    readonly DurableTaskProcessDefinitionRegistry definitions;
    readonly DurableTaskProcessOptions options;
    readonly ILogger<DurableTaskProcessOrchestration> logger;
    readonly Dictionary<string, Queue<object?>> bufferedSignals = new(StringComparer.Ordinal);
    readonly Dictionary<string, TaskCompletionSource<object?>> signalWaiters = new(StringComparer.Ordinal);

    DurableTaskProcessOrchestrationStatus currentStatus = new(string.Empty, ProcessExecutionStatus.Pending, null, string.Empty, null);

    public DurableTaskProcessOrchestration(
        ProcessExecutionPlanner planner,
        DurableTaskProcessDefinitionRegistry definitions,
        DataConverter dataConverter,
        DurableTaskProcessOptions options,
        ILogger<DurableTaskProcessOrchestration> logger
        )
    {
        this.planner = Guard.RequireNotNull(planner);
        this.definitions = Guard.RequireNotNull(definitions);
        DataConverter = Guard.RequireNotNull(dataConverter);
        this.options = Guard.RequireNotNull(options);
        this.logger = Guard.RequireNotNull(logger);
    }

    public override async Task<ProcessRunResult> RunTask(OrchestrationContext context, DurableTaskProcessRequest input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            LogReplaySafe(
                context,
                LogLevel.Information,
                "Starting durable process orchestration '{ProcessName}' ({ProcessId}).",
                input.ProcessName,
                input.RunOptions.ProcessId ?? context.OrchestrationInstance.InstanceId
                );
            
            if (DataConverter is JsonDataConverter jsonDataConverter)
            {
                context.MessageDataConverter = jsonDataConverter;
                context.ErrorDataConverter = jsonDataConverter;
            }

            var process = definitions.Get(processName: input.ProcessName);
            var operationContext = CreateOperationContext(context);
            var checkpoint = planner.CreateCheckpoint(
                context: operationContext,
                process: process,
                parameters: input.Parameters,
                runOptions: input.RunOptions
            );

            currentStatus = ToStatus(checkpoint, wait: null);

            while (true)
            {
                {
                    operationContext = CreateOperationContext(context);
                    var nextStepPlan = planner.PlanNextStep(operationContext, process, checkpoint);
                    checkpoint = nextStepPlan.Checkpoint;
                    LogReplaySafe(
                        context,
                        LogLevel.Debug,
                        "Durable process orchestration '{ProcessName}' ({ProcessId}) planned step '{StepKind}' at node '{NodeName}' in place '{Place}' with status '{Status}'.",
                        process.Name,
                        checkpoint.ProcessId,
                        nextStepPlan.Kind,
                        checkpoint.CurrentNode ?? "<none>",
                        checkpoint.CurrentPlace,
                        checkpoint.Status
                        );
                    currentStatus = ToStatus(checkpoint, nextStepPlan.Wait);

                    switch (nextStepPlan.Kind)
                    {
                        case ProcessExecutionPlanKind.Advance:
                            continue;

                        case ProcessExecutionPlanKind.ExecuteNode:
                            LogReplaySafe(
                                context,
                                LogLevel.Debug,
                                "Scheduling durable activity '{ActivityName}' for process '{ProcessName}' ({ProcessId}) at node '{NodeName}'.",
                                options.ActivityName,
                                process.Name,
                                checkpoint.ProcessId,
                                checkpoint.CurrentNode ?? "<none>");
                            checkpoint = await context
                                .ScheduleTask<ProcessCheckpoint>(
                                    options.ActivityName,
                                    options.ActivityVersion,
                                    new DurableTaskProcessNodeRequest(ProcessName: process.Name, checkpoint))
                                .ConfigureAwait(true);
                            if (checkpoint is null)
                            {
                                throw new InvalidOperationException(
                                    $"Durable activity '{options.ActivityName}' returned a null '{nameof(ProcessCheckpoint)}' " +
                                    $"for process '{process.Name}' ({input.RunOptions.ProcessId ?? checkpoint?.ProcessId ?? "<unknown>"}).");
                            }
                            LogReplaySafe(
                                context,
                                LogLevel.Debug,
                                "Durable activity '{ActivityName}' completed for process '{ProcessName}' ({ProcessId}); next node '{NodeName}', place '{Place}', status '{Status}'.",
                                options.ActivityName,
                                process.Name,
                                checkpoint.ProcessId,
                                checkpoint.CurrentNode ?? "<none>",
                                checkpoint.CurrentPlace,
                                checkpoint.Status
                                );
                            currentStatus = ToStatus(checkpoint, wait: null);
                            continue;

                        case ProcessExecutionPlanKind.Wait:
                        {
                            var wait = nextStepPlan.Wait ?? throw new InvalidOperationException("Wait plan did not include wait metadata.");
                            var payload = await WaitAsync(context, wait).ConfigureAwait(true);
                            operationContext = CreateOperationContext(context);
                            checkpoint = planner.ResumeWait(operationContext, process, checkpoint, payload);
                            currentStatus = ToStatus(checkpoint, wait: null);
                            continue;
                        }

                        case ProcessExecutionPlanKind.Complete:
                            LogReplaySafe(
                                context,
                                LogLevel.Information,
                                "Completed durable process orchestration '{ProcessName}' ({ProcessId}).",
                                process.Name,
                                checkpoint.ProcessId);
                            return planner.BuildRunResult(checkpoint);

                        default:
                            throw new ArgumentOutOfRangeException(nameof(nextStepPlan.Kind), nextStepPlan.Kind, "Unexpected process execution plan.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogReplaySafe(
                context,
                LogLevel.Error,
                ex,
                "Durable process orchestration '{ProcessName}' ({ProcessId}) failed.",
                input.ProcessName,
                input.RunOptions.ProcessId ?? context.OrchestrationInstance.InstanceId);
            throw;
        }
    }

    public override void OnEvent(OrchestrationContext context, string name, DurableTaskProcessSignal input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(input);

        if (!string.Equals(name, DurableTaskProcessEngine.SignalEventName, StringComparison.Ordinal))
            return;

        if (signalWaiters.Remove(input.Key, out var waiter))
        {
            waiter.TrySetResult(input.Payload);
            return;
        }

        if (!bufferedSignals.TryGetValue(input.Key, out var queue))
        {
            queue = [];
            bufferedSignals[input.Key] = queue;
        }

        queue.Enqueue(input.Payload);
    }

    public override DurableTaskProcessOrchestrationStatus OnGetStatus() => currentStatus;

    async Task<object?> WaitAsync(OrchestrationContext context, ProcessWaitRequest wait)
    {
        return wait.WaitType switch
        {
            ProcessWaitType.Timer => await WaitForTimerAsync(context, wait).ConfigureAwait(true),
            ProcessWaitType.ExternalEvent => await WaitForExternalSignalAsync(context, wait).ConfigureAwait(true),
            _ => throw new SemanticRuleViolationException($"Unsupported wait type '{wait.WaitType}'.")
        };
    }

    static async Task<ProcessTimerFired> WaitForTimerAsync(OrchestrationContext context, ProcessWaitRequest wait)
    {
        if (wait.Timeout is not { } delay || delay <= TimeSpan.Zero)
            return new(Key: wait.Key, FiredAtUtc: new(context.CurrentUtcDateTime, TimeSpan.Zero));

        var firedAtUtc = context.CurrentUtcDateTime.Add(delay);
        return await context
            .CreateTimer(firedAtUtc, new ProcessTimerFired(Key: wait.Key, FiredAtUtc: new(firedAtUtc, TimeSpan.Zero)))
            .ConfigureAwait(true);
    }

    async Task<object?> WaitForExternalSignalAsync(OrchestrationContext context, ProcessWaitRequest wait)
    {
        if (bufferedSignals.TryGetValue(wait.Key, out var queued) && queued.Count > 0)
        {
            var payload = queued.Dequeue();
            if (queued.Count == 0)
                bufferedSignals.Remove(wait.Key);

            return payload;
        }

        var signalTask = WaitForSignalAsync(wait.Key);
        if (wait.Timeout is not { } timeout || timeout <= TimeSpan.Zero)
            return await signalTask.ConfigureAwait(true);

        using var timeoutCts = new CancellationTokenSource();
        var timerTask = context.CreateTimer<object?>(
            context.CurrentUtcDateTime.Add(timeout),
            state: null,
            cancelToken: timeoutCts.Token);

        var completed = await Task.WhenAny(signalTask, timerTask).ConfigureAwait(true);
        if (completed == signalTask)
        {
            timeoutCts.Cancel();
            return await signalTask.ConfigureAwait(true);
        }

        signalWaiters.Remove(wait.Key);
        throw new TimeoutException($"Wait for external event '{wait.Key}' timed out after {timeout}.");
    }

    Task<object?> WaitForSignalAsync(string key)
    {
        if (bufferedSignals.TryGetValue(key, out var queued) && queued.Count > 0)
        {
            var payload = queued.Dequeue();
            if (queued.Count == 0)
                bufferedSignals.Remove(key);

            return Task.FromResult(payload);
        }

        var waiter = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        signalWaiters[key] = waiter;
        return waiter.Task;
    }

    static DurableTaskProcessOrchestrationStatus ToStatus(ProcessCheckpoint checkpoint, ProcessWaitRequest? wait) => new(
        ProcessName: checkpoint.ProcessName,
        Status: checkpoint.Status,
        CurrentNode: checkpoint.CurrentNode,
        CurrentPlace: checkpoint.CurrentPlace,
        Wait: wait
        );

    static OperationContext CreateOperationContext(OrchestrationContext context) => OperationContext.Create(
        timeProvider: new DurableTaskOrchestrationTimeProvider(context)
    );

    void LogReplaySafe(OrchestrationContext context, LogLevel level, string message, params object?[] arguments)
    {
        if (!context.IsReplaying && logger.IsEnabled(level))
            logger.Log(level, message, arguments);
    }

    void LogReplaySafe(OrchestrationContext context, LogLevel level, Exception exception, string message, params object?[] arguments)
    {
        if (!context.IsReplaying && logger.IsEnabled(level))
            logger.Log(level, exception, message, arguments);
    }
    
    sealed class DurableTaskOrchestrationTimeProvider(OrchestrationContext context) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => 
            context.CurrentUtcDateTime.ToDateTimeOffsetUtc();
    }
}
