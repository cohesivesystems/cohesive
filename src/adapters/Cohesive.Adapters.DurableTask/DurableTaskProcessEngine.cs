using DurableTask.Core;
using DurableTask.Core.Serializing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Durable Task-backed process engine that starts, signals, and monitors registered process definitions.
/// </summary>
public sealed class DurableTaskProcessEngine : IProcessEngine
{
    internal const string SignalEventName = "Cohesive.Process.Signal";

    readonly IDurableTaskProcessHubClient client;
    readonly DurableTaskProcessDefinitionRegistry definitions;
    readonly DataConverter dataConverter;
    readonly DurableTaskProcessOptions options;
    readonly IOperationContextScopeFactory? operationContextScopeFactory;
    readonly ILogger<DurableTaskProcessEngine> logger;

    internal DurableTaskProcessEngine(
        IDurableTaskProcessHubClient client,
        DurableTaskProcessDefinitionRegistry definitions,
        DurableTaskProcessOptions? options = null,
        DataConverter? dataConverter = null,
        IOperationContextScopeFactory? operationContextScopeFactory = null,
        ILoggerFactory? loggerFactory = null
        )
    {
        this.client = Guard.RequireNotNull(client);
        this.definitions = Guard.RequireNotNull(definitions);
        this.options = options ?? new DurableTaskProcessOptions();
        this.dataConverter = dataConverter ?? DurableTaskProcessSerialization.CreateDataConverter();
        this.operationContextScopeFactory = operationContextScopeFactory;
        logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DurableTaskProcessEngine>();
    }

    /// <inheritdoc />
    public async Task<ProcessStartResult> StartAsync(OperationContext context, ProcessDefinition process, IReadOnlyDictionary<string, object?>? parameters = null, ProcessRunOptions? runOptions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(process);
        context.ThrowIfCancellationRequested();

        using var _ = PushOperationContext(context);

        definitions.Register(process);

        var processId = string.IsNullOrWhiteSpace(runOptions?.ProcessId)
            ? Guid.NewGuid().ToString("N")
            : runOptions.ProcessId;

        runOptions = runOptions is null
            ? new() { ProcessId = processId }
            : runOptions with { ProcessId = processId };

        logger.LogInformation(
            "Starting durable process '{ProcessName}' ({ProcessId}) using orchestration '{OrchestrationName}'.",
            process.Name,
            processId,
            options.OrchestrationName);

        await client.StartAsync(
            orchestrationName: options.OrchestrationName,
            orchestrationVersion: options.OrchestrationVersion,
            instanceId: processId,
            request: new(
                ProcessName: process.Name,
                Parameters: parameters,
                RunOptions: runOptions
            )
        ).ConfigureAwait(false);

        return new(
            ProcessId: processId,
            ProcessName: process.Name,
            StartedAtUtc: context.UtcNow
            );
    }

    /// <inheritdoc />
    public async Task<ProcessExecutionState?> GetStatusAsync(OperationContext context, string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.ThrowIfCancellationRequested();

        using var _ = PushOperationContext(context);

        var state = await client
            .GetStateAsync(processId, context.CancellationToken)
            .ConfigureAwait(false);

        if (state is null)
        {
            logger.LogDebug(
                "Durable process '{ProcessId}' was not found when retrieving status.",
                processId);
            return null;
        }

        var customStatus = TryGetCustomStatus(state.CustomStatus);
        var request = TryGetProcessRequest(state.Input);
        var resolved = new ProcessExecutionState(
            ProcessId: processId,
            ProcessName: customStatus?.ProcessName ?? request?.ProcessName,
            Status: DurableTaskProcessStatus.ResolveStatus(state.Status, customStatus, state.FailureDetails),
            StartedAtUtc: state.CreatedTime.ToDateTimeOffsetUtc(),
            UpdatedAtUtc: state.LastUpdatedTime.ToDateTimeOffsetUtc(),
            CompletedAtUtc: DurableTaskProcessStatus.ToCompletedAtUtc(state.CompletedTime)
            )
        {
            Parameters = request?.Parameters,
            Output = TryGetProcessOutput(state.Output),
            Error = DurableTaskProcessStatus.MapFailure(state.FailureDetails),
            FailureMessage = DurableTaskProcessStatus.FormatFailureMessage(state.FailureDetails)
        };

        logger.LogDebug(
            "Retrieved durable status for process '{ProcessId}': {Status}.",
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
        context.ThrowIfCancellationRequested();

        using var _ = PushOperationContext(context);
        logger.LogInformation(
            "Publishing signal '{SignalKey}' to durable process '{ProcessId}'.",
            signalKey,
            processId);
        return client.RaiseEventAsync(
            instanceId: processId,
            eventName: SignalEventName,
            payload: new DurableTaskProcessSignal(signalKey, payload),
            ct: context.CancellationToken
            );
    }

    /// <exception cref="TimeoutException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    /// <inheritdoc />
    public async Task<ProcessRunResult> WaitForCompletionAsync(OperationContext context, string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.CancellationToken.ThrowIfCancellationRequested();

        using var _ = PushOperationContext(context);

        var current = await client
            .GetStateAsync(processId, context.CancellationToken)
            .ConfigureAwait(false);

        if (current is null)
            throw new InvalidOperationException($"Durable process orchestration '{processId}' was not found.");

        if (DurableTaskProcessStatus.IsTerminal(current.Status))
        {
            logger.LogInformation(
                "Durable process '{ProcessId}' is already terminal with orchestration status '{Status}'.",
                processId,
                current.Status);
            return DeserializeCompletion(processId, current);
        }

        logger.LogInformation(
            "Waiting for durable process '{ProcessId}' to complete with timeout '{Timeout}'.",
            processId,
            options.CompletionTimeout);

        var completed = await client.WaitForCompletionAsync(
            instanceId: processId,
            timeout: options.CompletionTimeout,
            ct: context.CancellationToken
            ).ConfigureAwait(false);

        if (completed is null)
            throw new TimeoutException($"Durable process orchestration '{processId}' did not complete within '{options.CompletionTimeout}'.");

        var result = DeserializeCompletion(processId, completed);
        logger.LogInformation(
            "Durable process '{ProcessId}' completed at place '{FinalPlace}'.",
            processId,
            result.FinalPlace);
        return result;
    }

    ProcessRunResult DeserializeCompletion(string processId, DurableTaskProcessHubState state)
    {
        if (state.Status is not OrchestrationStatus.Completed)
        {
            var customStatus = TryGetCustomStatus(state.CustomStatus);
            var node = customStatus?.CurrentNode ?? "<unknown>";
            var place = customStatus?.CurrentPlace ?? "<unknown>";
            throw new InvalidOperationException(
                $"Durable process orchestration '{processId}' completed with status '{state.Status}'. " +
                $"Node: {node}. Place: {place}. Output: {state.Output ?? "<null>"}. Failure: {state.FailureDetails?.ToString() ?? "<none>"}"
                );
        }

        var deserialized = dataConverter.Deserialize(state.Output ?? string.Empty, typeof(ProcessRunResult));
        if (deserialized is not ProcessRunResult result)
            throw new InvalidOperationException($"Durable process orchestration '{processId}' returned an invalid '{nameof(ProcessRunResult)}' payload.");

        return result;
    }

    DurableTaskProcessOrchestrationStatus? TryGetCustomStatus(string? serializedStatus)
    {
        if (string.IsNullOrWhiteSpace(serializedStatus))
            return null;

        try
        {
            return dataConverter.Deserialize(serializedStatus, typeof(DurableTaskProcessOrchestrationStatus)) as DurableTaskProcessOrchestrationStatus;
        }
        catch
        {
            return null;
        }
    }

    DurableTaskProcessRequest? TryGetProcessRequest(string? serializedInput)
    {
        if (string.IsNullOrWhiteSpace(serializedInput))
            return null;

        try
        {
            return dataConverter.Deserialize(serializedInput, typeof(DurableTaskProcessRequest)) as DurableTaskProcessRequest;
        }
        catch
        {
            return null;
        }
    }

    object? TryGetProcessOutput(string? serializedOutput)
    {
        if (string.IsNullOrWhiteSpace(serializedOutput))
            return null;

        try
        {
            return dataConverter.Deserialize(serializedOutput, typeof(ProcessRunResult)) is ProcessRunResult run
                ? run.Result
                : null;
        }
        catch
        {
            return null;
        }
    }

    IDisposable? PushOperationContext(OperationContext context) =>
        operationContextScopeFactory?.Push(context);
}

interface IDurableTaskProcessHubClient
{
    Task StartAsync(string orchestrationName, string orchestrationVersion, string instanceId, DurableTaskProcessRequest request);

    Task<DurableTaskProcessHubState?> GetStateAsync(string instanceId, CancellationToken ct);

    Task<DurableTaskProcessHubState?> WaitForCompletionAsync(string instanceId, TimeSpan timeout, CancellationToken ct);

    Task RaiseEventAsync(string instanceId, string eventName, object? payload, CancellationToken ct);
}

sealed record DurableTaskProcessHubState(
    OrchestrationStatus Status,
    string? CustomStatus,
    string? Input,
    string? Output,
    DateTime CreatedTime,
    DateTime LastUpdatedTime,
    DateTime CompletedTime,
    FailureDetails? FailureDetails = null
);

sealed class DurableTaskProcessHubClient(TaskHubClient client) : IDurableTaskProcessHubClient
{
    public async Task StartAsync(
        string orchestrationName,
        string orchestrationVersion,
        string instanceId,
        DurableTaskProcessRequest request
        )
    {
        _ = await client
            .CreateOrchestrationInstanceAsync(orchestrationName, orchestrationVersion, instanceId, request)
            .ConfigureAwait(false);
    }

    public async Task<DurableTaskProcessHubState?> GetStateAsync(
        string instanceId,
        CancellationToken ct
        )
    {
        ct.ThrowIfCancellationRequested();

        var state = await client
            .GetOrchestrationStateAsync(instanceId)
            .ConfigureAwait(false);

        return state is null ? null : ToHubState(state);
    }

    public async Task<DurableTaskProcessHubState?> WaitForCompletionAsync(string instanceId, TimeSpan timeout, CancellationToken ct)
    {
        var state = await client
            .WaitForOrchestrationAsync(new() { InstanceId = instanceId }, timeout, ct)
            .ConfigureAwait(false);

        return state is null ? null : ToHubState(state);
    }

    public Task RaiseEventAsync(string instanceId, string eventName, object? payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);
        return client.RaiseEventAsync(new() { InstanceId = instanceId }, eventName, payload);
    }

    static DurableTaskProcessHubState ToHubState(OrchestrationState state)
    {
        return new(
            Status: state.OrchestrationStatus,
            CustomStatus: state.Status,
            Input: state.Input,
            Output: state.Output,
            FailureDetails: state.FailureDetails,
            CreatedTime: state.CreatedTime,
            LastUpdatedTime: state.LastUpdatedTime,
            CompletedTime: state.CompletedTime
            );
    }
}

sealed record DurableTaskProcessRequest(
    string ProcessName,
    IReadOnlyDictionary<string, object?>? Parameters,
    ProcessRunOptions RunOptions
);

sealed record DurableTaskProcessSignal(string Key, object? Payload);

sealed record DurableTaskProcessOrchestrationStatus(
    string ProcessName,
    ProcessExecutionStatus Status,
    string? CurrentNode,
    string CurrentPlace,
    ProcessWaitRequest? Wait
);

static class DurableTaskProcessSerialization
{
    public static DataConverter CreateDataConverter() => new DurableTaskSystemTextJsonDataConverter();
}
