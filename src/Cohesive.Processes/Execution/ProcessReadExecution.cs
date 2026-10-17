using Cohesive.Execution;

namespace Cohesive.Processes.Execution;

// Shared by asynchronous process interpretation; repository adapters do not own retry loops.
internal static class ProcessReadExecution
{
    internal static async ValueTask<ProcessOperationResult> EvaluateAsync(OperationContext context,
        ProcessRelationEvaluation evaluation, IAsyncProcessReferenceHost host)
    {
        var policy = evaluation.Policy ?? throw new ArgumentException("A read policy is required.", nameof(evaluation));
        using var deadline = policy.Timeout is { } timeout ? new CancellationTokenSource(timeout, context.TimeProvider) : null;
        using var cancellation = deadline is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, deadline.Token);
        var scoped = cancellation is null ? context : context.WithCancellationToken(cancellation.Token);
        for (var attempt = 1; ; attempt++)
        {
            scoped.ThrowIfCancellationRequested();
            var result = await host.EvaluateRelationAsync(scoped, evaluation).ConfigureAwait(false);
            scoped.ThrowIfCancellationRequested();
            if (result is null || !result.IsValidOutcome())
                throw new InvalidOperationException("Read host returned invalid operation evidence.");
            if (result.IsSuccessful || attempt >= policy.MaxAttempts || result.Failure is null
                || !policy.RetryableFailureCodes.Contains(result.Failure.Code, StringComparer.Ordinal)) return result;
            if (!result.Emissions.IsEmpty)
                throw new InvalidOperationException("Cannot retry a read that emitted interactions.");
            if (policy.RetryDelay > TimeSpan.Zero)
                await Task.Delay(policy.RetryDelay, context.TimeProvider, scoped.CancellationToken).ConfigureAwait(false);
        }
    }
}
