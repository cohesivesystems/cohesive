using System.Text.RegularExpressions;
using Cohesive.AI.Training;
using Cohesive.Adapters.AzureML;

namespace Cohesive.Adapters.AzureML.Tests.Training;

public sealed partial class AzureMLTrainingSubmissionTests
{
    [Theory]
    [InlineData("training-run/42/submission")]
    [InlineData("TRAINING RUN 42")]
    [InlineData("訓練/42")]
    [InlineData("a")]
    public void JobIdentity_IsDeterministicValidAndBoundToExactSubmissionIdentity(string submissionId)
    {
        var first = AzureMLModelTrainer.CreateJobId(submissionId);
        var second = AzureMLModelTrainer.CreateJobId(submissionId);

        Assert.Equal(first, second);
        Assert.Matches(JobIdPattern(), first);
        Assert.InRange(first.Length, 1, 255);
    }

    [Fact]
    public void JobIdentity_DistinguishesIdentitiesWithTheSameReadablePrefix()
    {
        var first = AzureMLModelTrainer.CreateJobId("training-run/42");
        var second = AzureMLModelTrainer.CreateJobId("training_run_42");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void JobIdentity_IsPinnedAcrossAdapterVersions()
    {
        var jobId = AzureMLModelTrainer.CreateJobId("training-run/42/submission");

        Assert.Equal(
            "train-training-run-42-submission-368d38ed292aafb0676160e606d36a7978878f1b176b3a59c7e3b62c973c1b9c",
            jobId);
    }

    [Fact]
    public void Evidence_RoundTripsExactSubmissionBinding()
    {
        var submission = Submission("submission-42", outputModelName: "model-v1");
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);

        AzureMLTrainingSubmissionEvidence.WriteTo(properties, submission);

        AzureMLTrainingSubmissionEvidence.EnsureMatches(properties, submission);
        Assert.Equal(submission.SubmissionId, properties[AzureMLTrainingSubmissionEvidence.SubmissionIdProperty]);
        Assert.Equal(
            submission.RequestFingerprint,
            properties[AzureMLTrainingSubmissionEvidence.RequestFingerprintProperty]);
    }

    [Fact]
    public void Evidence_RejectsIdentityReuseWithDifferentRequestContent()
    {
        var accepted = Submission("submission-42", outputModelName: "model-v1");
        var conflicting = Submission("submission-42", outputModelName: "model-v2");
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        AzureMLTrainingSubmissionEvidence.WriteTo(properties, accepted);

        var error = Assert.Throws<TrainingJobSubmissionConflictException>(
            () => AzureMLTrainingSubmissionEvidence.EnsureMatches(properties, conflicting));

        Assert.Equal(conflicting.SubmissionId, error.SubmissionId);
        Assert.Equal(conflicting.RequestFingerprint, error.ExpectedRequestFingerprint);
        Assert.Equal(accepted.SubmissionId, error.ObservedSubmissionId);
        Assert.Equal(accepted.RequestFingerprint, error.ObservedRequestFingerprint);
    }

    [Fact]
    public void Evidence_RejectsAProviderJobWithoutCohesiveSubmissionEvidence()
    {
        var submission = Submission("submission-42", outputModelName: "model-v1");

        var error = Assert.Throws<TrainingJobSubmissionConflictException>(
            () => AzureMLTrainingSubmissionEvidence.EnsureMatches(
                new Dictionary<string, string>(StringComparer.Ordinal),
                submission));

        Assert.Null(error.ObservedSubmissionId);
        Assert.Null(error.ObservedRequestFingerprint);
    }

    static TrainingJobSubmission Submission(string submissionId, string outputModelName) => new(
        submissionId,
        new(
            ModelName: "semantic-matcher",
            BaseVersion: null,
            Datasets: [],
            Code: null,
            OutputModelName: outputModelName,
            ExperimentName: null,
            ComputeTarget: null,
            ConfigJson: "{}"));

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobIdPattern();
}
