using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Runtime;

/// <summary>Reads retained payload-safe canonical traces without loading Process values into status projections.</summary>
public interface IProcessExecutionTraceRepository
{
    /// <summary>Reads canonical traces retained for one physical execution repository key.</summary>
    /// <param name="context">Operation context that supplies cancellation for the read.</param>
    /// <param name="processId">Stable physical repository key assigned by the backing execution engine.</param>
    /// <returns>An explicit availability result and portable trace artifact when available.</returns>
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

    /// <summary>Reads retained canonical traces by trusted authority scope and logical Process identity.</summary>
    /// <param name="context">Operation context that supplies cancellation for the read.</param>
    /// <param name="authorityScope">Exact trusted authority and optional tenant isolating the execution.</param>
    /// <param name="processInstanceId">Canonical logical Process instance identity.</param>
    /// <returns>An explicit availability result and portable trace artifact when available.</returns>
    /// <remarks>
    /// Application-facing reads use this logical address. Implementations may derive a provider-specific physical
    /// key, but neither the caller nor the returned artifact observes that representation.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="authorityScope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="processInstanceId"/> is the default identity.</exception>
    /// <exception cref="InvalidOperationException">Retained canonical evidence is malformed or contradictory.</exception>
    /// <exception cref="NotSupportedException">The selected repository cannot retrieve canonical traces.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    ValueTask<ProcessExecutionTraceReadResult> GetTracesAsync(
        OperationContext context,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId);
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
    /// <param name="artifact">Portable trace artifact exactly when <paramref name="state"/> is available.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unspecified or unsupported.</exception>
    /// <exception cref="ArgumentException">Artifact presence contradicts <paramref name="state"/>.</exception>
    [JsonConstructor]
    public ProcessExecutionTraceReadResult(
        ProcessExecutionTraceReadState state,
        ProcessExecutionTraceArtifact? artifact = null)
    {
        if (!Enum.IsDefined(state) || state == ProcessExecutionTraceReadState.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "A trace read requires an explicit state.");
        }
        if ((state == ProcessExecutionTraceReadState.Available) != (artifact is not null))
        {
            throw new ArgumentException(
                "A portable trace artifact exists exactly when the trace read is available.",
                nameof(artifact));
        }

        State = state;
        Artifact = artifact;
    }

    /// <summary>Explicit availability disposition.</summary>
    public ProcessExecutionTraceReadState State { get; }

    /// <summary>Portable canonical trace evidence when <see cref="State"/> is available.</summary>
    public ProcessExecutionTraceArtifact? Artifact { get; }

    /// <summary>Creates a result for an execution that is not retained.</summary>
    public static ProcessExecutionTraceReadResult NotFound() =>
        new(ProcessExecutionTraceReadState.NotFound);

    /// <summary>Creates a result for a retained execution that is still active.</summary>
    public static ProcessExecutionTraceReadResult InProgress() =>
        new(ProcessExecutionTraceReadState.InProgress);

    /// <summary>Creates a result containing available canonical trace coverage.</summary>
    /// <param name="artifact">Validated portable trace artifact.</param>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    public static ProcessExecutionTraceReadResult Available(ProcessExecutionTraceArtifact artifact) =>
        new(ProcessExecutionTraceReadState.Available, artifact ?? throw new ArgumentNullException(nameof(artifact)));

    /// <summary>Creates a result for a terminal execution that has no canonical result artifact.</summary>
    public static ProcessExecutionTraceReadResult TerminalArtifactUnavailable() =>
        new(ProcessExecutionTraceReadState.TerminalArtifactUnavailable);
}
