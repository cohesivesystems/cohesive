using System.Text.Json.Serialization;

namespace Cohesive.AI.Training;

/// <summary>
/// Binds one stable logical cancellation identity to one provider training-job identity.
/// </summary>
/// <remarks>
/// The complete pair is the exact cancellation input. Replaying the pair requests the same logical operation;
/// observing the same <see cref="CancellationId"/> bound to another <see cref="JobId"/> is a conflict.
/// </remarks>
public sealed record TrainingJobCancellation
{
    /// <summary>Creates an exact model-training cancellation operation.</summary>
    /// <param name="cancellationId">Stable logical identity supplied by the orchestrating caller.</param>
    /// <param name="jobId">Stable provider training-job identity to cancel.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="cancellationId"/> or <paramref name="jobId"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="cancellationId"/> or <paramref name="jobId"/> is empty or white-space.
    /// </exception>
    [JsonConstructor]
    public TrainingJobCancellation(string cancellationId, string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cancellationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        CancellationId = cancellationId;
        JobId = jobId;
    }

    /// <summary>Stable logical identity supplied by the orchestrating caller.</summary>
    public string CancellationId { get; }

    /// <summary>Stable provider training-job identity to cancel.</summary>
    public string JobId { get; }
}
