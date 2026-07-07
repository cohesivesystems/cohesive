namespace Cohesive.Processes.Runtime;

/// <summary>
/// Queryable durable process execution repository.
/// </summary>
public interface IProcessExecutionRepository
{
    /// <summary>
    /// Returns a process execution by id, when it is still retained by the backing engine.
    /// </summary>
    ValueTask<ProcessExecutionRecord?> GetAsync(OperationContext context, string processId);

    /// <summary>
    /// Queries retained process executions using the backing engine's native execution index.
    /// </summary>
    ValueTask<ProcessExecutionQueryResult> QueryAsync(OperationContext context, ProcessExecutionQuery query);
}

/// <summary>
/// Process execution query criteria common to process-engine execution indexes.
/// </summary>
public sealed record ProcessExecutionQuery
{
    /// <summary>
    /// Optional process instance id prefix.
    /// </summary>
    public string? ProcessIdPrefix { get; init; }

    /// <summary>
    /// Optional process definition name.
    /// </summary>
    public string? ProcessName { get; init; }

    /// <summary>
    /// Optional lifecycle statuses.
    /// </summary>
    public IReadOnlyCollection<ProcessExecutionStatus>? Statuses { get; init; }

    /// <summary>
    /// Optional inclusive lower bound on the process creation time.
    /// </summary>
    public DateTimeOffset? CreatedAfterUtc { get; init; }

    /// <summary>
    /// Optional inclusive upper bound on the process creation time.
    /// </summary>
    public DateTimeOffset? CreatedBeforeUtc { get; init; }

    /// <summary>
    /// Optional maximum page size.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Optional backend continuation token.
    /// </summary>
    public string? ContinuationToken { get; init; }
}

/// <summary>
/// Paged process execution query result.
/// </summary>
public sealed record ProcessExecutionQueryResult(
    IReadOnlyList<ProcessExecutionRecord> Items,
    string? ContinuationToken
);

/// <summary>
/// Durable process execution state retained by a process-engine execution index.
/// </summary>
public sealed record ProcessExecutionRecord(
    string ProcessId,
    string? ProcessName,
    ProcessExecutionStatus Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    string? FailureMessage = null,
    ProcessExecutionError? Error = null,
    object? Output = null
)
{
    /// <summary>
    /// Returns true when the process is in a terminal state.
    /// </summary>
    public bool IsTerminal =>
        Status is ProcessExecutionStatus.Completed
        or ProcessExecutionStatus.Failed
        or ProcessExecutionStatus.Cancelled
        or ProcessExecutionStatus.Terminated;
}
