using System.Text.Json.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Semantic before/after boundaries exposed by the reference materialization interpreters.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationExecutionBoundaryPoint
{
    /// <summary>A bounded baseline or change-source read has not yet started.</summary>
    BeforeSourceRead = 0,

    /// <summary>A bounded source page was read and validated but has not yet been hydrated.</summary>
    AfterSourceRead = 1,

    /// <summary>Relations hydration or inverse-impact interpretation has not yet started.</summary>
    BeforeHydration = 2,

    /// <summary>Hydration completed but its target intent has not yet been applied.</summary>
    AfterHydration = 3,

    /// <summary>One exact idempotent target batch has not yet been submitted.</summary>
    BeforeTargetBatch = 4,

    /// <summary>One exact target batch returned validated evidence that has not yet been interpreted.</summary>
    AfterTargetBatch = 5,

    /// <summary>One exact baseline or change-progress checkpoint has not yet been committed.</summary>
    BeforeCheckpointCommit = 6,

    /// <summary>One exact application checkpoint committed but later lifecycle work has not yet started.</summary>
    AfterCheckpointCommit = 7,

    /// <summary>A durably checkpointed source position has not yet been settled with its provider.</summary>
    BeforeSourceSettlement = 8,

    /// <summary>The provider acknowledged settlement but its receipt has not yet been retained.</summary>
    AfterSourceSettlement = 9,

    /// <summary>A durably prepared and validated generation has not yet been promoted.</summary>
    BeforeGenerationPromotion = 10,

    /// <summary>The target returned validated promotion evidence that has not yet been retained.</summary>
    AfterGenerationPromotion = 11
}

/// <summary>Attributable observation at one semantic materialization execution boundary.</summary>
public sealed record MaterializationExecutionBoundaryObservation
{
    /// <summary>Creates one exact materialization boundary observation.</summary>
    /// <param name="attempt">Process attempt owning the materialization generation.</param>
    /// <param name="generation">Exact candidate or active generation receiving the operation.</param>
    /// <param name="point">Semantic before/after boundary being observed.</param>
    /// <param name="scopeIdentity">Stable shard, change-feed, or target identity containing the operation.</param>
    /// <param name="operationIdentity">Stable page, batch, checkpoint, settlement, or promotion identity.</param>
    /// <param name="occurrence">
    /// Zero-based occurrence of this point for the exact operation within one physical interpreter invocation.
    /// Exact idempotent operations conventionally expose occurrence zero on every durable retry.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="attempt"/> or a string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A generation or string identity is empty, white space, or malformed Unicode.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="point"/> is unsupported or <paramref name="occurrence"/> is negative.
    /// </exception>
    public MaterializationExecutionBoundaryObservation(
        MaterializationRebuildAttempt attempt,
        MaterializationGenerationId generation,
        MaterializationExecutionBoundaryPoint point,
        string scopeIdentity,
        string operationIdentity,
        int occurrence)
    {
        Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        if (!Enum.IsDefined(point))
            throw new ArgumentOutOfRangeException(nameof(point), point, "Unsupported materialization execution boundary.");
        Generation = generation;
        Point = point;
        ScopeIdentity = MaterializationContract.RequireUnicodeIdentity(scopeIdentity, nameof(scopeIdentity));
        OperationIdentity = MaterializationContract.RequireUnicodeIdentity(operationIdentity, nameof(operationIdentity));
        if (occurrence < 0)
            throw new ArgumentOutOfRangeException(nameof(occurrence), occurrence, "A boundary occurrence cannot be negative.");
        Occurrence = occurrence;
    }

    /// <summary>Process attempt owning the materialization generation.</summary>
    public MaterializationRebuildAttempt Attempt { get; }

    /// <summary>Exact candidate or active generation receiving the operation.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Semantic before/after boundary being observed.</summary>
    public MaterializationExecutionBoundaryPoint Point { get; }

    /// <summary>Stable shard, change-feed, or target identity containing the operation.</summary>
    public string ScopeIdentity { get; }

    /// <summary>Stable page, batch, checkpoint, settlement, or promotion identity.</summary>
    public string OperationIdentity { get; }

    /// <summary>Zero-based occurrence of this point for the exact operation in one physical invocation.</summary>
    public int Occurrence { get; }
}

/// <summary>
/// Optional provider-neutral observation and deterministic interruption port for materialization conformance.
/// </summary>
public interface IMaterializationExecutionBoundaryObserver
{
    /// <summary>Observes one exact semantic boundary and may delay, cancel, or throw to simulate interruption.</summary>
    /// <param name="context">Explicit cancellation and tracing context.</param>
    /// <param name="observation">Exact attempt, generation, scope, operation, point, and occurrence evidence.</param>
    /// <returns>Completion when interpretation may cross the observed boundary.</returns>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask ObserveAsync(
        OperationContext context,
        MaterializationExecutionBoundaryObservation observation);
}

/// <summary>No-op boundary observer used by conventional materialization execution.</summary>
public sealed class NoOpMaterializationExecutionBoundaryObserver : IMaterializationExecutionBoundaryObserver
{
    NoOpMaterializationExecutionBoundaryObserver() { }

    /// <summary>Shared stateless no-op instance.</summary>
    public static NoOpMaterializationExecutionBoundaryObserver Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask ObserveAsync(
        OperationContext context,
        MaterializationExecutionBoundaryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observation);
        context.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
