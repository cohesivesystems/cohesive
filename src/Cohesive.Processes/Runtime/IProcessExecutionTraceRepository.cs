using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Runtime;

/// <summary>Reads retained payload-safe canonical traces without loading Process values into status projections.</summary>
public interface IProcessExecutionTraceRepository
{
    /// <summary>Reads canonical traces retained for one physical execution repository key.</summary>
    /// <param name="context">Operation context that supplies cancellation for the read.</param>
    /// <param name="processId">Stable physical repository key assigned by the backing execution engine.</param>
    /// <returns>An explicit availability result and the canonical trace record when available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="processId"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Retained execution, status, result, or trace evidence is malformed or contradictory.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The selected repository implementation cannot retrieve canonical normalized traces.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    ValueTask<ProcessExecutionTraceReadResult> GetTracesAsync(OperationContext context, string processId);
}

/// <summary>Disposition of one explicit Process trace repository read.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ProcessExecutionTraceReadState
{
    /// <summary>No read disposition was supplied.</summary>
    Unspecified = 0,

    /// <summary>No matching canonical execution is retained by the repository.</summary>
    NotFound = 1,

    /// <summary>The execution is retained but has not produced its terminal canonical result artifact.</summary>
    InProgress = 2,

    /// <summary>The execution retained a canonical result and its trace coverage is available.</summary>
    Available = 3,

    /// <summary>The execution is terminal but no canonical result artifact exists from which traces can be read.</summary>
    TerminalArtifactUnavailable = 4
}

/// <summary>Explicit outcome of reading retained canonical Process traces.</summary>
public sealed record ProcessExecutionTraceReadResult
{
    /// <summary>Creates one explicit trace-read result.</summary>
    /// <param name="state">Read disposition.</param>
    /// <param name="record">Canonical trace record exactly when <paramref name="state"/> is available.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unspecified or unsupported.</exception>
    /// <exception cref="ArgumentException">Record presence contradicts <paramref name="state"/>.</exception>
    [JsonConstructor]
    public ProcessExecutionTraceReadResult(
        ProcessExecutionTraceReadState state,
        ProcessExecutionTraceRecord? record = null)
    {
        if (!Enum.IsDefined(state) || state == ProcessExecutionTraceReadState.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "A trace read requires an explicit state.");
        }
        if ((state == ProcessExecutionTraceReadState.Available) != (record is not null))
        {
            throw new ArgumentException(
                "A canonical trace record exists exactly when the trace read is available.",
                nameof(record));
        }

        State = state;
        Record = record;
    }

    /// <summary>Explicit availability disposition.</summary>
    public ProcessExecutionTraceReadState State { get; }

    /// <summary>Canonical trace coverage when <see cref="State"/> is available.</summary>
    public ProcessExecutionTraceRecord? Record { get; }

    /// <summary>Creates a result for an execution that is not retained.</summary>
    public static ProcessExecutionTraceReadResult NotFound() =>
        new(ProcessExecutionTraceReadState.NotFound);

    /// <summary>Creates a result for a retained execution that is still active.</summary>
    public static ProcessExecutionTraceReadResult InProgress() =>
        new(ProcessExecutionTraceReadState.InProgress);

    /// <summary>Creates a result containing available canonical trace coverage.</summary>
    /// <param name="record">Validated canonical trace record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public static ProcessExecutionTraceReadResult Available(ProcessExecutionTraceRecord record) =>
        new(ProcessExecutionTraceReadState.Available, record ?? throw new ArgumentNullException(nameof(record)));

    /// <summary>Creates a result for a terminal execution that has no canonical result artifact.</summary>
    public static ProcessExecutionTraceReadResult TerminalArtifactUnavailable() =>
        new(ProcessExecutionTraceReadState.TerminalArtifactUnavailable);
}

/// <summary>Retained normalized Process traces with explicit pre-retention coverage evidence.</summary>
public sealed record ProcessExecutionTraceRecord
{
    /// <summary>Creates one canonical Process trace record.</summary>
    /// <param name="processId">Stable physical repository key assigned by the backing execution engine.</param>
    /// <param name="definition">Exact canonical Process definition.</param>
    /// <param name="processInstanceId">Canonical logical Process instance identity.</param>
    /// <param name="missingTracePrefixCount">
    /// Number of earliest activation-evidence entries that predate normalized trace retention.
    /// </param>
    /// <param name="traces">Retained normalized traces in canonical activation order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity, trace schema, definition, continuation, or activation identity is invalid or contradictory.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="missingTracePrefixCount"/> is negative.</exception>
    /// <exception cref="OverflowException">Missing and retained activation-evidence counts exceed the supported range.</exception>
    [JsonConstructor]
    public ProcessExecutionTraceRecord(
        string processId,
        ExecutionDefinitionReference definition,
        ProcessInstanceId processInstanceId,
        int missingTracePrefixCount,
        ImmutableArray<NormalizedExecutionTrace> traces = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
            throw new ArgumentException("A trace record requires a logical Process instance identity.", nameof(processInstanceId));
        if (missingTracePrefixCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(missingTracePrefixCount),
                missingTracePrefixCount,
                "A missing trace-prefix count cannot be negative.");
        }

        var normalized = traces.IsDefault ? ImmutableArray<NormalizedExecutionTrace>.Empty : traces;
        _ = checked(missingTracePrefixCount + normalized.Length);
        HashSet<(ProcessAttemptId Attempt, ActivationId Activation)> identities = [];
        foreach (var trace in normalized)
        {
            if (trace is null
                || trace.SchemaVersion != NormalizedExecutionTrace.CurrentSchemaVersion
                || trace.Kind != ProcessDefinitionDocuments.Kind
                || trace.Definition != definition
                || trace.Continuation is not { } continuation
                || continuation.ProcessInstanceId != processInstanceId)
            {
                throw new ArgumentException(
                    "Every retained trace must use the current Process trace schema and identify the record's exact definition and logical instance.",
                    nameof(traces));
            }
            if (!identities.Add((continuation.ProcessAttemptId, trace.Activation)))
            {
                throw new ArgumentException(
                    "A Process trace record cannot repeat an activation within one attempt.",
                    nameof(traces));
            }
        }

        ProcessId = processId;
        Definition = definition;
        ProcessInstanceId = processInstanceId;
        MissingTracePrefixCount = missingTracePrefixCount;
        Traces = normalized;
    }

    /// <summary>Stable physical repository key assigned by the backing execution engine.</summary>
    public string ProcessId { get; }

    /// <summary>Exact canonical Process definition shared by every retained trace.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Canonical logical Process instance identity shared by every retained trace.</summary>
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Number of earliest activation-evidence entries without a retained normalized trace.</summary>
    public int MissingTracePrefixCount { get; }

    /// <summary>Retained payload-safe normalized traces in canonical activation order.</summary>
    public ImmutableArray<NormalizedExecutionTrace> Traces { get; }

    /// <summary>Whether every retained activation-evidence entry has a normalized trace.</summary>
    [JsonIgnore]
    public bool IsComplete => MissingTracePrefixCount == 0;

    /// <summary>Total activation-evidence inventory represented by missing-prefix evidence plus retained traces.</summary>
    [JsonIgnore]
    public int ActivationEvidenceCount => checked(MissingTracePrefixCount + Traces.Length);
}
