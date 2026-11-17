using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed partial class ProcessDurableRuntimeTests
{
    [Fact]
    public async Task TwoChildScope_RecoversPartialClosureWithoutPrematureFinalization()
    {
        var (fixture, child) = CancellationFinalizerDurabilityTestFixture.CreateTwoChildScope();
        var store = new InMemoryProcessDurableStore();
        var bindings = new DurableRequestBindingCatalog(
        [
            fixture.Binding,
            child.BindDurably(maxAttempts: 3, claimLease: TimeSpan.FromMinutes(1),
                DurableOperationIdempotencyEvidence.TargetDeduplication,
                reconciliationTarget: new(child.Process.Reference, new("return")))
        ]);
        ProcessDurableRuntime RecoverWorker() => Runtime(store, RejectingProcessHost.Instance, bindings);
        var now = fixture.Start.AcceptedAtUtc;
        var runtime = RecoverWorker();
        var initialized = await runtime.InitializeAsync(Context(now), fixture.Plan, fixture.Start);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        for (var i = 0; i < 4 && checkpoint.Continuation.Children.Length < 2; i++)
        {
            now = now.AddSeconds(1);
            var started = await RecoverWorker().ActivateAsync(Context(now), fixture.Plan, checkpoint.ContinuationIdentity,
                new(new($"activation/scope/start/{i}"), ProcessActivationCause.Start, now, fixture.ActivationContext));
            checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(started.Snapshot).Checkpoint;
        }
        Assert.Equal(2, checkpoint.Continuation.Children.Length);
        Assert.Equal(2, checkpoint.DurableOperations.Length);
        now = now.AddSeconds(1);
        var command = ProcessControlTestFixture.Create().Cancel(checkpoint.Control,
            id: "cancel/two-child-scope", issuedAtUtc: now);
        var cancelled = await RecoverWorker().CancelAsync(Context(now), fixture.Plan, command, fixture.ActivationContext);
        checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(cancelled.Snapshot).Checkpoint;
        var intents = ProcessChildCancellationIntents.Project(checkpoint.Continuation);
        Assert.Equal(2, intents.Length);
        Assert.Equal(ProcessControlMode.Cancelling, checkpoint.Control.Mode);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, checkpoint.Continuation.Terminal.Kind);

        for (var i = 0; i < intents.Length; i++)
        {
            now = now.AddSeconds(1);
            var closure = new ProcessChildCancellationClosure(intents[i].IntentId, intents[i].ChildContinuation,
                i == 0 ? ExecutionTerminalOutcomeKind.Completed : ExecutionTerminalOutcomeKind.Cancelled, now);
            var activation = new ProcessActivation(new($"activation/scope/close/{i}"), ProcessActivationCause.Control,
                now, fixture.ActivationContext, inputs: [], cancellation: null,
                admissionOperatingPoints: [], childCancellationClosures: [closure]);
            var closed = await RecoverWorker().ActivateAsync(Context(now), fixture.Plan, checkpoint.ContinuationIdentity, activation);
            Assert.Equal(ProcessDurableRuntimeDisposition.Applied, closed.Disposition);
            checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(closed.Snapshot).Checkpoint;
            Assert.Equal(ProcessControlMode.Cancelling, checkpoint.Control.Mode);
            Assert.Equal(ExecutionTerminalOutcomeKind.None, checkpoint.Continuation.Terminal.Kind);
            Assert.Equal(i == 0 ? 2 : 3, checkpoint.DurableOperations.Length);
            Assert.Equal(i == 0 ? ProcessCancellationFinalizationPhase.WaitingForPropagatedChildren
                : ProcessCancellationFinalizationPhase.FinalizerActive, checkpoint.Continuation.CancellationFinalization?.Phase);
            var replay = await RecoverWorker().ActivateAsync(Context(now), fixture.Plan, checkpoint.ContinuationIdentity, activation);
            Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
            Assert.Equal(closed.Snapshot.Revision, replay.Snapshot?.Revision);
        }
        Assert.All(checkpoint.Continuation.Children.Where(item => item.Node.Value.StartsWith("child/", StringComparison.Ordinal)),
            item => Assert.Equal(ProcessChildDisposition.CancellationSettled, item.Disposition));
        Assert.Equal(2, checkpoint.DurableOperations.Count(item => item.Request.ChildTarget?.Definition == child.Process.Reference));
    }
}
