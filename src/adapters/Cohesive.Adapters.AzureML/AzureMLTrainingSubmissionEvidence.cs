using Cohesive.AI.Training;

namespace Cohesive.Adapters.AzureML;

static class AzureMLTrainingSubmissionEvidence
{
    internal const string SubmissionIdProperty = "cohesive.submissionId";
    internal const string RequestFingerprintProperty = "cohesive.requestFingerprint";

    internal static void WriteTo(
        IDictionary<string, string> properties,
        TrainingJobSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(submission);

        properties[SubmissionIdProperty] = submission.SubmissionId;
        properties[RequestFingerprintProperty] = submission.RequestFingerprint;
    }

    internal static void EnsureMatches(
        IDictionary<string, string> properties,
        TrainingJobSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(submission);

        properties.TryGetValue(SubmissionIdProperty, out var observedSubmissionId);
        properties.TryGetValue(RequestFingerprintProperty, out var observedRequestFingerprint);
        if (!StringComparer.Ordinal.Equals(submission.SubmissionId, observedSubmissionId)
            || !StringComparer.Ordinal.Equals(submission.RequestFingerprint, observedRequestFingerprint))
        {
            throw new TrainingJobSubmissionConflictException(
                submission.SubmissionId,
                submission.RequestFingerprint,
                observedSubmissionId,
                observedRequestFingerprint);
        }
    }
}
