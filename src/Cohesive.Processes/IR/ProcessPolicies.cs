using System.Text.Json.Serialization;

namespace Cohesive.Processes.IR;

/// <summary>How a Join determines that enough reciprocal Fork branches completed.</summary>
public enum ProcessJoinMode
{
    /// <summary>No join mode was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>
    /// Every branch owned by the reciprocal Fork must complete; bindings guaranteed by each complete branch become
    /// definitely visible after the Join.
    /// </summary>
    All = 1,

    /// <summary>
    /// The first eligible completed branch permits the Join to continue. Branch-local bindings do not become
    /// definitely visible after the Join without a later explicit aggregation construct.
    /// </summary>
    Any = 2,

    /// <summary>
    /// An explicitly required number of eligible branches must complete. Branch-local bindings do not become
    /// definitely visible after the Join without a later explicit aggregation construct.
    /// </summary>
    RequiredCount = 3
}

/// <summary>How branch failure affects a Join.</summary>
public enum ProcessJoinFailurePolicy
{
    /// <summary>No branch-failure behavior was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>The first failed branch fails the Join without waiting for further branch completion.</summary>
    FailFast = 1,

    /// <summary>Failed branches remain observable while the Join waits for its required eligible completions.</summary>
    WaitForRequired = 2,

    /// <summary>Failed branches do not count toward the required completion threshold.</summary>
    ExcludeFailed = 3
}

/// <summary>How a satisfied or failed Join treats reciprocal branches that remain active.</summary>
public enum ProcessJoinCancellationPolicy
{
    /// <summary>No remaining-branch behavior was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>Request cancellation of branches that are no longer needed by the Join.</summary>
    CancelRemaining = 1,

    /// <summary>Allow remaining branches to complete before the Join token advances.</summary>
    AwaitRemaining = 2,

    /// <summary>Allow remaining branches to continue independently after the Join token advances.</summary>
    ContinueRemaining = 3
}

/// <summary>Whether branch completion order contributes to observable Process meaning.</summary>
public enum ProcessJoinCompletionOrder
{
    /// <summary>No completion-order behavior was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>Only the eligible branch set is semantic; physical completion order is not observable.</summary>
    Unobservable = 1,

    /// <summary>Logical completion order is retained as observable Process evidence.</summary>
    Observable = 2
}

/// <summary>Deterministic arbitration when multiple Join branches become eligible together.</summary>
public enum ProcessJoinTieBreak
{
    /// <summary>No tie-break was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>Select branches by their stable branch identities using ordinal ordering.</summary>
    BranchIdentity = 1,

    /// <summary>Select by logical completion sequence, then by stable branch identity for an exact tie.</summary>
    CompletionThenBranchIdentity = 2
}

/// <summary>Complete explicit branch-completion policy of one Process Join.</summary>
public sealed record ProcessJoinPolicy
{
    /// <summary>Creates an explicit Process Join policy.</summary>
    /// <param name="mode">Completion-threshold mode.</param>
    /// <param name="requiredCount">Required eligible branch count interpreted under <paramref name="mode"/>.</param>
    /// <param name="failure">Branch-failure behavior.</param>
    /// <param name="cancellation">Behavior for reciprocal branches that remain active.</param>
    /// <param name="completionOrder">Whether logical completion order is observable.</param>
    /// <param name="tieBreak">Deterministic simultaneous-eligibility arbitration.</param>
    [JsonConstructor]
    public ProcessJoinPolicy(
        ProcessJoinMode mode,
        int requiredCount,
        ProcessJoinFailurePolicy failure,
        ProcessJoinCancellationPolicy cancellation,
        ProcessJoinCompletionOrder completionOrder,
        ProcessJoinTieBreak tieBreak)
    {
        Mode = mode;
        RequiredCount = requiredCount;
        Failure = failure;
        Cancellation = cancellation;
        CompletionOrder = completionOrder;
        TieBreak = tieBreak;
    }

    /// <summary>Completion-threshold mode.</summary>
    public ProcessJoinMode Mode { get; }

    /// <summary>Required eligible branch count interpreted under <see cref="Mode"/>.</summary>
    public int RequiredCount { get; }

    /// <summary>Branch-failure behavior.</summary>
    public ProcessJoinFailurePolicy Failure { get; }

    /// <summary>Behavior for reciprocal branches that remain active.</summary>
    public ProcessJoinCancellationPolicy Cancellation { get; }

    /// <summary>Whether logical completion order is observable.</summary>
    public ProcessJoinCompletionOrder CompletionOrder { get; }

    /// <summary>Deterministic simultaneous-eligibility arbitration.</summary>
    public ProcessJoinTieBreak TieBreak { get; }
}

/// <summary>Winner selection used by a durable AwaitMatch node.</summary>
public enum ProcessAwaitArbitration
{
    /// <summary>No arbitration was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>Select exactly one eligible clause by descending priority and then ordinal clause identity.</summary>
    ExclusivePriorityThenClauseId = 1
}

/// <summary>Disposition applied to a late, stale, or duplicate input targeting an AwaitMatch.</summary>
public enum ProcessAwaitInputDisposition
{
    /// <summary>No input disposition was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>Reject the input without changing the accepted AwaitMatch result.</summary>
    Reject = 1,

    /// <summary>Retain the input as observable evidence without reopening the AwaitMatch.</summary>
    Observe = 2,

    /// <summary>Return or reuse the input's previously accepted logical disposition.</summary>
    ReusePriorDisposition = 3
}

/// <summary>Disposition applied when an input's durable AwaitMatch target cannot be resolved.</summary>
public enum ProcessAwaitMissingTargetDisposition
{
    /// <summary>No missing-target disposition was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>Reject the input because no compatible durable target exists.</summary>
    Reject = 1,

    /// <summary>Retain the unresolved input as observable evidence.</summary>
    Observe = 2,

    /// <summary>Route the unresolved input to a durable dead-letter interpretation.</summary>
    DeadLetter = 3
}
