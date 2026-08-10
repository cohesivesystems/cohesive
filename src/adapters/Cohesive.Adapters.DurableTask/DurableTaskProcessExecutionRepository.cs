using Cohesive.Execution;
using DurableTask.Core;
using DurableTask.Core.Query;
using LegacyDataConverter = DurableTask.Core.Serializing.DataConverter;
using ModernDurableTaskClient = Microsoft.DurableTask.Client.DurableTaskClient;
using ModernOrchestrationMetadata = Microsoft.DurableTask.Client.OrchestrationMetadata;
using ModernOrchestrationQuery = Microsoft.DurableTask.Client.OrchestrationQuery;
using ModernOrchestrationStatus = Microsoft.DurableTask.Client.OrchestrationRuntimeStatus;

namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Durable Task-backed process execution repository over either the current standalone client or a retained legacy
/// task-hub query client.
/// </summary>
/// <remarks>
/// The standalone client is the primary path for canonical Process executions. Its task-hub custom status is a
/// derived <see cref="ExecutionStatus"/> observation; the retained start receipt supplies exact identity and definition
/// affinity. The Core query-client constructor exists only for historical executions created by the retired adapter.
/// </remarks>
public sealed class DurableTaskProcessExecutionRepository : IProcessExecutionRepository
{
    const int DefaultPageSize = 100;
    const int MaxPageSize = 1000;

    readonly ModernDurableTaskClient? currentClient;
    readonly IOrchestrationServiceQueryClient? historicalQueryClient;
    readonly LegacyDataConverter? historicalDataConverter;
    readonly string? taskHubName;

    /// <summary>Creates a repository over the standalone Durable Task client used by the canonical Process interpreter.</summary>
    /// <param name="client">Standalone Durable Task client that owns current task-hub queries.</param>
    /// <param name="taskHubName">
    /// Optional paged-query task hub restriction. Exact lookup uses the client's configured task-hub scope.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public DurableTaskProcessExecutionRepository(
        ModernDurableTaskClient client,
        string? taskHubName = null)
    {
        currentClient = Guard.RequireNotNull(client);
        this.taskHubName = NormalizeTaskHubName(taskHubName);
    }

    /// <summary>Creates a historical repository over the Core task-hub query client used by the retired Process adapter.</summary>
    /// <remarks>
    /// This compatibility path understands only the retired adapter's monitoring projections. Remove it only after
    /// pre-canonical task hubs are outside the supported migration and retention window.
    /// </remarks>
    /// <param name="queryClient">Legacy Durable Task client that owns historical task-hub query execution.</param>
    /// <param name="taskHubName">Optional task hub restriction; <see langword="null"/> queries the client's configured scope.</param>
    /// <param name="dataConverter">Optional converter for retained historical projections.</param>
    /// <exception cref="ArgumentNullException"><paramref name="queryClient"/> is <see langword="null"/>.</exception>
    public DurableTaskProcessExecutionRepository(
        IOrchestrationServiceQueryClient queryClient,
        string? taskHubName = null,
        LegacyDataConverter? dataConverter = null)
    {
        historicalQueryClient = Guard.RequireNotNull(queryClient);
        this.taskHubName = NormalizeTaskHubName(taskHubName);
        historicalDataConverter = dataConverter ?? DurableTaskProcessQuerySerialization.CreateDataConverter();
    }

    /// <inheritdoc />
    public ValueTask<ProcessExecutionRecord?> GetAsync(OperationContext context, string processId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        context.ThrowIfCancellationRequested();

        return currentClient is null
            ? GetHistoricalAsync(context, processId)
            : GetCurrentAsync(context, processId);
    }

    /// <inheritdoc />
    public ValueTask<ProcessExecutionQueryResult> QueryAsync(
        OperationContext context,
        ProcessExecutionQuery query)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        context.ThrowIfCancellationRequested();

        return currentClient is null
            ? QueryHistoricalAsync(context, query)
            : QueryCurrentAsync(context, query);
    }

    async ValueTask<ProcessExecutionRecord?> GetCurrentAsync(OperationContext context, string processId)
    {
        var metadata = await currentClient!.GetInstanceAsync(
            processId,
            getInputsAndOutputs: true,
            context.CancellationToken).ConfigureAwait(false);
        return metadata is null || !IsCurrentProcess(metadata)
            ? null
            : MapCurrent(metadata);
    }

    async ValueTask<ProcessExecutionQueryResult> QueryCurrentAsync(
        OperationContext context,
        ProcessExecutionQuery query)
    {
        var pageable = currentClient!.GetAllInstancesAsync(CreateCurrentQuery(query));
        await foreach (var page in pageable.AsPages()
                           .WithCancellation(context.CancellationToken)
                           .ConfigureAwait(false))
        {
            var requestedStatuses = RequestedStatuses(query);
            var items = page.Values
                .Where(IsCurrentProcess)
                .Select(MapCurrent)
                .Where(execution => MatchesQuery(execution, query, requestedStatuses))
                .ToArray();
            return new(items, page.ContinuationToken);
        }

        return new([], ContinuationToken: null);
    }

    ModernOrchestrationQuery CreateCurrentQuery(ProcessExecutionQuery query) => new(
        CreatedFrom: query.CreatedAfterUtc,
        CreatedTo: query.CreatedBeforeUtc,
        Statuses: query.Statuses is { Count: > 0 }
            ? query.Statuses.SelectMany(MapCurrentRuntimeStatuses).Distinct()
            : null,
        TaskHubNames: taskHubName is null ? null : [taskHubName],
        InstanceIdPrefix: string.IsNullOrWhiteSpace(query.ProcessIdPrefix) ? null : query.ProcessIdPrefix,
        PageSize: NormalizePageSize(query.Limit),
        FetchInputsAndOutputs: true,
        ContinuationToken: query.ContinuationToken);

    ProcessExecutionRecord MapCurrent(ModernOrchestrationMetadata metadata)
    {
        if (metadata.DataConverter is null)
        {
            throw InvalidCurrentEvidence(
                metadata,
                "was returned without the data converter required to inspect safe custom status and start affinity");
        }

        var start = ReadCurrentStart(metadata);
        ValidatePhysicalIdentity(metadata, start);
        var runtimeStatus = ReadCurrentStatus(metadata);
        ValidateStatusAffinity(metadata, start, runtimeStatus);

        if (runtimeStatus is null && metadata.RuntimeStatus != ModernOrchestrationStatus.Pending)
        {
            throw InvalidCurrentEvidence(
                metadata,
                "does not contain canonical custom status outside the pending admission state");
        }

        var status = DurableTaskProcessStatus.ResolveStatus(
            metadata.RuntimeStatus,
            runtimeStatus,
            metadata.FailureDetails);
        ValidateTerminalAgreement(metadata, runtimeStatus, status);
        var definition = runtimeStatus?.Definition ?? start.Receipt.Request.Definition;

        return new(
            ProcessId: metadata.InstanceId,
            ProcessName: definition.DefinitionId.Value,
            Status: status,
            StartedAtUtc: runtimeStatus?.CreatedAtUtc ?? metadata.CreatedAt,
            UpdatedAtUtc: metadata.LastUpdatedAt,
            CompletedAtUtc: runtimeStatus?.TerminalOutcome.OccurredAtUtc
                ?? (DurableTaskProcessStatus.IsTerminal(metadata.RuntimeStatus)
                    ? metadata.LastUpdatedAt
                    : null),
            Parameters: null,
            FailureMessage: null,
            Error: null,
            Output: null,
            RuntimeStatus: runtimeStatus);
    }

    static DurableTaskSequentialProcessStart ReadCurrentStart(ModernOrchestrationMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.SerializedInput))
        {
            throw InvalidCurrentEvidence(metadata, "does not contain its canonical start receipt");
        }

        try
        {
            return metadata.ReadInputAs<DurableTaskSequentialProcessStart>()
                ?? throw InvalidCurrentEvidence(metadata, "contains a null canonical start receipt");
        }
        catch (Exception exception) when (exception is not InvalidOperationException
                                          || !exception.Message.StartsWith("Durable Task Process instance", StringComparison.Ordinal))
        {
            throw InvalidCurrentEvidence(metadata, "contains malformed canonical start evidence", exception);
        }
    }

    static ExecutionStatus? ReadCurrentStatus(ModernOrchestrationMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.SerializedCustomStatus))
        {
            return null;
        }

        try
        {
            return metadata.ReadCustomStatusAs<ExecutionStatus>()
                ?? throw InvalidCurrentEvidence(metadata, "contains a null canonical custom status");
        }
        catch (Exception exception) when (exception is not InvalidOperationException
                                          || !exception.Message.StartsWith("Durable Task Process instance", StringComparison.Ordinal))
        {
            throw InvalidCurrentEvidence(metadata, "contains malformed canonical custom status", exception);
        }
    }

    static void ValidatePhysicalIdentity(
        ModernOrchestrationMetadata metadata,
        DurableTaskSequentialProcessStart start)
    {
        var expected = DurableTaskSequentialProcessIdentities.OrchestrationInstance(start);
        if (!string.Equals(metadata.InstanceId, expected, StringComparison.Ordinal))
        {
            throw InvalidCurrentEvidence(
                metadata,
                $"conflicts with the physical identity derived from its retained start receipt ('{expected}')");
        }
    }

    static void ValidateStatusAffinity(
        ModernOrchestrationMetadata metadata,
        DurableTaskSequentialProcessStart start,
        ExecutionStatus? status)
    {
        if (status is null)
        {
            return;
        }

        var request = start.Receipt.Request;
        if (status.ProcessInstanceId != request.InitialContinuation.ProcessInstanceId
            || status.Definition != request.Definition)
        {
            throw InvalidCurrentEvidence(
                metadata,
                "contains canonical custom status with Process identity or exact definition affinity that conflicts with its start receipt");
        }
    }

    static void ValidateTerminalAgreement(
        ModernOrchestrationMetadata metadata,
        ExecutionStatus? runtimeStatus,
        ProcessExecutionStatus resolved)
    {
        if (runtimeStatus is null
            || runtimeStatus.TerminalOutcome.Kind == ExecutionTerminalOutcomeKind.None
            || !DurableTaskProcessStatus.IsTerminal(metadata.RuntimeStatus))
        {
            return;
        }

        var canonical = DurableTaskProcessStatus.MapStatus(runtimeStatus);
        if (canonical != resolved)
        {
            throw InvalidCurrentEvidence(
                metadata,
                $"has contradictory terminal states: canonical '{canonical}' and task-hub '{resolved}'");
        }
    }

    static InvalidOperationException InvalidCurrentEvidence(
        ModernOrchestrationMetadata metadata,
        string detail,
        Exception? innerException = null) => new(
        $"Durable Task Process instance '{metadata.InstanceId}' {detail}.",
        innerException);

    static bool IsCurrentProcess(ModernOrchestrationMetadata metadata) =>
        string.Equals(
            metadata.Name,
            DurableTaskSequentialProcessNames.Orchestration,
            StringComparison.Ordinal);

    async ValueTask<ProcessExecutionRecord?> GetHistoricalAsync(OperationContext context, string processId)
    {
        string? continuationToken = null;
        do
        {
            var result = await QueryHistoricalAsync(context, new()
            {
                ProcessIdPrefix = processId,
                Limit = MaxPageSize,
                ContinuationToken = continuationToken
            }).ConfigureAwait(false);

            var match = result.Items.FirstOrDefault(execution =>
                string.Equals(execution.ProcessId, processId, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            continuationToken = result.ContinuationToken;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));

        return null;
    }

    async ValueTask<ProcessExecutionQueryResult> QueryHistoricalAsync(
        OperationContext context,
        ProcessExecutionQuery query)
    {
        var orchestrationQuery = CreateHistoricalQuery(query);
        var result = await historicalQueryClient!.GetOrchestrationWithQueryAsync(
            orchestrationQuery,
            context.CancellationToken).ConfigureAwait(false);
        var requestedStatuses = RequestedStatuses(query);
        var items = result.OrchestrationState
            .Select(MapHistorical)
            .Where(execution => MatchesQuery(execution, query, requestedStatuses))
            .ToArray();

        return new(items, ContinuationToken: result.ContinuationToken);
    }

    OrchestrationQuery CreateHistoricalQuery(ProcessExecutionQuery query)
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

        if (taskHubName is not null)
        {
            orchestrationQuery.TaskHubNames = [taskHubName];
        }

        if (query.Statuses is { Count: > 0 })
        {
            orchestrationQuery.RuntimeStatus = query.Statuses.SelectMany(MapHistoricalRuntimeStatuses).Distinct().ToArray();
        }

        return orchestrationQuery;
    }

    ProcessExecutionRecord MapHistorical(OrchestrationState state)
    {
        var customStatus = TryGetHistoricalCustomStatus(state.Status);
        var request = TryGetHistoricalProcessRequest(state.Input);
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
            Output: TryGetHistoricalProcessOutput(state.Output),
            RuntimeStatus: null);
    }

    DurableTaskProcessOrchestrationStatus? TryGetHistoricalCustomStatus(string? serializedStatus)
    {
        if (string.IsNullOrWhiteSpace(serializedStatus))
        {
            return null;
        }

        try
        {
            return historicalDataConverter!.Deserialize<DurableTaskProcessOrchestrationStatus>(serializedStatus);
        }
        catch
        {
            return null;
        }
    }

    DurableTaskProcessRequest? TryGetHistoricalProcessRequest(string? serializedInput)
    {
        if (string.IsNullOrWhiteSpace(serializedInput))
        {
            return null;
        }

        try
        {
            return historicalDataConverter!.Deserialize<DurableTaskProcessRequest>(serializedInput);
        }
        catch
        {
            return null;
        }
    }

    object? TryGetHistoricalProcessOutput(string? serializedOutput)
    {
        if (string.IsNullOrWhiteSpace(serializedOutput))
        {
            return null;
        }

        try
        {
            return historicalDataConverter!.Deserialize<DurableTaskProcessOutput>(serializedOutput)?.Result;
        }
        catch
        {
            return null;
        }
    }

    static bool MatchesQuery(
        ProcessExecutionRecord execution,
        ProcessExecutionQuery query,
        HashSet<ProcessExecutionStatus>? requestedStatuses) =>
        (requestedStatuses is null || requestedStatuses.Contains(execution.Status))
        && (string.IsNullOrWhiteSpace(query.ProcessName)
            || string.Equals(execution.ProcessName, query.ProcessName, StringComparison.Ordinal));

    static HashSet<ProcessExecutionStatus>? RequestedStatuses(ProcessExecutionQuery query) =>
        query.Statuses is { Count: > 0 } ? query.Statuses.ToHashSet() : null;

    static string? NormalizeTaskHubName(string? taskHubName) =>
        string.IsNullOrWhiteSpace(taskHubName) ? null : taskHubName;

    static int NormalizePageSize(int? limit) =>
        limit is null or <= 0 ? DefaultPageSize : Math.Min(limit.Value, MaxPageSize);

    static IEnumerable<OrchestrationStatus> MapHistoricalRuntimeStatuses(ProcessExecutionStatus status)
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

    // Retain obsolete provider cases in query filters so migrated task-hub rows are not made undiscoverable.
#pragma warning disable CS0618
    static IEnumerable<ModernOrchestrationStatus> MapCurrentRuntimeStatuses(ProcessExecutionStatus status)
    {
        return status switch
        {
            ProcessExecutionStatus.Pending => [ModernOrchestrationStatus.Pending],
            ProcessExecutionStatus.Running =>
                [ModernOrchestrationStatus.Running, ModernOrchestrationStatus.ContinuedAsNew],
            ProcessExecutionStatus.Waiting => [ModernOrchestrationStatus.Running],
            ProcessExecutionStatus.Completed => [ModernOrchestrationStatus.Completed],
            ProcessExecutionStatus.Failed =>
                [ModernOrchestrationStatus.Failed, ModernOrchestrationStatus.Completed],
            ProcessExecutionStatus.Cancelled =>
                [ModernOrchestrationStatus.Completed, ModernOrchestrationStatus.Canceled],
            ProcessExecutionStatus.Terminated =>
                [ModernOrchestrationStatus.Completed, ModernOrchestrationStatus.Terminated],
            ProcessExecutionStatus.Suspended =>
                [ModernOrchestrationStatus.Running, ModernOrchestrationStatus.Suspended],
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unexpected process execution status.")
        };
    }
#pragma warning restore CS0618
}
