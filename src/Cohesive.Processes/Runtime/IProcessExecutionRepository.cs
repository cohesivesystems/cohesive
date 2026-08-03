namespace Cohesive.Processes.Runtime;

/// <summary>
/// Queryable durable process execution repository.
/// </summary>
public interface IProcessExecutionRepository
{
    /// <summary>
    /// Returns a process execution by id, when it is still retained by the backing engine.
    /// </summary>
    /// <param name="context">Operation context that supplies cancellation for the query.</param>
    /// <param name="processId">Stable process instance identifier to retrieve.</param>
    /// <returns>The retained execution record, or <see langword="null"/> when no matching execution is retained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="processId"/> is empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    ValueTask<ProcessExecutionRecord?> GetAsync(OperationContext context, string processId);

    /// <summary>
    /// Queries retained process executions using the backing engine's native execution index.
    /// </summary>
    /// <param name="context">Operation context that supplies cancellation for the query.</param>
    /// <param name="query">Provider-neutral filters and paging request to apply.</param>
    /// <returns>The retained executions in the requested page and an opaque continuation token when another page is available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
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
/// <param name="Items">Retained execution records in provider order for this page.</param>
/// <param name="ContinuationToken">Opaque provider token for the next page, or <see langword="null"/> when the query is exhausted.</param>
public sealed record ProcessExecutionQueryResult(
    IReadOnlyList<ProcessExecutionRecord> Items,
    string? ContinuationToken
);

/// <summary>
/// Durable process execution state retained by a process-engine execution index.
/// </summary>
/// <param name="ProcessId">Stable process instance identifier.</param>
/// <param name="ProcessName">Process definition name when retained by the execution engine.</param>
/// <param name="Status">Current high-level lifecycle status.</param>
/// <param name="StartedAtUtc">UTC creation or start time when retained by the execution engine.</param>
/// <param name="UpdatedAtUtc">UTC time of the latest retained execution update.</param>
/// <param name="CompletedAtUtc">UTC terminal completion time, or <see langword="null"/> for nonterminal or unknown executions.</param>
/// <param name="Parameters">Read-only process input parameters when retained by the execution engine.</param>
/// <param name="FailureMessage">Human-readable failure summary when available.</param>
/// <param name="Error">Structured failure evidence when available.</param>
/// <param name="Output">Process output when retained by the execution engine.</param>
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
