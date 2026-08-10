using Cohesive.Execution;

namespace Cohesive.Processes.Runtime;

/// <summary>Reads canonical Process explanation artifacts from retained runtime evidence.</summary>
/// <remarks>
/// Implementations acquire existing definition, compilation, realization, trace, and status evidence. They do not
/// re-execute a Process or define an alternative explanation model.
/// </remarks>
public interface IProcessExecutionExplainRepository
{
    /// <summary>Returns the canonical explanation for one logical Process in a trusted authority scope.</summary>
    /// <param name="context">Operation context that supplies cancellation for the read.</param>
    /// <param name="authorityScope">
    /// Server-resolved authority and optional tenant boundary that isolates the physical execution.
    /// </param>
    /// <param name="processInstanceId">Canonical logical Process instance identity.</param>
    /// <returns>
    /// The canonical explanation artifact, or <see langword="null"/> when the execution is not retained or visible
    /// at the supplied trusted address. Active and pending executions may return partial lifecycle artifacts without
    /// trace evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="authorityScope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="processInstanceId"/> is default.</exception>
    /// <exception cref="KeyNotFoundException">No exact deployed definition plan can explain the execution.</exception>
    /// <exception cref="InvalidOperationException">
    /// Retained definition, status, trace, realization, or projection evidence is malformed or contradictory.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The selected repository cannot acquire canonical explanation evidence.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Canonical explanation content cannot be serialized for deterministic identity.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    ValueTask<ExecutionExplainArtifact?> GetExplainAsync(
        OperationContext context,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId);

    /// <summary>Returns the canonical explanation for one retained physical execution.</summary>
    /// <param name="context">Operation context that supplies cancellation for the read.</param>
    /// <param name="processId">Stable physical repository key assigned by the backing execution engine.</param>
    /// <returns>
    /// The canonical explanation artifact, or <see langword="null"/> when the execution is not retained.
    /// Active and pending executions may return partial lifecycle artifacts without trace evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="processId"/> is empty or whitespace.</exception>
    /// <exception cref="KeyNotFoundException">No exact deployed definition plan can explain the execution.</exception>
    /// <exception cref="InvalidOperationException">
    /// Retained definition, status, trace, realization, or projection evidence is malformed or contradictory.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The selected repository cannot acquire canonical explanation evidence.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Canonical explanation content cannot be serialized for deterministic identity.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested through <paramref name="context"/>.</exception>
    ValueTask<ExecutionExplainArtifact?> GetExplainAsync(OperationContext context, string processId);
}
