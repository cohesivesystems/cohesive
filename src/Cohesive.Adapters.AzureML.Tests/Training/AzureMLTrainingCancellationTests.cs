using Azure;
using Azure.Core;
using Azure.ResourceManager.MachineLearning.Models;
using Cohesive.AI.Training;
using Cohesive.Adapters.AzureML;

namespace Cohesive.Adapters.AzureML.Tests.Training;

public sealed class AzureMLTrainingCancellationTests
{
    [Theory]
    [InlineData(TrainingJobStatus.Completed)]
    [InlineData(TrainingJobStatus.Failed)]
    [InlineData(TrainingJobStatus.Cancelled)]
    public async Task TerminalProviderState_WinsWithoutCancellationDispatch(
        TrainingJobStatus status)
    {
        var operations = new ScriptedCancellationOperations(State(status));
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        var terminal = Assert.IsType<TrainingJobCancellationResult.AlreadyTerminal>(result);
        Assert.Equal(status, terminal.TrainingJob.Status);
        Assert.Equal(1, operations.ObservationCount);
        Assert.Equal(0, operations.CancellationRequestCount);
    }

    [Fact]
    public async Task MissingProviderJob_ReturnsAuthoritativeNotFound()
    {
        var operations = new ScriptedCancellationOperations((object?)null);
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        Assert.IsType<TrainingJobCancellationResult.NotFound>(result);
        Assert.Equal(JobId, result.JobId);
        Assert.Equal(0, operations.CancellationRequestCount);
    }

    [Fact]
    public async Task CancellationRequested_ReplaysAsAcceptedWithoutRedispatch()
    {
        var operations = new ScriptedCancellationOperations(
            State(TrainingJobStatus.Running),
            State(TrainingJobStatus.CancellationRequested));
        var trainer = Trainer(operations);
        var cancellation = Cancellation();

        var first = await trainer.CancelAsync(cancellation);
        var replay = await trainer.CancelAsync(cancellation);

        Assert.IsType<TrainingJobCancellationResult.Accepted>(first);
        Assert.IsType<TrainingJobCancellationResult.Accepted>(replay);
        Assert.Equal(2, operations.ObservationCount);
        Assert.Equal(1, operations.CancellationRequestCount);
    }

    [Fact]
    public async Task RunningProviderJob_DispatchesCancellationOnce()
    {
        var operations = new ScriptedCancellationOperations(State(TrainingJobStatus.Running));
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        Assert.IsType<TrainingJobCancellationResult.Accepted>(result);
        Assert.Equal(JobId, operations.RequestedJobIds.Single());
    }

    [Fact]
    public async Task UnknownNonterminalProviderState_StillAttemptsCancellation()
    {
        var operations = new ScriptedCancellationOperations(State(TrainingJobStatus.Unknown));
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        Assert.IsType<TrainingJobCancellationResult.Accepted>(result);
        Assert.Equal(1, operations.CancellationRequestCount);
    }

    [Fact]
    public async Task CallerCancellation_StopsTheWaitWithoutDispatchingProviderCancellation()
    {
        var operations = new ScriptedCancellationOperations(State(TrainingJobStatus.Running));
        var trainer = Trainer(operations);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.CancelAsync(Cancellation(), cancellationSource.Token).AsTask());

        Assert.Equal(0, operations.ObservationCount);
        Assert.Equal(0, operations.CancellationRequestCount);
    }

    [Theory]
    [InlineData(TrainingJobStatus.Completed)]
    [InlineData(TrainingJobStatus.Failed)]
    [InlineData(TrainingJobStatus.Cancelled)]
    public async Task TerminalRace_IsReobservedWithoutOverwritingProviderState(
        TrainingJobStatus terminalStatus)
    {
        var operations = new ScriptedCancellationOperations(
            State(TrainingJobStatus.Running),
            State(terminalStatus))
        {
            CancellationFailure = Failure(status: 409, errorCode: "Conflict")
        };
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        var terminal = Assert.IsType<TrainingJobCancellationResult.AlreadyTerminal>(result);
        Assert.Equal(terminalStatus, terminal.TrainingJob.Status);
        Assert.Equal(2, operations.ObservationCount);
        Assert.Equal(1, operations.CancellationRequestCount);
    }

    [Fact]
    public async Task AmbiguousDispatch_ReconcilesCancellationRequestedAsAccepted()
    {
        var operations = new ScriptedCancellationOperations(
            State(TrainingJobStatus.Running),
            State(TrainingJobStatus.CancellationRequested))
        {
            CancellationFailure = Failure(status: 408, errorCode: "Timeout")
        };
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        Assert.IsType<TrainingJobCancellationResult.Accepted>(result);
        Assert.Equal(2, operations.ObservationCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task AmbiguousOrTransientDispatch_RemainsUnresolvedWhenJobStillRuns(int status)
    {
        var operations = new ScriptedCancellationOperations(
            State(TrainingJobStatus.Running),
            State(TrainingJobStatus.Running))
        {
            CancellationFailure = Failure(status, errorCode: null)
        };
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        var unresolved = Assert.IsType<TrainingJobCancellationResult.Unresolved>(result);
        Assert.True(unresolved.IsTransient);
        Assert.Equal(JobId, unresolved.JobId);
    }

    [Fact]
    public async Task DeterministicProviderRefusal_IsRejectedAfterStateReconciliation()
    {
        var operations = new ScriptedCancellationOperations(
            State(TrainingJobStatus.Running),
            State(TrainingJobStatus.Running))
        {
            CancellationFailure = Failure(status: 403, errorCode: "AuthorizationFailed")
        };
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        var rejected = Assert.IsType<TrainingJobCancellationResult.Rejected>(result);
        Assert.Equal("AzureML.AuthorizationFailed", rejected.ErrorType);
    }

    [Fact]
    public async Task CancellationNotFound_IsAuthoritativeOnlyWhenReobservationIsAbsent()
    {
        var absent = new ScriptedCancellationOperations(
            State(TrainingJobStatus.Running),
            (object?)null)
        {
            CancellationFailure = Failure(status: 404, errorCode: "NotFound")
        };
        var inconsistent = new ScriptedCancellationOperations(
            State(TrainingJobStatus.Running),
            State(TrainingJobStatus.Running))
        {
            CancellationFailure = Failure(status: 404, errorCode: "NotFound")
        };

        var absentResult = await Trainer(absent).CancelAsync(Cancellation());
        var inconsistentResult = await Trainer(inconsistent).CancelAsync(Cancellation());

        Assert.IsType<TrainingJobCancellationResult.NotFound>(absentResult);
        var unresolved = Assert.IsType<TrainingJobCancellationResult.Unresolved>(inconsistentResult);
        Assert.False(unresolved.IsTransient);
    }

    [Theory]
    [InlineData(404, typeof(TrainingJobCancellationResult.NotFound))]
    [InlineData(403, typeof(TrainingJobCancellationResult.Rejected))]
    [InlineData(429, typeof(TrainingJobCancellationResult.Unresolved))]
    public async Task InitialObservationFailure_IsClassifiedWithoutCancellationDispatch(
        int status,
        Type expectedType)
    {
        var operations = new ScriptedCancellationOperations(Failure(status, errorCode: null));
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        Assert.IsType(expectedType, result);
        Assert.Equal(0, operations.CancellationRequestCount);
    }

    [Fact]
    public async Task FailedReconciliation_PreservesDispatchAndObservationAmbiguity()
    {
        var operations = new ScriptedCancellationOperations(
            State(TrainingJobStatus.Running),
            Failure(status: 503, errorCode: "ServiceUnavailable"))
        {
            CancellationFailure = Failure(status: 409, errorCode: "Conflict")
        };
        var trainer = Trainer(operations);

        var result = await trainer.CancelAsync(Cancellation());

        var unresolved = Assert.IsType<TrainingJobCancellationResult.Unresolved>(result);
        Assert.True(unresolved.IsTransient);
        Assert.Contains("cancellation failed", unresolved.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconciliation failed", unresolved.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureCancelRequested_MapsToCanonicalCancellationRequested()
    {
        var status = AzureMLModelTrainer.MapStatus(MachineLearningJobStatus.CancelRequested);

        Assert.Equal(TrainingJobStatus.CancellationRequested, status);
    }

    const string JobId = "azureml-job-42";

    static AzureMLModelTrainer Trainer(
        IAzureMLTrainingJobCancellationOperations operations) => new(
        new StubTokenCredential(),
        new(
            SubscriptionId: "00000000-0000-0000-0000-000000000000",
            ResourceGroupName: "training-rg",
            WorkspaceName: "training-workspace"),
        operations);

    static TrainingJobCancellation Cancellation() => new(
        cancellationId: "training-run/42/cancel/1",
        jobId: JobId);

    static TrainingJobState State(TrainingJobStatus status) => new(
        JobId: JobId,
        Status: status,
        Result: null,
        Failure: null);

    static RequestFailedException Failure(int status, string? errorCode) => new(
        status,
        $"Azure ML request failed with status {status}.",
        errorCode,
        innerException: null);

    sealed class ScriptedCancellationOperations : IAzureMLTrainingJobCancellationOperations
    {
        readonly Queue<object?> observations;

        public ScriptedCancellationOperations(params object?[] observations) =>
            this.observations = new(observations);

        public RequestFailedException? CancellationFailure { get; init; }

        public int ObservationCount { get; private set; }

        public int CancellationRequestCount { get; private set; }

        public List<string> RequestedJobIds { get; } = [];

        public ValueTask<TrainingJobState?> ObserveAsync(
            string jobId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ObservationCount++;
            Assert.Equal(JobId, jobId);
            var observation = observations.Dequeue();
            if (observation is Exception error)
                throw error;

            return ValueTask.FromResult((TrainingJobState?)observation);
        }

        public ValueTask RequestCancellationAsync(
            string jobId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CancellationRequestCount++;
            RequestedJobIds.Add(jobId);
            if (CancellationFailure is { } error)
                throw error;

            return ValueTask.CompletedTask;
        }
    }

    sealed class StubTokenCredential : TokenCredential
    {
        static readonly AccessToken Token = new("test-token", DateTimeOffset.MaxValue);

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => Token;

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(Token);
    }
}
