using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Execution;

namespace Cohesive.Adapters.DurableTask;

// Runs inside orchestration replay. Activities and timers, rather than worker memory, own progress.
internal static class DurableTaskProcessReadExecution
{
    internal const string DeadlineExceeded = "processes.read.deadlineExceeded";

    internal static async Task<ProcessOperationResult> ExecuteAsync(
        DurableTaskProcessHostOperation operation,
        Func<DurableTaskProcessHostOperation, Task<ProcessOperationResult>> execute,
        Func<TimeSpan, CancellationToken, Task> timer,
        Func<DateTimeOffset> utcNow,
        Func<Task, Task>? awaitHostWork = null)
    {
        var policy = operation.RelationQuery!.Policy!;
        // Orchestration time is replay-stable. Every scheduled attempt carries the SAME absolute deadline.
        DateTimeOffset? deadline = policy.Timeout is { } timeout ? utcNow() + timeout : null;
        var attemptOperation = new DurableTaskProcessHostOperation(operation.Kind,
            relationQuery: operation.RelationQuery, readDeadlineUtc: deadline);
        for (var attempt = 1; ; attempt++)
        {
            if (deadline is { } due && utcNow() >= due) return TimedOut();
            var execution = execute(attemptOperation);
            if (awaitHostWork is not null) await awaitHostWork(execution).ConfigureAwait(true);
            var result = await execution.ConfigureAwait(true);
            if (result is null || !result.IsValidOutcome())
                throw new InvalidOperationException("Read activity returned invalid operation evidence.");
            if (deadline is { } finishedBy && utcNow() >= finishedBy) return TimedOut();
            if (result.IsSuccessful || attempt >= policy.MaxAttempts || result.Failure is null
                || result.Failure.Code == DeadlineExceeded
                || !policy.RetryableFailureCodes.Contains(result.Failure.Code, StringComparer.Ordinal)) return result;
            if (!result.Emissions.IsEmpty)
                throw new InvalidOperationException("Cannot retry a read that emitted interactions.");
            if (policy.RetryDelay > TimeSpan.Zero)
            {
                var delay = policy.RetryDelay;
                if (deadline is { } end && end - utcNow() < delay) delay = end - utcNow();
                if (delay <= TimeSpan.Zero) return TimedOut();
                using var cancellation = new CancellationTokenSource();
                var backoff = timer(delay, cancellation.Token);
                try
                {
                    if (awaitHostWork is not null) await awaitHostWork(backoff).ConfigureAwait(true);
                    await backoff.ConfigureAwait(true);
                }
                finally
                {
                    if (!backoff.IsCompleted) cancellation.Cancel();
                }
            }
        }
    }

    // A single physical attempt. Semantic retries belong to orchestration history, never this worker.
    internal static async ValueTask<ProcessOperationResult> EvaluateAttemptAsync(
        OperationContext context, DurableTaskProcessHostOperation operation, IAsyncProcessReferenceHost host)
    {
        context.ThrowIfCancellationRequested();
        var evaluation = operation.RelationQuery!;
        if (evaluation.Policy?.Timeout is not null && operation.ReadDeadlineUtc is null)
            throw new ArgumentException("A timed read activity requires its original orchestration deadline.", nameof(operation));
        if (operation.ReadDeadlineUtc is not { } deadline)
            return await host.EvaluateRelationAsync(context, evaluation).ConfigureAwait(false);
        var remaining = deadline - context.TimeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero) return TimedOut();
        using var timeout = new CancellationTokenSource(remaining, context.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeout.Token);
        try
        {
            var result = await host.EvaluateRelationAsync(context.WithCancellationToken(linked.Token), evaluation).ConfigureAwait(false);
            context.ThrowIfCancellationRequested();
            return timeout.IsCancellationRequested || context.TimeProvider.GetUtcNow() >= deadline ? TimedOut() : result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            // The awaited host has quiesced. Worker shutdown remains a scheduler redelivery, not a semantic retry.
            return TimedOut();
        }
    }

    static ProcessOperationResult TimedOut() => ProcessOperationResult.Failed(
        new(DeadlineExceeded, DiagnosticSeverity.Error, "The process read exceeded its total deadline."));
}
