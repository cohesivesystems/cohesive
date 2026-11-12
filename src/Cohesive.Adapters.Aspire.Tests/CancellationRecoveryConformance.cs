using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Adapters.Aspire.Tests;

// Adapter fixtures supply physical interruption, lease expiry and restored drivers. Scenario ordering and
// canonical recovery assertions are shared; lock scheduling and database-specific races remain adapter tests.
// Seed contract: submitted order, stock 3, and two reservations for the same SKU with quantities 2 and 1.
internal interface ICancellationRecoveryFixture : IAsyncDisposable
{
    ProcessInterpreterRealizationReport Recovery { get; }
    ExecutionDefinitionReference Release { get; }
    ExecutionDefinitionReference MarkReleased { get; }
    OperationContext Context { get; }
    ProcessContinuationIdentity Continuation { get; }
    Task<ProcessDurableActivationResult> Run(string owner,
        Func<OperationContext, ProcessTransitionInvocation, IAsyncProcessReferenceHost, ValueTask<ProcessOperationResult>>? hook = null);
    Task<ProcessDurableStoreSnapshot> Snapshot();
    Task WaitForExpiry();
    Task<ProcessStoreMutationResult> AcquireReplacement();
    Task<int> Stock();
    Task AssertCompleted(ProcessDurableActivationResult result);
}

internal static class CancellationRecoveryConformance
{
    internal static async Task RejectStaleWorker(ICancellationRecoveryFixture fixture, bool replaceBeforeResume)
    {
        Assert.True(fixture.Recovery.IsRealizable);
        Assert.All(ProcessRecoveryRequirements.WorkerTakeover, key =>
            Assert.Contains(fixture.Recovery.Decisions, decision => decision.Requirement == key));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = fixture.Run("old", async (context, invocation, inner) =>
        {
            if (invocation.Definition == fixture.Release)
            {
                var frozen = new FrozenTimeProvider(context.UtcNow);
                entered.TrySetResult();
                await resume.Task.WaitAsync(context.CancellationToken);
                context = context with { TimeProvider = frozen };
            }
            return await inner.InvokeTransitionAsync(context, invocation);
        });
        await entered.Task.WaitAsync(fixture.Context.CancellationToken);
        var checkpoint = (await fixture.Snapshot());
        await fixture.WaitForExpiry();
        if (replaceBeforeResume)
        {
            var acquired = await fixture.AcquireReplacement();
            Assert.True(acquired.Snapshot!.WorkerLease!.Fence.Ordinal > checkpoint.WorkerLease!.Fence.Ordinal);
        }
        resume.SetResult();
        var rejected = await running;
        Assert.Equal(replaceBeforeResume ? ProcessDurableRuntimeDisposition.StaleFence : ProcessDurableRuntimeDisposition.LeaseExpired, rejected.Disposition);
        Assert.Equal(3, await fixture.Stock());
        var retained = (await fixture.Snapshot());
        Assert.Equal(checkpoint.Checkpoint.Continuation.CompletedActivationCount, retained.Checkpoint.Continuation.CompletedActivationCount);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, retained.Checkpoint.Continuation.Terminal.Kind);
        await fixture.AssertCompleted(await fixture.Run("replacement"));
    }

    internal static async Task RecoverCommittedTransition(ICancellationRecoveryFixture fixture, bool crashAfterMark)
    {
        Assert.True(fixture.Recovery.IsRealizable);
        Assert.All(ProcessRecoveryRequirements.WorkerTakeover, key =>
            Assert.Contains(fixture.Recovery.Decisions, decision => decision.Requirement == key));
        var selected = crashAfterMark ? fixture.MarkReleased : fixture.Release;
        var crashed = false;
        var running = fixture.Run("old", async (context, invocation, inner) =>
        {
            var result = await inner.InvokeTransitionAsync(context, invocation);
            if (!crashed && invocation.Definition == selected) { crashed = true; throw new InjectedCrash(); }
            return result;
        });
        await Assert.ThrowsAsync<InjectedCrash>(() => running);
        Assert.InRange(await fixture.Stock(), 4, 5);
        var oldSnapshot = (await fixture.Snapshot());
        await fixture.WaitForExpiry();
        await fixture.AssertCompleted(await fixture.Run("replacement"));
        var restored = (await fixture.Snapshot());
        Assert.Equal(oldSnapshot.Checkpoint.ContinuationIdentity, restored.Checkpoint.ContinuationIdentity);
        Assert.True(restored.WorkerLease!.Fence.Ordinal > oldSnapshot.WorkerLease!.Fence.Ordinal);
    }

    sealed class InjectedCrash : Exception;
    sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
}
