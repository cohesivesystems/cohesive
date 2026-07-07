using DurableTask.Core;
using DurableTask.Core.Query;
using DurableTask.Core.Serializing;

namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Durable Task-backed process execution repository backed by the task hub query client.
/// </summary>
public sealed class DurableTaskProcessExecutionRepository : IProcessExecutionRepository
{
    const int DefaultPageSize = 100;
    const int MaxPageSize = 1000;

    readonly IOrchestrationServiceQueryClient queryClient;
    readonly DataConverter dataConverter;
    readonly string? taskHubName;

    /// <summary>
    /// Creates a repository over a Durable Task query client.
    /// </summary>
    public DurableTaskProcessExecutionRepository(
        IOrchestrationServiceQueryClient queryClient,
        string? taskHubName = null,
        DataConverter? dataConverter = null
        )
    {
        this.queryClient = Guard.RequireNotNull(queryClient);
        this.taskHubName = string.IsNullOrWhiteSpace(taskHubName) ? null : taskHubName;
        this.dataConverter = dataConverter ?? DurableTaskProcessSerialization.CreateDataConverter();
    }

    /// <inheritdoc />
    public async ValueTask<ProcessExecutionRecord?> GetAsync(OperationContext context, string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.ThrowIfCancellationRequested();

        string? continuationToken = null;
        do
        {
            var result = await QueryAsync(context, new()
            {
                ProcessIdPrefix = processId,
                Limit = MaxPageSize,
                ContinuationToken = continuationToken
            }).ConfigureAwait(false);

            var match = result.Items.FirstOrDefault(execution => string.Equals(execution.ProcessId, processId, StringComparison.Ordinal));
            if (match is not null)
                return match;

            continuationToken = result.ContinuationToken;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<ProcessExecutionQueryResult> QueryAsync(OperationContext context, ProcessExecutionQuery query)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        context.ThrowIfCancellationRequested();

        var orchestrationQuery = CreateQuery(query);
        var result = await queryClient.GetOrchestrationWithQueryAsync(orchestrationQuery, context.CancellationToken).ConfigureAwait(false);
        var requestedStatuses = query.Statuses is { Count: > 0 } ? query.Statuses.ToHashSet() : null;
        var items = result.OrchestrationState
            .Select(Map)
            .Where(execution => requestedStatuses is null || requestedStatuses.Contains(execution.Status))
            .Where(execution => string.IsNullOrWhiteSpace(query.ProcessName) || string.Equals(execution.ProcessName, query.ProcessName, StringComparison.Ordinal))
            .ToArray();

        return new(items, ContinuationToken: result.ContinuationToken);
    }

    OrchestrationQuery CreateQuery(ProcessExecutionQuery query)
    {
        var orchestrationQuery = new OrchestrationQuery
        {
            PageSize = NormalizePageSize(query.Limit),
            ContinuationToken = query.ContinuationToken,
            InstanceIdPrefix = string.IsNullOrWhiteSpace(query.ProcessIdPrefix) ? null : query.ProcessIdPrefix,
            CreatedTimeFrom = query.CreatedAfterUtc?.UtcDateTime,
            CreatedTimeTo = query.CreatedBeforeUtc?.UtcDateTime,
            FetchInputsAndOutputs = true,
            ExcludeEntities = true,
            TaskHubNames = null,
            RuntimeStatus = null
        };

        if (!string.IsNullOrWhiteSpace(taskHubName))
            orchestrationQuery.TaskHubNames = [taskHubName];

        if (query.Statuses is { Count: > 0 })
            orchestrationQuery.RuntimeStatus = query.Statuses.SelectMany(MapRuntimeStatuses).Distinct().ToArray();

        return orchestrationQuery;
    }

    ProcessExecutionRecord Map(OrchestrationState state)
    {
        var customStatus = TryGetCustomStatus(state.Status);
        var request = TryGetProcessRequest(state.Input);
        var processId = state.OrchestrationInstance?.InstanceId
            ?? throw new InvalidOperationException("Durable Task returned an orchestration state without an instance id.");

        return new(
            ProcessId: processId,
            ProcessName: customStatus?.ProcessName ?? request?.ProcessName,
            Status: DurableTaskProcessStatus.ResolveStatus(state.OrchestrationStatus, customStatus, state.FailureDetails),
            StartedAtUtc: state.CreatedTime.ToDateTimeOffsetUtc(),
            UpdatedAtUtc: state.LastUpdatedTime.ToDateTimeOffsetUtc(),
            CompletedAtUtc: DurableTaskProcessStatus.ToCompletedAtUtc(state.CompletedTime),
            Parameters: request?.Parameters,
            FailureMessage: DurableTaskProcessStatus.FormatFailureMessage(state.FailureDetails),
            Error: DurableTaskProcessStatus.MapFailure(state.FailureDetails),
            Output: TryGetProcessOutput(state.Output)
            );
    }

    DurableTaskProcessOrchestrationStatus? TryGetCustomStatus(string? serializedStatus)
    {
        if (string.IsNullOrWhiteSpace(serializedStatus))
            return null;

        try
        {
            return dataConverter.Deserialize<DurableTaskProcessOrchestrationStatus>(serializedStatus);
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
            return dataConverter.Deserialize<DurableTaskProcessRequest>(serializedInput);
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
            return dataConverter.Deserialize<ProcessRunResult>(serializedOutput) is { } run ? run.Result : null;
        }
        catch
        {
            return null;
        }
    }

    static int NormalizePageSize(int? limit) => limit is null or <= 0 ? DefaultPageSize : Math.Min(limit.Value, MaxPageSize);

    static IEnumerable<OrchestrationStatus> MapRuntimeStatuses(ProcessExecutionStatus status)
    {
        return status switch
        {
            ProcessExecutionStatus.Pending => [OrchestrationStatus.Pending],
            ProcessExecutionStatus.Running => [OrchestrationStatus.Running, OrchestrationStatus.ContinuedAsNew],
            ProcessExecutionStatus.Waiting => [OrchestrationStatus.Running],
            ProcessExecutionStatus.Completed => [OrchestrationStatus.Completed],
            ProcessExecutionStatus.Failed => [OrchestrationStatus.Failed],
            ProcessExecutionStatus.Cancelled => [OrchestrationStatus.Canceled],
            ProcessExecutionStatus.Terminated => [OrchestrationStatus.Terminated],
            ProcessExecutionStatus.Suspended => [OrchestrationStatus.Suspended],
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unexpected process execution status.")
        };
    }

}
