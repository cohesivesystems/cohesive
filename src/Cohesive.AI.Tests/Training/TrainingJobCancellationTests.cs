using System.Text.Json;
using Cohesive.AI.Training;

namespace Cohesive.AI.Tests.Training;

public sealed class TrainingJobCancellationTests
{
    [Fact]
    public void Constructor_BindsExactCancellationAndJobIdentity()
    {
        var cancellation = new TrainingJobCancellation(
            cancellationId: "training-run/42/cancel/1",
            jobId: "provider-job-42");

        Assert.Equal("training-run/42/cancel/1", cancellation.CancellationId);
        Assert.Equal("provider-job-42", cancellation.JobId);
        Assert.Equal(
            cancellation,
            new TrainingJobCancellation(
                cancellationId: "training-run/42/cancel/1",
                jobId: "provider-job-42"));
        Assert.NotEqual(
            cancellation,
            new TrainingJobCancellation(
                cancellationId: "training-run/42/cancel/2",
                jobId: "provider-job-42"));
    }

    [Theory]
    [InlineData(null, "provider-job-42")]
    [InlineData("", "provider-job-42")]
    [InlineData(" ", "provider-job-42")]
    [InlineData("training-run/42/cancel/1", null)]
    [InlineData("training-run/42/cancel/1", "")]
    [InlineData("training-run/42/cancel/1", " ")]
    public void Constructor_RejectsMissingIdentity(string? cancellationId, string? jobId)
    {
        _ = Assert.ThrowsAny<ArgumentException>(() => new TrainingJobCancellation(
            cancellationId!,
            jobId!));
    }

    [Fact]
    public void JsonRoundTrip_PreservesExactCancellationInput()
    {
        var cancellation = new TrainingJobCancellation(
            cancellationId: "training-run/42/cancel/1",
            jobId: "provider-job-42");

        var json = JsonSerializer.Serialize(cancellation);
        var roundTripped = JsonSerializer.Deserialize<TrainingJobCancellation>(json);

        Assert.Equal(cancellation, roundTripped);
    }

    public static TheoryData<TrainingJobCancellationResult, string> PortableResults => new()
    {
        {
            new TrainingJobCancellationResult.Accepted(jobId: "provider-job-42"),
            TrainingJobCancellationWireNames.Accepted
        },
        {
            new TrainingJobCancellationResult.AlreadyTerminal(new(
                JobId: "provider-job-42",
                Status: TrainingJobStatus.Completed,
                Result: null,
                Failure: null)),
            TrainingJobCancellationWireNames.AlreadyTerminal
        },
        {
            new TrainingJobCancellationResult.NotFound(jobId: "provider-job-42"),
            TrainingJobCancellationWireNames.NotFound
        },
        {
            new TrainingJobCancellationResult.Rejected(
                jobId: "provider-job-42",
                errorType: "Provider.Forbidden",
                errorMessage: "The caller cannot cancel this job."),
            TrainingJobCancellationWireNames.Rejected
        },
        {
            new TrainingJobCancellationResult.Unresolved(
                jobId: "provider-job-42",
                errorType: "Provider.Timeout",
                errorMessage: "The provider response was not observed.",
                isTransient: true),
            TrainingJobCancellationWireNames.Unresolved
        }
    };

    [Theory]
    [MemberData(nameof(PortableResults))]
    public void ResultJsonRoundTrip_PreservesClosedVariant(
        TrainingJobCancellationResult result,
        string discriminator)
    {
        var json = JsonSerializer.Serialize(result);
        var roundTripped = JsonSerializer.Deserialize<TrainingJobCancellationResult>(json);

        Assert.Contains(
            $"\"{TrainingJobCancellationWireNames.ResultDiscriminator}\":\"{discriminator}\"",
            json,
            StringComparison.Ordinal);
        Assert.Equal(result, roundTripped);
        Assert.Equal(result.GetType(), roundTripped?.GetType());
    }

    [Theory]
    [InlineData(TrainingJobStatus.Completed)]
    [InlineData(TrainingJobStatus.Failed)]
    [InlineData(TrainingJobStatus.Cancelled)]
    public void AlreadyTerminal_AcceptsOnlyProviderTerminalState(TrainingJobStatus status)
    {
        var state = State(status);

        var result = new TrainingJobCancellationResult.AlreadyTerminal(state);

        Assert.Equal(state, result.TrainingJob);
        Assert.Equal(state.JobId, result.JobId);
    }

    [Theory]
    [InlineData(TrainingJobStatus.Pending)]
    [InlineData(TrainingJobStatus.Running)]
    [InlineData(TrainingJobStatus.CancellationRequested)]
    [InlineData(TrainingJobStatus.Unknown)]
    public void AlreadyTerminal_RejectsNonTerminalState(TrainingJobStatus status)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new TrainingJobCancellationResult.AlreadyTerminal(State(status)));

        Assert.Contains($"status is '{status}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationRequested_IsAStableDistinctLifecycleState()
    {
        Assert.Equal(6, (int)TrainingJobStatus.CancellationRequested);
        Assert.NotEqual(TrainingJobStatus.Running, TrainingJobStatus.CancellationRequested);
        Assert.NotEqual(TrainingJobStatus.Cancelled, TrainingJobStatus.CancellationRequested);
    }

    [Fact]
    public void Conflict_PreservesRequestedAndObservedIdentityEvidence()
    {
        var error = new TrainingJobCancellationConflictException(
            cancellationId: "training-run/42/cancel/1",
            expectedJobId: "provider-job-42",
            observedJobId: "provider-job-41");

        Assert.Equal("training-run/42/cancel/1", error.CancellationId);
        Assert.Equal("provider-job-42", error.ExpectedJobId);
        Assert.Equal("provider-job-41", error.ObservedJobId);
        Assert.Contains("provider-job-42", error.Message, StringComparison.Ordinal);
        Assert.Contains("provider-job-41", error.Message, StringComparison.Ordinal);
    }

    static TrainingJobState State(TrainingJobStatus status) => new(
        JobId: "provider-job-42",
        Status: status,
        Result: null,
        Failure: null);
}
