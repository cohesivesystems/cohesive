namespace Cohesive.AI.Training;

/// <summary>
/// Reports that a stable logical cancellation identity is already bound to another training job.
/// </summary>
public sealed class TrainingJobCancellationConflictException : InvalidOperationException
{
    /// <summary>Initializes a training cancellation identity conflict.</summary>
    /// <param name="cancellationId">Stable logical cancellation identity whose job binding conflicted.</param>
    /// <param name="expectedJobId">Provider job identity requested by the caller.</param>
    /// <param name="observedJobId">Different provider job identity observed for the cancellation identity.</param>
    public TrainingJobCancellationConflictException(
        string cancellationId,
        string expectedJobId,
        string? observedJobId)
        : base(CreateMessage(cancellationId, expectedJobId, observedJobId))
    {
        CancellationId = cancellationId;
        ExpectedJobId = expectedJobId;
        ObservedJobId = observedJobId;
    }

    /// <summary>Stable logical cancellation identity whose job binding conflicted.</summary>
    public string CancellationId { get; }

    /// <summary>Provider job identity requested by the caller.</summary>
    public string ExpectedJobId { get; }

    /// <summary>Different provider job identity observed, or <see langword="null"/> when evidence is missing.</summary>
    public string? ObservedJobId { get; }

    static string CreateMessage(
        string cancellationId,
        string expectedJobId,
        string? observedJobId) =>
        $"Training cancellation '{cancellationId}' for job '{expectedJobId}' conflicts with provider evidence " +
        $"for job '{observedJobId ?? "<missing>"}'.";
}
