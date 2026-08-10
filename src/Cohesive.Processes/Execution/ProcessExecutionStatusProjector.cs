using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Processes.Execution;

/// <summary>Projects canonical Process continuation and control state into common execution status.</summary>
public static class ProcessExecutionStatusProjector
{
    /// <summary>
    /// Projects safe Process lifecycle, token, wait, progress, demand, and health status without retained payloads.
    /// </summary>
    /// <param name="continuation">Complete canonical Process continuation to summarize.</param>
    /// <param name="control">Complete canonical lifecycle-control state for the same continuation.</param>
    /// <param name="durableOperations">
    /// Canonical durable Request ledgers used only to classify runtime health; request and result values are not
    /// copied into the projection.
    /// </param>
    /// <param name="extensions">Typed runtime-owned status extensions to attach.</param>
    /// <param name="terminalDetailDisclosure">
    /// Maximum terminal-detail disclosure. Redaction is the safe default; disclosed preserves the continuation's
    /// existing explicit disclosure. Unknown is invalid because the continuation is an authoritative observation.
    /// </param>
    /// <returns>
    /// Protocol-neutral status derived from the supplied semantic authorities, with only structurally safe runtime
    /// facets disclosed.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="continuation"/> or <paramref name="control"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The continuation and control state do not have exact definition, instance, and current-attempt affinity, or
    /// the supplied state cannot be represented by the common status contract.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="terminalDetailDisclosure"/> is unsupported.
    /// </exception>
    public static ExecutionStatus Project(
        ProcessContinuationState continuation,
        ProcessControlState control,
        ImmutableArray<DurableOperationState> durableOperations = default,
        ImmutableArray<ExecutionRuntimeStatusExtension> extensions = default,
        ExecutionStatusDisclosure terminalDetailDisclosure = ExecutionStatusDisclosure.Redacted)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(control);
        RequireAffinity(continuation, control);
        if (terminalDetailDisclosure is not (
            ExecutionStatusDisclosure.Disclosed or ExecutionStatusDisclosure.Redacted))
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalDetailDisclosure),
                terminalDetailDisclosure,
                "Unsupported terminal-detail disclosure.");
        }

        var operations = durableOperations.IsDefault ? [] : durableOperations;
        if (operations.Any(static operation => operation is null))
        {
            throw new ArgumentException(
                "Durable operation status cannot contain null entries.",
                nameof(durableOperations));
        }

        return ExecutionStatusProjector.Project(
            state: control,
            runtime: ProjectRuntime(continuation, operations, extensions),
            terminalOutcome: ProjectTerminal(continuation.Terminal, terminalDetailDisclosure));
    }

    static ExecutionTerminalOutcome ProjectTerminal(
        ExecutionTerminalOutcome terminal,
        ExecutionStatusDisclosure disclosure)
    {
        if (terminal.Detail is null || terminal.Kind == ExecutionTerminalOutcomeKind.None)
        {
            return terminal;
        }

        var detail = disclosure switch
        {
            ExecutionStatusDisclosure.Disclosed => terminal.Detail,
            ExecutionStatusDisclosure.Redacted => ExecutionStatusValue.Redacted(terminal.Detail.Contract),
            _ => throw new ArgumentOutOfRangeException(nameof(disclosure), disclosure, null)
        };
        return new(terminal.Kind, terminal.OccurredAtUtc, detail);
    }

    static void RequireAffinity(ProcessContinuationState continuation, ProcessControlState control)
    {
        if (continuation.Definition != control.Definition)
        {
            throw new ArgumentException("Process continuation and control state must pin the same exact definition.");
        }
        if (continuation.Continuation.ProcessInstanceId != control.ProcessInstanceId)
        {
            throw new ArgumentException("Process continuation and control state must name the same Process instance.");
        }
        if (continuation.Continuation.ProcessAttemptId != control.CurrentAttempt.AttemptId)
        {
            throw new ArgumentException("Process continuation must belong to the current control attempt.");
        }
    }

    static ExecutionRuntimeStatusDetails ProjectRuntime(
        ProcessContinuationState continuation,
        ImmutableArray<DurableOperationState> durableOperations,
        ImmutableArray<ExecutionRuntimeStatusExtension> extensions)
    {
        HashSet<TokenId> activeWaitTokens =
        [
            .. continuation.Waits.Where(static wait => wait.Active).Select(static wait => wait.Token)
        ];
        return new(
            tokensDisclosure: ExecutionStatusDisclosure.Disclosed,
            tokens:
            [
                .. continuation.Tokens
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
                .. continuation.Waits.Where(static wait => wait.Active).Select(static wait => new ExecutionWaitStatus(
                    tokenId: wait.Token,
                    node: wait.Node,
                    waitingSinceUtc: wait.RegisteredAtUtc,
                    deadlineUtc: wait.Timers.IsEmpty
                        ? null
                        : wait.Timers.Min(static timer => timer.DueAtUtc)))
            ],
            progressDisclosure: ExecutionStatusDisclosure.Disclosed,
            progress: new(
                completed: continuation.CompletedActivationCount,
                total: null,
                unit: "activation"),
            demandDisclosure: ExecutionStatusDisclosure.Disclosed,
            demand: new(
                ready: continuation.Tokens.Count(static token => token.Disposition == ExecutionTokenDisposition.Ready),
                delayed: continuation.Tokens.Count(static token => token.Disposition == ExecutionTokenDisposition.Pending)),
            health: GetHealth(continuation, durableOperations),
            extensions: extensions);
    }

    static ExecutionHealthStatus GetHealth(
        ProcessContinuationState continuation,
        ImmutableArray<DurableOperationState> durableOperations)
    {
        if (continuation.Terminal.Kind is ExecutionTerminalOutcomeKind.Failed
                or ExecutionTerminalOutcomeKind.Terminated
            || continuation.Tokens.Any(static token => token.Disposition == ExecutionTokenDisposition.Failed)
            || durableOperations.Any(static operation => operation.Status is
                DurableOperationStatus.TerminalOutcomeRequired or DurableOperationStatus.EscalationRequired))
        {
            return ExecutionHealthStatus.Unhealthy;
        }

        return durableOperations.Any(static operation => operation.Status is
            DurableOperationStatus.RetryEligible or DurableOperationStatus.ReconciliationRequired)
                ? ExecutionHealthStatus.Degraded
                : ExecutionHealthStatus.Healthy;
    }
}
