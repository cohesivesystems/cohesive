namespace Cohesive.AI.Training;

/// <summary>
/// Reports that a stable logical submission identity is already bound to different training-request content.
/// </summary>
public sealed class TrainingJobSubmissionConflictException : InvalidOperationException
{
    /// <summary>
    /// Initializes a training submission identity conflict.
    /// </summary>
    /// <param name="submissionId">Stable logical identity whose content binding conflicted.</param>
    /// <param name="expectedRequestFingerprint">Fingerprint requested by the caller.</param>
    /// <param name="observedSubmissionId">Logical identity evidence observed at the provider.</param>
    /// <param name="observedRequestFingerprint">Different fingerprint evidence observed at the provider.</param>
    public TrainingJobSubmissionConflictException(
        string submissionId,
        string expectedRequestFingerprint,
        string? observedSubmissionId,
        string? observedRequestFingerprint)
        : base(CreateMessage(
            submissionId,
            expectedRequestFingerprint,
            observedSubmissionId,
            observedRequestFingerprint))
    {
        SubmissionId = submissionId;
        ExpectedRequestFingerprint = expectedRequestFingerprint;
        ObservedSubmissionId = observedSubmissionId;
        ObservedRequestFingerprint = observedRequestFingerprint;
    }

    /// <summary>Stable logical identity whose content binding conflicted.</summary>
    public string SubmissionId { get; }

    /// <summary>Fingerprint requested by the caller.</summary>
    public string ExpectedRequestFingerprint { get; }

    /// <summary>Logical identity evidence observed at the provider, or <see langword="null"/> when evidence is missing.</summary>
    public string? ObservedSubmissionId { get; }

    /// <summary>Different fingerprint observed at the provider, or <see langword="null"/> when evidence is missing.</summary>
    public string? ObservedRequestFingerprint { get; }

    static string CreateMessage(
        string submissionId,
        string expectedRequestFingerprint,
        string? observedSubmissionId,
        string? observedRequestFingerprint) =>
        $"Training submission '{submissionId}' with request fingerprint '{expectedRequestFingerprint}' conflicts " +
        $"with provider evidence for submission '{observedSubmissionId ?? "<missing>"}' and request fingerprint " +
        $"'{observedRequestFingerprint ?? "<missing>"}'.";
}
