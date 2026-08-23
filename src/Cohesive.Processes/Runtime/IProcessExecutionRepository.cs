using Cohesive.Execution;

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
    /// <param name="processId">
    /// Stable repository key assigned by the backing execution engine. When <see cref="ProcessExecutionRecord.RuntimeStatus"/>
    /// is available, its logical <see cref="ExecutionStatus.ProcessInstanceId"/> may intentionally differ from this physical key.
    /// </param>
    /// <returns>The retained execution record, or <see langword="null"/> when no matching execution is retained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="processId"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Retained execution metadata is malformed or contains conflicting physical and canonical evidence.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    ValueTask<ProcessExecutionRecord?> GetAsync(OperationContext context, string processId);

    /// <summary>
    /// Returns a process execution by trusted authority scope and logical Process identity when it is still retained
    /// by the backing engine.
    /// </summary>
    /// <param name="context">Operation context that supplies cancellation for the query.</param>
    /// <param name="authorityScope">Exact trusted authority and optional tenant isolating the execution.</param>
    /// <param name="processInstanceId">Canonical logical Process instance identity.</param>
    /// <returns>The retained execution record, or <see langword="null"/> when no matching execution is retained.</returns>
    /// <remarks>
    /// Application-facing reads use this logical address. Implementations may derive a provider-specific physical
    /// key, but callers must not supply one or rely on its representation.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="authorityScope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="processInstanceId"/> is the default identity.</exception>
    /// <exception cref="InvalidOperationException">
    /// Logical lookup is unsupported by a migration-only repository, or retained execution metadata is malformed or
    /// contradictory.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    ValueTask<ProcessExecutionRecord?> GetAsync(
        OperationContext context,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId);

    /// <summary>
    /// Queries retained process executions using the backing engine's native execution index.
    /// </summary>
    /// <param name="context">Operation context that supplies cancellation for the query.</param>
    /// <param name="query">Provider-neutral filters and paging request to apply.</param>
    /// <returns>The retained executions in the requested page and an opaque continuation token when another page is available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Retained execution metadata is malformed or contains conflicting physical and canonical evidence.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    ValueTask<ProcessExecutionQueryResult> QueryAsync(OperationContext context, ProcessExecutionQuery query);
}

/// <summary>
/// Process execution query criteria common to process-engine execution indexes.
/// </summary>
public sealed record ProcessExecutionQuery
{
    /// <summary>
    /// Optional physical repository-key prefix.
    /// </summary>
    public string? ProcessIdPrefix { get; init; }

    /// <summary>
    /// Optional process definition name or stable definition identity.
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
/// <param name="ProcessId">Stable physical repository key assigned by the backing execution engine.</param>
/// <param name="ProcessName">Process definition name or stable definition identity when retained by the execution engine.</param>
/// <param name="Status">Current high-level lifecycle status.</param>
/// <param name="StartedAtUtc">UTC creation or start time when retained by the execution engine.</param>
/// <param name="UpdatedAtUtc">UTC time of the latest retained execution update.</param>
/// <param name="CompletedAtUtc">UTC terminal completion time, or <see langword="null"/> for nonterminal or unknown executions.</param>
/// <param name="Parameters">Read-only process input parameters when retained by the execution engine.</param>
/// <param name="FailureMessage">Human-readable failure summary when available.</param>
/// <param name="Error">Structured failure evidence when available.</param>
/// <param name="Output">Process output when retained by the execution engine.</param>
/// <param name="RuntimeStatus">
/// Protocol-neutral canonical Process status when the execution interpretation published one. Its
/// <see cref="ExecutionStatus.ProcessInstanceId"/> is the logical Process identity and need not equal the physical
/// <paramref name="ProcessId"/> used by the backing engine.
/// </param>
/// <param name="Definition">
/// Exact canonical definition identity, revision, and fingerprint when retained by the backing interpretation.
/// This remains available during admission windows in which <paramref name="RuntimeStatus"/> has not yet been
/// published.
/// </param>
/// <param name="LogicalProcessInstanceId">
/// Canonical logical Process identity when retained by the backing interpretation. This remains available during
/// admission windows in which <paramref name="RuntimeStatus"/> has not yet been published and intentionally differs
/// from the physical <paramref name="ProcessId"/> assigned by the backing engine.
/// </param>
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
    object? Output = null,
    ExecutionStatus? RuntimeStatus = null,
    ExecutionDefinitionReference? Definition = null,
    ProcessInstanceId? LogicalProcessInstanceId = null
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
