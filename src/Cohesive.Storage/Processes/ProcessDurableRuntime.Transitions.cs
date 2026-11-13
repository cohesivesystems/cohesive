using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

public sealed partial class ProcessDurableRuntime
{
    // Host policy is rechecked against the acquired snapshot, not a stale discovery read.
    internal Func<OperationContext, ProcessDurableStoreSnapshot, bool>? AdmitTransitionAttempt { get; init; }

    sealed class NativeTransitionJournal(ProcessDurableStoreSnapshot snapshot)
    {
        internal ProcessDurableStoreSnapshot Snapshot = snapshot;
    }

    async ValueTask<ProcessOperationResult?> ExecuteRecoverableTransitionAsync(OperationContext context,
        ProcessTransitionInvocation invocation, IRecoverableProcessTransitionOperationAdapter adapter, NativeTransitionJournal journal)
    {
        var history = ProcessTransitionAttempt.Read(journal.Snapshot);
        var operation = ProcessTransitionAttempt.Operation(invocation);
        var related = history.Where(item => ProcessTransitionAttempt.Operation(item.Invocation) == operation).ToArray();
        if (related.Any(item => item.Invocation != invocation))
            throw new InvalidOperationException("Native transition replay changed its retained invocation.");
        if (related.LastOrDefault()?.Result is { } retained) return retained;
        if (AdmitTransitionAttempt is { } admit && !admit(context, journal.Snapshot))
            return null;
        var ordinal = related.Length + 1;
        var lease = journal.Snapshot.WorkerLease!;
        var now = context.UtcNow;
        var key = ProcessStorageContentFingerprints.Value(invocation).Value + "/" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var claim = new DurableOperationClaim(new(key), options.WorkerId, new(lease.Fence.Ordinal), now, lease.ExpiresAtUtc);
        var dispatched = new ProcessTransitionAttempt(invocation,
            new(ordinal, claim, DurableOperationAttemptStage.Dispatched, dispatchedAtUtc: now));
        // The dispatch boundary must be confirmed before crossing the target boundary. An interrupted
        // call is therefore discoverable even if cancellation prevents recording a failure afterwards.
        await Retain(dispatched, expectedVersion: 0).ConfigureAwait(false);
        ProcessOperationResult result;
        try
        {
            result = await adapter.ExecuteAsync(context, invocation).ConfigureAwait(false);
            if (!result.IsValidOutcome()) throw new InvalidOperationException("The target returned an invalid operation result.");
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (!context.CancellationToken.IsCancellationRequested)
                await Retain(new(invocation, new(ordinal, claim, DurableOperationAttemptStage.Failed,
                    now, context.UtcNow, adapter.DescribeFailure(exception))), expectedVersion: 1).ConfigureAwait(false);
            throw;
        }
        await Retain(new(invocation, new(ordinal, claim, DurableOperationAttemptStage.Acknowledged,
            now, context.UtcNow), result), expectedVersion: 1).ConfigureAwait(false);
        return result;

        async Task Retain(ProcessTransitionAttempt attempt, long expectedVersion)
        {
            var snapshot = journal.Snapshot;
            var observed = context.UtcNow;
            var mutation = attempt.Mutation(expectedVersion);
            var commit = new ProcessDurableCommit(new(mutation.Identity), snapshot.Revision, options.WorkerId,
                snapshot.WorkerLease!.Fence, snapshot.Checkpoint.WithInbox(snapshot.Checkpoint.Inbox, observed), [mutation], observed);
            var retained = await CommitExactAsync(context, commit).ConfigureAwait(false);
            if (retained?.Disposition is ProcessStoreMutationDisposition.StaleFence or ProcessStoreMutationDisposition.LeaseExpired)
                throw new ProcessWorkerFenceException(retained.Disposition);
            if (retained?.Disposition is not (ProcessStoreMutationDisposition.Applied or ProcessStoreMutationDisposition.Replayed)
                || retained.Snapshot is null)
                throw new InvalidOperationException("Native transition attempt persistence is unconfirmed; recover the same operation.");
            journal.Snapshot = retained.Snapshot;
        }
    }
}
