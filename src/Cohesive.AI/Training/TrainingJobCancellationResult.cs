using System.Text.Json.Serialization;

namespace Cohesive.AI.Training;

/// <summary>Stable JSON names for the closed model-training cancellation result family.</summary>
public static class TrainingJobCancellationWireNames
{
    /// <summary>JSON property that discriminates cancellation result variants.</summary>
    public const string ResultDiscriminator = "$cancellationOutcome";

    /// <summary>Provider-accepted cancellation discriminator.</summary>
    public const string Accepted = "accepted";

    /// <summary>Already-terminal provider-job discriminator.</summary>
    public const string AlreadyTerminal = "alreadyTerminal";

    /// <summary>Authoritatively absent provider-job discriminator.</summary>
    public const string NotFound = "notFound";

    /// <summary>Deterministically rejected cancellation discriminator.</summary>
    public const string Rejected = "rejected";

    /// <summary>Unresolved cancellation-attempt discriminator.</summary>
    public const string Unresolved = "unresolved";
}

/// <summary>
/// Closed provider-neutral result of requesting cancellation for one exact training job.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = TrainingJobCancellationWireNames.ResultDiscriminator)]
[JsonDerivedType(typeof(TrainingJobCancellationResult.Accepted), TrainingJobCancellationWireNames.Accepted)]
[JsonDerivedType(typeof(TrainingJobCancellationResult.AlreadyTerminal), TrainingJobCancellationWireNames.AlreadyTerminal)]
[JsonDerivedType(typeof(TrainingJobCancellationResult.NotFound), TrainingJobCancellationWireNames.NotFound)]
[JsonDerivedType(typeof(TrainingJobCancellationResult.Rejected), TrainingJobCancellationWireNames.Rejected)]
[JsonDerivedType(typeof(TrainingJobCancellationResult.Unresolved), TrainingJobCancellationWireNames.Unresolved)]
public abstract record TrainingJobCancellationResult
{
    private protected TrainingJobCancellationResult(string jobId) =>
        JobId = RequireText(jobId, nameof(jobId));

    /// <summary>Stable provider training-job identity to which the result applies.</summary>
    public string JobId { get; }

    /// <summary>
    /// The provider accepted the cancellation request but has not yet supplied terminal job evidence.
    /// </summary>
    public sealed record Accepted : TrainingJobCancellationResult
    {
        /// <summary>Creates a provider-accepted, non-terminal cancellation result.</summary>
        /// <param name="jobId">Stable provider training-job identity.</param>
        /// <exception cref="ArgumentException"><paramref name="jobId"/> is empty or white-space.</exception>
        [JsonConstructor]
        public Accepted(string jobId)
            : base(jobId)
        {
        }
    }

    /// <summary>
    /// The provider job was terminal before cancellation could change its outcome.
    /// </summary>
    public sealed record AlreadyTerminal : TrainingJobCancellationResult
    {
        /// <summary>Creates an already-terminal cancellation result from provider-owned job evidence.</summary>
        /// <param name="trainingJob">Provider job state observed as completed, failed, or cancelled.</param>
        /// <exception cref="ArgumentNullException"><paramref name="trainingJob"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="trainingJob"/> is pending, running, cancellation-requested, or unknown.
        /// </exception>
        [JsonConstructor]
        public AlreadyTerminal(TrainingJobState trainingJob)
            : base(RequireTerminal(trainingJob).JobId) => TrainingJob = trainingJob;

        /// <summary>Exact provider-owned terminal state observed before cancellation.</summary>
        public TrainingJobState TrainingJob { get; }
    }

    /// <summary>The provider authoritatively reports no job for the supplied identity.</summary>
    public sealed record NotFound : TrainingJobCancellationResult
    {
        /// <summary>Creates an authoritatively absent cancellation result.</summary>
        /// <param name="jobId">Stable provider training-job identity that was not found.</param>
        /// <exception cref="ArgumentException"><paramref name="jobId"/> is empty or white-space.</exception>
        [JsonConstructor]
        public NotFound(string jobId)
            : base(jobId)
        {
        }
    }

    /// <summary>The provider deterministically rejected the cancellation request.</summary>
    public sealed record Rejected : TrainingJobCancellationResult
    {
        /// <summary>Creates a deterministic cancellation rejection.</summary>
        /// <param name="jobId">Stable provider training-job identity.</param>
        /// <param name="errorType">Stable provider or adapter rejection classification.</param>
        /// <param name="errorMessage">Human-readable rejection description.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="jobId"/>, <paramref name="errorType"/>, or <paramref name="errorMessage"/> is empty or
        /// white-space.
        /// </exception>
        [JsonConstructor]
        public Rejected(string jobId, string errorType, string errorMessage)
            : base(jobId)
        {
            ErrorType = RequireText(errorType, nameof(errorType));
            ErrorMessage = RequireText(errorMessage, nameof(errorMessage));
        }

        /// <summary>Stable provider or adapter rejection classification.</summary>
        public string ErrorType { get; }

        /// <summary>Human-readable rejection description.</summary>
        public string ErrorMessage { get; }
    }

    /// <summary>
    /// The provider cancellation outcome could not be established and must be reconciled before blind redispatch.
    /// </summary>
    public sealed record Unresolved : TrainingJobCancellationResult
    {
        /// <summary>Creates an unresolved or transient cancellation result.</summary>
        /// <param name="jobId">Stable provider training-job identity.</param>
        /// <param name="errorType">Stable provider or adapter failure classification.</param>
        /// <param name="errorMessage">Human-readable unresolved-attempt description.</param>
        /// <param name="isTransient">Whether another observation or attempt may succeed without configuration changes.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="jobId"/>, <paramref name="errorType"/>, or <paramref name="errorMessage"/> is empty or
        /// white-space.
        /// </exception>
        [JsonConstructor]
        public Unresolved(
            string jobId,
            string errorType,
            string errorMessage,
            bool isTransient)
            : base(jobId)
        {
            ErrorType = RequireText(errorType, nameof(errorType));
            ErrorMessage = RequireText(errorMessage, nameof(errorMessage));
            IsTransient = isTransient;
        }

        /// <summary>Stable provider or adapter failure classification.</summary>
        public string ErrorType { get; }

        /// <summary>Human-readable unresolved-attempt description.</summary>
        public string ErrorMessage { get; }

        /// <summary>Whether another observation or attempt may succeed without configuration changes.</summary>
        public bool IsTransient { get; }
    }

    static TrainingJobState RequireTerminal(TrainingJobState trainingJob)
    {
        ArgumentNullException.ThrowIfNull(trainingJob);
        if (trainingJob.Status is not (TrainingJobStatus.Completed or TrainingJobStatus.Failed or TrainingJobStatus.Cancelled))
        {
            throw new ArgumentException(
                $"Training job '{trainingJob.JobId}' must be terminal, but its status is '{trainingJob.Status}'.",
                nameof(trainingJob));
        }

        return trainingJob;
    }

    static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null, empty, or white-space.", parameterName);

        return value;
    }
}
