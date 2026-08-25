using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Execution;

/// <summary>Outcome of one finite reference-interpreter activation.</summary>
public enum ProcessActivationDisposition
{
    /// <summary>No disposition was supplied; invalid in a decision.</summary>
    Unspecified = 0,

    /// <summary>No token can make progress until new durable evidence arrives.</summary>
    Quiescent = 1,

    /// <summary>The activation stopped at the first deterministic durable boundary.</summary>
    DurableCut = 2,

    /// <summary>The Process completed successfully.</summary>
    Completed = 3,

    /// <summary>The Process ended through semantic or interpreter failure.</summary>
    Failed = 4,

    /// <summary>The Process ended through cooperative safe-point cancellation.</summary>
    Cancelled = 5,

    /// <summary>The activation was rejected without changing semantic continuation state.</summary>
    Rejected = 6
}

/// <summary>Kind of one deterministic Process execution trace event.</summary>
public enum ProcessTraceEventKind
{
    /// <summary>A presented input was buffered or dispositioned.</summary>
    InputAdmitted = 0,

    /// <summary>A token began interpreting one canonical node occurrence.</summary>
    NodeEntered = 1,

    /// <summary>An operation completed with explicit host evidence.</summary>
    OperationCompleted = 2,

    /// <summary>An ordered branch case was selected.</summary>
    BranchSelected = 3,

    /// <summary>A Fork created its stable branch tokens.</summary>
    ForkCreated = 4,

    /// <summary>A branch arrived at its reciprocal Join.</summary>
    JoinArrived = 5,

    /// <summary>A reciprocal Join selected its deterministic branch set.</summary>
    JoinResolved = 6,

    /// <summary>A complete durable wait was registered.</summary>
    WaitRegistered = 7,

    /// <summary>An interaction or timer won AwaitMatch arbitration.</summary>
    WaitResolved = 8,

    /// <summary>A canonical interaction intent was emitted.</summary>
    InteractionEmitted = 9,

    /// <summary>A token advanced across a canonical edge.</summary>
    TokenAdvanced = 10,

    /// <summary>A token or Process reached a terminal disposition.</summary>
    TerminalReached = 11,

    /// <summary>Cancellation was applied at an activation safe point.</summary>
    CancellationApplied = 12,

    /// <summary>A replay-stable child Process occurrence was retained.</summary>
    ChildRegistered = 13,

    /// <summary>A child Process terminal outcome was admitted.</summary>
    ChildResolved = 14,

    /// <summary>Owner or partition closure retained a child cancellation-propagation intent.</summary>
    ChildCancellationRequested = 15,

    /// <summary>Owner or partition closure deliberately detached child work.</summary>
    ChildDetached = 16,

    /// <summary>A bounded partition-work occurrence was retained or resolved.</summary>
    PartitionBatchChanged = 17,

    /// <summary>A durable recurrence decision retained explicit progress evidence.</summary>
    RecurrenceAdvanced = 18,

    /// <summary>Owner or partition closure closed bounded child work before its Request was emitted.</summary>
    ChildCancelledBeforeStart = 19,

    /// <summary>A Fork admission point changed, admitted a branch, or retained a finite activation boundary.</summary>
    ForkAdmissionChanged = 20,

    /// <summary>A propagated child cancellation reached an observed terminal closure.</summary>
    ChildCancellationSettled = 21,

    /// <summary>The exact authored cancellation-finalizer child occurrence started.</summary>
    CancellationFinalizerStarted = 22,

    /// <summary>The authored cancellation-finalizer produced acknowledgement or explicit failure.</summary>
    CancellationFinalizerResolved = 23
}

/// <summary>One ordered attributable observation from Process reference interpretation.</summary>
/// <param name="Sequence">Zero-based sequence within the finite activation.</param>
/// <param name="Kind">Stable trace-event kind.</param>
/// <param name="Definition">Exact Process definition identity, revision, and fingerprint.</param>
/// <param name="Continuation">Logical Process instance and attempt.</param>
/// <param name="Activation">Finite activation identity.</param>
/// <param name="Token">Durable token associated with the event.</param>
/// <param name="Node">Canonical Process node associated with the event.</param>
/// <param name="BranchOrClause">Optional stable branch, clause, or case identity.</param>
/// <param name="Emission">Optional logical interaction identity.</param>
/// <param name="Detail">Optional stable non-sensitive detail.</param>
/// <param name="SourceReferences">Producer-source references resolved from the canonical source map.</param>
/// <param name="EmissionFingerprint">
/// Exact complete interaction-envelope fingerprint for an <see cref="ProcessTraceEventKind.InteractionEmitted"/>
/// event; null for every other event kind.
/// </param>
/// <param name="OperationOccurrence">
/// Zero-based token-history occurrence for an <see cref="ProcessTraceEventKind.OperationCompleted"/> event; null
/// for every other event kind.
/// </param>
/// <param name="InputDisposition">
/// Exact semantic disposition for a <see cref="ProcessTraceEventKind.InputAdmitted"/> event; null for every other
/// event kind.
/// </param>
/// <param name="InputReason">
/// Exact semantic classification for a <see cref="ProcessTraceEventKind.InputAdmitted"/> event; null for every
/// other event kind.
/// </param>
/// <param name="WaitRegistrationId">
/// Exact wait occurrence named by an input disposition, when one participated in the decision.
/// </param>
/// <param name="ProcessOccurrence">Typed payload-safe child, partition, or recurrence occurrence evidence.</param>
/// <param name="RequestOutcome">Exact terminal Request outcome identity when a Reply participated.</param>
public sealed record ProcessTraceEvent(
    int Sequence,
    ProcessTraceEventKind Kind,
    ExecutionDefinitionReference Definition,
    ProcessContinuationIdentity Continuation,
    ActivationId Activation,
    TokenId Token,
    ExecutionNodeId Node,
    ExecutionNodeId? BranchOrClause,
    EmissionId? Emission,
    string? Detail,
    ImmutableArray<string> SourceReferences,
    InteractionEnvelopeContentFingerprint? EmissionFingerprint = null,
    long? OperationOccurrence = null,
    ProcessInputAdmissionDisposition? InputDisposition = null,
    ProcessInputAdmissionReason? InputReason = null,
    ProcessWaitRegistrationId? WaitRegistrationId = null,
    ProcessTraceOccurrenceEvidence? ProcessOccurrence = null,
    RequestTerminalOutcomeId? RequestOutcome = null);

/// <summary>Attributable deterministic evidence returned by one finite Process activation.</summary>
/// <param name="Definition">Exact Process definition identity, revision, and fingerprint.</param>
/// <param name="Activation">Finite activation identity.</param>
/// <param name="Cause">Closed activation cause.</param>
/// <param name="SafePointNode">First deterministic durable-boundary node, when one stopped the activation.</param>
/// <param name="Trace">Complete ordered execution trace.</param>
public sealed record ProcessExecutionEvidence(
    ExecutionDefinitionReference Definition,
    ActivationId Activation,
    ProcessActivationCause Cause,
    ExecutionNodeId? SafePointNode,
    ImmutableArray<ProcessTraceEvent> Trace);

/// <summary>Pure replacement-state decision produced by one finite Process activation.</summary>
public sealed record ProcessActivationDecision
{
    internal ProcessActivationDecision(
        ProcessActivationDisposition disposition,
        ProcessContinuationState state,
        ImmutableArray<InteractionEnvelope> emissions,
        ImmutableArray<ProcessInputReceipt> inputAdmissions,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        ProcessExecutionEvidence evidence)
    {
        Disposition = disposition;
        State = state;
        Emissions = emissions.IsDefault ? [] : emissions;
        InputAdmissions = inputAdmissions.IsDefault ? [] : inputAdmissions;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
        Evidence = evidence;
    }

    /// <summary>Finite activation outcome.</summary>
    public ProcessActivationDisposition Disposition { get; }

    /// <summary>Complete immutable replacement continuation state.</summary>
    public ProcessContinuationState State { get; }

    /// <summary>Canonical interaction intents to commit with the replacement continuation.</summary>
    public ImmutableArray<InteractionEnvelope> Emissions { get; }

    /// <summary>Input dispositions decided by this activation.</summary>
    public ImmutableArray<ProcessInputReceipt> InputAdmissions { get; }

    /// <summary>Structured interpreter diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Exact definition provenance, safe point, and ordered trace.</summary>
    public ProcessExecutionEvidence Evidence { get; }
}
