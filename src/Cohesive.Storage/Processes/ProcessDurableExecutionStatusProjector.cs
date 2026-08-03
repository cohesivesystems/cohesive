using Cohesive.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Projects one durable Process checkpoint into the common execution-status contract.</summary>
public static class ProcessDurableExecutionStatusProjector
{
    /// <summary>Projects canonical durable Process state without exposing inputs, bindings, or operation payloads.</summary>
    /// <param name="checkpoint">Complete durable Process checkpoint to project.</param>
    /// <returns>
    /// Common execution status whose token, wait, progress, demand, and health facets come from the checkpoint's
    /// existing canonical authorities.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The checkpoint cannot be represented by the common status contract.</exception>
    public static ExecutionStatus Project(ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return ExecutionStatusProjector.Project(
            state: checkpoint.Control,
            runtime: ProjectRuntime(checkpoint),
            terminalOutcome: checkpoint.Continuation.Terminal);
    }

    static ExecutionRuntimeStatusDetails ProjectRuntime(ProcessDurableCheckpoint checkpoint)
    {
        var state = checkpoint.Continuation;
        var health = GetHealth(checkpoint);
        HashSet<TokenId> activeWaitTokens =
        [
            .. state.Waits.Where(static wait => wait.Active).Select(static wait => wait.Token)
        ];
        return new(
            tokensDisclosure: ExecutionStatusDisclosure.Disclosed,
            tokens:
            [
                .. state.Tokens
                    .Where(token => token.Disposition != ExecutionTokenDisposition.Waiting
                        || activeWaitTokens.Contains(token.Id))
                    .Select(static token => new ExecutionTokenStatus(
                    tokenId: token.Id,
                    node: token.Node,
                    disposition: token.Disposition))
            ],
            waitsDisclosure: ExecutionStatusDisclosure.Disclosed,
            waits:
            [
                .. state.Waits.Where(static wait => wait.Active).Select(static wait => new ExecutionWaitStatus(
                    tokenId: wait.Token,
                    node: wait.Node,
                    waitingSinceUtc: wait.RegisteredAtUtc,
                    deadlineUtc: wait.Timers.IsEmpty
                        ? null
                        : wait.Timers.Min(static timer => timer.DueAtUtc)))
            ],
            progressDisclosure: ExecutionStatusDisclosure.Disclosed,
            progress: new(
                completed: state.CompletedActivationCount,
                total: null,
                unit: "activation"),
            demandDisclosure: ExecutionStatusDisclosure.Disclosed,
            demand: new(
                ready: state.Tokens.Count(static token => token.Disposition == ExecutionTokenDisposition.Ready),
                delayed: 0),
            health: health);
    }

    static ExecutionHealthStatus GetHealth(ProcessDurableCheckpoint checkpoint)
    {
        if (checkpoint.Continuation.Terminal.Kind is ExecutionTerminalOutcomeKind.Failed
                or ExecutionTerminalOutcomeKind.Terminated
            || checkpoint.Continuation.Tokens.Any(static token =>
                token.Disposition == ExecutionTokenDisposition.Failed)
            || checkpoint.DurableOperations.Any(static operation => operation.Status is
                DurableOperationStatus.TerminalOutcomeRequired or DurableOperationStatus.EscalationRequired))
        {
            return ExecutionHealthStatus.Unhealthy;
        }

        return checkpoint.DurableOperations.Any(static operation => operation.Status is
            DurableOperationStatus.RetryEligible or DurableOperationStatus.ReconciliationRequired)
                ? ExecutionHealthStatus.Degraded
                : ExecutionHealthStatus.Healthy;
    }
}
