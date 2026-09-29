using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed partial class ProcessDurableRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncHost_CommitRecoveryAndNewDriverReplayRetainOneObservation(bool crashAfterCommit)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime/async-recovery/{crashAfterCommit}",
            semanticVariant: $"async-recovery/{crashAfterCommit}");
        var crash = ProcessStoreCrashScript.Once(ProcessStoreMutationKind.AggregateCommit,
            crashAfterCommit ? ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn : ProcessStoreCrashPhase.BeforeAtomicCommit);
        var store = new InMemoryProcessDurableStore(crash.ShouldCrash);
        var host = new AsyncRecordingHost(fixture.OperationResult);
        var runtime = AsyncRuntime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc), fixture.Plan, fixture.Start);
        var continuation = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint.ContinuationIdentity;

        var activated = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), fixture.Plan, continuation, fixture.Activation);

        Assert.True(crash.IsComplete);
        Assert.Equal(crashAfterCommit ? ProcessDurableRuntimeDisposition.Replayed : ProcessDurableRuntimeDisposition.Applied,
            activated.Disposition);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot).Checkpoint;
        Assert.Equal(fixture.OperationResult, Assert.Single(checkpoint.Operations).Result);
        Assert.Single(checkpoint.Activations);
        Assert.Single(checkpoint.Emissions);
        Assert.Single(checkpoint.DurableOperations);
        Assert.Equal(1, host.RelationCalls);

        // A new worker object must recover from stored evidence, not an activation-local cache.
        var restartedHost = new AsyncRecordingHost(fixture.OperationResult);
        var restarted = AsyncRuntime(store, fixture, restartedHost);
        var replay = await restarted.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), fixture.Plan, continuation, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(0, restartedHost.RelationCalls);
        Assert.Equal(0, restarted.RetainedInstanceGateCount);
    }

    [Fact]
    public async Task AsyncHost_CancellationAfterAwaitDoesNotCommitPartialEvidence_AndCanRetry()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/async-cancellation", semanticVariant: "async-cancellation");
        var store = new InMemoryProcessDurableStore();
        using var cancellation = new CancellationTokenSource();
        var host = new AsyncRecordingHost(fixture.OperationResult, cancellation.Cancel);
        var runtime = AsyncRuntime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc), fixture.Plan, fixture.Start);
        var continuation = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint.ContinuationIdentity;
        var context = OperationContext.Create(
            timeProvider: new FixedTimeProvider(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ActivateAsync(
            context, fixture.Plan, continuation, fixture.Activation));

        var retained = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), continuation.ProcessInstanceId));
        Assert.Empty(retained.Checkpoint.Operations);
        Assert.Empty(retained.Checkpoint.Activations);
        Assert.Empty(retained.Checkpoint.Emissions);
        Assert.Equal(0, runtime.RetainedInstanceGateCount);
        var recoveredHost = new AsyncRecordingHost(fixture.OperationResult);
        var recovered = await AsyncRuntime(store, fixture, recoveredHost).ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), fixture.Plan, continuation, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, recovered.Disposition);
        Assert.Equal(1, recoveredHost.RelationCalls);
    }

    static ProcessDurableRuntime AsyncRuntime(
        IProcessDurableStore store, ProcessDurabilityTestFixture fixture, IAsyncProcessReferenceHost host) =>
        new(store, host, new("worker/durable-runtime-tests", WorkerLease),
            new BindingResolver(fixture.DurableOperation.Binding));

    sealed class AsyncRecordingHost(ProcessOperationResult result, Action? afterAwait = null) : IAsyncProcessReferenceHost
    {
        internal int RelationCalls { get; private set; }

        public async ValueTask<ProcessOperationResult> EvaluateRelationAsync(
            OperationContext context, ProcessRelationEvaluation evaluation)
        {
            context.ThrowIfCancellationRequested();
            RelationCalls++;
            await Task.Yield();
            afterAwait?.Invoke();
            return result;
        }

        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(
            OperationContext context, ProcessTransitionInvocation invocation) => throw new InvalidOperationException("Unexpected Transition.");

        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(
            OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException("Unexpected Signal.");
    }
}
