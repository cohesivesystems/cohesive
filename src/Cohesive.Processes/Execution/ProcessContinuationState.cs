using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Execution;

/// <summary>One typed value retained in token-local Process coordination state.</summary>
/// <param name="Binding">Stable canonical binding identity.</param>
/// <param name="Value">Typed portable binding value.</param>
public sealed record ProcessBindingValue(ValueBindingId Binding, PortableValue Value);

/// <summary>One inbound Request obligation retained for a later canonical Reply.</summary>
/// <param name="Binding">Stable obligation binding declared by Process IR.</param>
/// <param name="Request">Exact admitted Request envelope.</param>
public sealed record ProcessRequestObligation(
    RequestObligationBindingId Binding,
    RequestEnvelope Request);

/// <summary>Membership of one child token in a stable Fork occurrence.</summary>
/// <param name="RegistrationId">Opaque replay-stable Fork occurrence identity.</param>
/// <param name="Branch">Canonical Fork branch identity.</param>
public sealed record ProcessForkMembership(string RegistrationId, ExecutionNodeId Branch);

/// <summary>Complete coordination-local state of one durable Process token.</summary>
public sealed record ProcessTokenState
{
    [JsonConstructor]
    internal ProcessTokenState(
        TokenId id,
        ExecutionNodeId node,
        ExecutionTokenDisposition disposition,
        long step,
        ImmutableArray<ProcessBindingValue> bindings,
        ImmutableArray<ProcessRequestObligation> requestObligations,
        ProcessForkMembership? forkMembership,
        DocumentValidationDiagnostic? failure)
    {
        Id = id;
        Node = node;
        Disposition = disposition;
        Step = step;
        Bindings = bindings.IsDefault ? [] : bindings;
        RequestObligations = requestObligations.IsDefault ? [] : requestObligations;
        ForkMembership = forkMembership;
        Failure = failure;
    }

    /// <summary>Stable durable token identity.</summary>
    public TokenId Id { get; internal init; }

    /// <summary>Current canonical Process node.</summary>
    public ExecutionNodeId Node { get; internal init; }

    /// <summary>Current token lifecycle disposition.</summary>
    public ExecutionTokenDisposition Disposition { get; internal init; }

    /// <summary>Number of canonical node occurrences already executed by this token.</summary>
    public long Step { get; internal init; }

    /// <summary>Typed visible bindings in stable binding-identity order.</summary>
    public ImmutableArray<ProcessBindingValue> Bindings { get; internal init; }

    /// <summary>Visible inbound Request obligations in stable binding-identity order.</summary>
    public ImmutableArray<ProcessRequestObligation> RequestObligations { get; internal init; }

    /// <summary>Optional owning Fork occurrence and branch.</summary>
    public ProcessForkMembership? ForkMembership { get; internal init; }

    /// <summary>Structured token failure evidence when <see cref="Disposition"/> is failed.</summary>
    public DocumentValidationDiagnostic? Failure { get; internal init; }
}

/// <summary>Observed state of one branch owned by a reciprocal Fork and Join.</summary>
/// <param name="Branch">Canonical branch identity.</param>
/// <param name="Token">Stable child token identity.</param>
/// <param name="Disposition">Current child-token disposition.</param>
/// <param name="CompletionSequence">Logical completion sequence, or null while incomplete.</param>
public sealed record ProcessForkBranchState(
    ExecutionNodeId Branch,
    TokenId Token,
    ExecutionTokenDisposition Disposition,
    long? CompletionSequence = null);

/// <summary>Durable membership and convergence state for one Fork occurrence.</summary>
public sealed record ProcessForkState
{
    internal ProcessForkState(
        string registrationId,
        TokenId owner,
        ExecutionNodeId fork,
        ExecutionNodeId join,
        long occurrence,
        ImmutableArray<ProcessBindingValue> parentBindings,
        ImmutableArray<ProcessRequestObligation> parentRequestObligations,
        ImmutableArray<ProcessForkBranchState> branches,
        ImmutableArray<ExecutionNodeId> selectedBranches,
        bool resolved)
        : this(
            registrationId,
            owner,
            fork,
            join,
            occurrence,
            parentBindings,
            parentRequestObligations,
            branches,
            selectedBranches,
            resolved,
            ProcessAdmissionOperatingPoint.Canonical(
                fork,
                Math.Max(1, branches.IsDefault ? 0 : branches.Length),
                evidenceReference: fork.Value))
    {
    }

    /// <summary>Creates complete durable Fork membership, admission, and convergence state.</summary>
    /// <param name="registrationId">Opaque replay-stable Fork occurrence identity.</param>
    /// <param name="owner">Parked coordinator token that executed the Fork.</param>
    /// <param name="fork">Canonical Fork node identity.</param>
    /// <param name="join">Canonical reciprocal Join node identity.</param>
    /// <param name="occurrence">Zero-based occurrence of this Fork in the owner-token history.</param>
    /// <param name="parentBindings">Bindings visible before the Fork.</param>
    /// <param name="parentRequestObligations">Request obligations visible before the Fork.</param>
    /// <param name="branches">Complete branch membership and durable dispositions.</param>
    /// <param name="selectedBranches">Branches frozen when the Join threshold first became satisfied.</param>
    /// <param name="resolved">Whether the reciprocal Join advanced its coordinator token.</param>
    /// <param name="admissionOperatingPoint">Latest effective attributable admission point.</param>
    [JsonConstructor]
    internal ProcessForkState(
        string registrationId,
        TokenId owner,
        ExecutionNodeId fork,
        ExecutionNodeId join,
        long occurrence,
        ImmutableArray<ProcessBindingValue> parentBindings,
        ImmutableArray<ProcessRequestObligation> parentRequestObligations,
        ImmutableArray<ProcessForkBranchState> branches,
        ImmutableArray<ExecutionNodeId> selectedBranches,
        bool resolved,
        ProcessAdmissionOperatingPoint admissionOperatingPoint)
    {
        RegistrationId = registrationId;
        Owner = owner;
        Fork = fork;
        Join = join;
        Occurrence = occurrence;
        ParentBindings = parentBindings.IsDefault ? [] : parentBindings;
        ParentRequestObligations = parentRequestObligations.IsDefault ? [] : parentRequestObligations;
        Branches = branches.IsDefault ? [] : branches;
        SelectedBranches = selectedBranches.IsDefault ? [] : selectedBranches;
        Resolved = resolved;
        AdmissionOperatingPoint = admissionOperatingPoint;
    }

    /// <summary>Opaque replay-stable Fork occurrence identity.</summary>
    public string RegistrationId { get; internal init; }

    /// <summary>Parked coordinator token that executed the Fork.</summary>
    public TokenId Owner { get; internal init; }

    /// <summary>Canonical Fork node identity.</summary>
    public ExecutionNodeId Fork { get; internal init; }

    /// <summary>Canonical reciprocal Join node identity.</summary>
    public ExecutionNodeId Join { get; internal init; }

    /// <summary>Zero-based occurrence of this Fork in the owner-token history.</summary>
    public long Occurrence { get; internal init; }

    /// <summary>Bindings visible before the Fork.</summary>
    public ImmutableArray<ProcessBindingValue> ParentBindings { get; internal init; }

    /// <summary>Request obligations visible before the Fork.</summary>
    public ImmutableArray<ProcessRequestObligation> ParentRequestObligations { get; internal init; }

    /// <summary>Branch membership and current dispositions in canonical branch order.</summary>
    public ImmutableArray<ProcessForkBranchState> Branches { get; internal init; }

    /// <summary>Branches frozen when the Join threshold first becomes satisfied, possibly before final resolution.</summary>
    public ImmutableArray<ExecutionNodeId> SelectedBranches { get; internal init; }

    /// <summary>Whether the reciprocal Join has advanced the coordinator token.</summary>
    public bool Resolved { get; internal init; }

    /// <summary>Latest effective attributable admission point retained for deterministic recovery.</summary>
    public ProcessAdmissionOperatingPoint AdmissionOperatingPoint { get; internal init; }
}

/// <summary>Durable lifecycle of one exact child Process invocation.</summary>
public enum ProcessChildDisposition
{
    /// <summary>No lifecycle disposition was supplied; invalid for persisted child state.</summary>
    Unspecified = 0,

    /// <summary>A bounded partition-work child identity is retained but its Request has not yet been emitted.</summary>
    Pending = 1,

    /// <summary>The child Request is outstanding and awaits one exact terminal Reply.</summary>
    Active = 2,

    /// <summary>The child produced a declared successful Request result.</summary>
    Completed = 3,

    /// <summary>The child produced a declared non-success terminal Request result.</summary>
    Failed = 4,

    /// <summary>Owner or partition closure retained an intent to propagate cancellation to started child work.</summary>
    CancellationRequested = 5,

    /// <summary>Owner or partition closure deliberately left started child work independently active.</summary>
    Detached = 6,

    /// <summary>
    /// Owner or partition closure, including sibling failure, cancelled bounded child work before its Request emitted.
    /// </summary>
    CancelledBeforeStart = 7,

    /// <summary>A propagated cancellation request reached one observed physical and semantic child closure.</summary>
    CancellationSettled = 8
}

/// <summary>Complete durable semantic state of one exact child Process invocation.</summary>
public sealed record ProcessChildState
{
    internal ProcessChildState(
        string registrationId,
        TokenId owner,
        TokenId token,
        ExecutionNodeId node,
        long occurrence,
        string? progressIdentity,
        ExecutionDefinitionReference process,
        ProcessContinuationIdentity continuation,
        ProcessChildPurpose purpose,
        ProcessChildCancellationPolicy cancellation,
        ProcessChildDisposition disposition,
        EmissionId? requestEmission = null,
        RequestTerminalOutcomeId? terminalOutcome = null,
        PortableValue? result = null)
        : this(
            registrationId,
            owner,
            token,
            node,
            occurrence,
            progressIdentity,
            process,
            continuation,
            purpose,
            cancellation,
            disposition,
            requestEmission,
            terminalOutcome,
            result,
            cancellationClosure: null)
    {
    }

    /// <summary>Creates one replay-stable child Process occurrence.</summary>
    /// <param name="registrationId">Opaque child occurrence identity.</param>
    /// <param name="owner">Parent coordination token that owns the child.</param>
    /// <param name="token">
    /// Replay-derived token that owns the child's canonical Request once started; pending bounded children have no
    /// token state or wait yet.
    /// </param>
    /// <param name="node">Canonical child-bearing Process node.</param>
    /// <param name="occurrence">Zero-based occurrence in the owner token history.</param>
    /// <param name="progressIdentity">Stable partition progress identity, or null for a direct invocation.</param>
    /// <param name="process">Exact child Process definition identity, revision, and fingerprint.</param>
    /// <param name="continuation">Interpreter-derived child Process instance and first attempt identity.</param>
    /// <param name="purpose">Explicit ordinary-work, compensation, or reconciliation purpose.</param>
    /// <param name="cancellation">Explicit parent-to-child cancellation policy.</param>
    /// <param name="disposition">Current durable child lifecycle disposition.</param>
    /// <param name="requestEmission">Canonical Request emission once the child has started.</param>
    /// <param name="terminalOutcome">Declared terminal Request outcome once observed.</param>
    /// <param name="result">Typed terminal outcome value once observed.</param>
    /// <param name="cancellationClosure">Exact closure evidence after propagated child cancellation.</param>
    [JsonConstructor]
    internal ProcessChildState(
        string registrationId,
        TokenId owner,
        TokenId token,
        ExecutionNodeId node,
        long occurrence,
        string? progressIdentity,
        ExecutionDefinitionReference process,
        ProcessContinuationIdentity continuation,
        ProcessChildPurpose purpose,
        ProcessChildCancellationPolicy cancellation,
        ProcessChildDisposition disposition,
        EmissionId? requestEmission,
        RequestTerminalOutcomeId? terminalOutcome,
        PortableValue? result,
        ProcessChildCancellationClosure? cancellationClosure)
    {
        RegistrationId = registrationId;
        Owner = owner;
        Token = token;
        Node = node;
        Occurrence = occurrence;
        ProgressIdentity = progressIdentity;
        Process = process;
        Continuation = continuation;
        Purpose = purpose;
        Cancellation = cancellation;
        Disposition = disposition;
        RequestEmission = requestEmission;
        TerminalOutcome = terminalOutcome;
        Result = result;
        CancellationClosure = cancellationClosure;
    }

    /// <summary>Opaque replay-stable child occurrence identity.</summary>
    public string RegistrationId { get; internal init; }

    /// <summary>Parent coordination token that owns the child.</summary>
    public TokenId Owner { get; internal init; }

    /// <summary>
    /// Replay-derived token that owns the child's canonical Request once started; it is not materialized for pending
    /// or cancelled-before-start bounded children.
    /// </summary>
    public TokenId Token { get; internal init; }

    /// <summary>Canonical child-bearing Process node.</summary>
    public ExecutionNodeId Node { get; internal init; }

    /// <summary>Zero-based occurrence in the owner token history.</summary>
    public long Occurrence { get; internal init; }

    /// <summary>Stable partition progress identity, or null for a direct invocation.</summary>
    public string? ProgressIdentity { get; internal init; }

    /// <summary>Exact child Process definition identity, revision, and fingerprint.</summary>
    public ExecutionDefinitionReference Process { get; internal init; }

    /// <summary>Interpreter-derived child Process instance and first attempt identity.</summary>
    public ProcessContinuationIdentity Continuation { get; internal init; }

    /// <summary>Explicit ordinary-work, compensation, or reconciliation purpose.</summary>
    public ProcessChildPurpose Purpose { get; internal init; }

    /// <summary>Explicit parent-to-child cancellation policy.</summary>
    public ProcessChildCancellationPolicy Cancellation { get; internal init; }

    /// <summary>Current durable child lifecycle disposition.</summary>
    public ProcessChildDisposition Disposition { get; internal init; }

    /// <summary>Canonical Request emission once the child has started.</summary>
    public EmissionId? RequestEmission { get; internal init; }

    /// <summary>Declared terminal Request outcome once observed.</summary>
    public RequestTerminalOutcomeId? TerminalOutcome { get; internal init; }

    /// <summary>Typed terminal outcome value once observed.</summary>
    public PortableValue? Result { get; internal init; }

    /// <summary>Exact physical and semantic closure observed after propagated child cancellation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProcessChildCancellationClosure? CancellationClosure { get; internal init; }
}

/// <summary>Durable phase of an authored cancellation-finalization protocol.</summary>
public enum ProcessCancellationFinalizationPhase
{
    /// <summary>No phase was supplied; invalid for retained cancellation state.</summary>
    Unspecified = 0,

    /// <summary>Normal work is closed while propagated child cancellations settle.</summary>
    WaitingForPropagatedChildren = 1,

    /// <summary>The exact cancellation-finalizer child Request is active.</summary>
    FinalizerActive = 2,

    /// <summary>The exact finalizer acknowledged the cancelled parent attempt.</summary>
    Acknowledged = 3,

    /// <summary>The finalizer failed, was cancelled or terminated, or returned invalid acknowledgement evidence.</summary>
    Failed = 4
}

/// <summary>Portable retained state of one authored cancellation-finalization occurrence.</summary>
public sealed record ProcessCancellationFinalizationState
{
    /// <summary>Creates exact retained cancellation-finalization state.</summary>
    /// <param name="intent">Accepted cancellation intent, including its causal command identity.</param>
    /// <param name="phase">Current closed cancellation-finalization phase.</param>
    /// <param name="requestedAtUtc">Explicit UTC time at which cancellation entered reference interpretation.</param>
    /// <param name="failure">Structured failure evidence exactly when <paramref name="phase"/> is failed.</param>
    [JsonConstructor]
    public ProcessCancellationFinalizationState(
        ProcessCancellationIntent intent,
        ProcessCancellationFinalizationPhase phase,
        DateTimeOffset requestedAtUtc,
        DocumentValidationDiagnostic? failure = null)
    {
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        if (intent.CommandId is null)
            throw new ArgumentException("Authored cancellation finalization requires a causal command identity.", nameof(intent));
        if (!Enum.IsDefined(phase) || phase == ProcessCancellationFinalizationPhase.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Cancellation-finalization phase must be explicit.");
        }
        if (requestedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Cancellation-finalization time must use the UTC offset.", nameof(requestedAtUtc));
        if ((phase == ProcessCancellationFinalizationPhase.Failed) != (failure is not null))
        {
            throw new ArgumentException(
                "Cancellation-finalization failure evidence must be present exactly for the failed phase.",
                nameof(failure));
        }
        if (failure is { Severity: not DiagnosticSeverity.Error })
            throw new ArgumentException("Cancellation-finalization failure evidence must be an error.", nameof(failure));

        Phase = phase;
        RequestedAtUtc = requestedAtUtc;
        Failure = failure;
    }

    /// <summary>Accepted cancellation intent and causal command identity.</summary>
    public ProcessCancellationIntent Intent { get; }

    /// <summary>Current cancellation-finalization phase.</summary>
    public ProcessCancellationFinalizationPhase Phase { get; }

    /// <summary>Explicit UTC time at which cancellation entered reference interpretation.</summary>
    public DateTimeOffset RequestedAtUtc { get; }

    /// <summary>Structured finalizer failure evidence.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentValidationDiagnostic? Failure { get; }
}

/// <summary>One retained partition value and its exact child occurrence.</summary>
/// <param name="ProgressIdentity">Authored stable progress identity for the partition.</param>
/// <param name="CapacityIdentity">Evaluated capacity-domain identity, or null when no domain policy is authored.</param>
/// <param name="Partition">Exact typed portable partition value evaluated once by the owner.</param>
/// <param name="ChildRegistrationId">Replay-stable child occurrence identity for the partition.</param>
public sealed record ProcessPartitionWorkState(
    string ProgressIdentity,
    string? CapacityIdentity,
    PortableValue Partition,
    string ChildRegistrationId);

/// <summary>Durable coarse-grained coordination state for one bounded partition-work occurrence.</summary>
public sealed record ProcessPartitionState
{
    /// <summary>Creates one replay-stable bounded partition-work occurrence.</summary>
    /// <param name="registrationId">Opaque bounded-work occurrence identity.</param>
    /// <param name="owner">Parked coordinator token.</param>
    /// <param name="node">Canonical <see cref="ForEachPartitionProcessNode"/> identity.</param>
    /// <param name="occurrence">Zero-based occurrence in the owner token history.</param>
    /// <param name="work">Finite work set in canonical progress-identity order.</param>
    /// <param name="resolved">
    /// Whether the bounded occurrence is finalized after a successful join or owner termination/cancellation.
    /// </param>
    [JsonConstructor]
    internal ProcessPartitionState(
        string registrationId,
        TokenId owner,
        ExecutionNodeId node,
        long occurrence,
        ImmutableArray<ProcessPartitionWorkState> work,
        bool resolved)
    {
        RegistrationId = registrationId;
        Owner = owner;
        Node = node;
        Occurrence = occurrence;
        Work = work.IsDefault ? [] : work;
        Resolved = resolved;
    }

    /// <summary>Opaque replay-stable bounded-work occurrence identity.</summary>
    public string RegistrationId { get; internal init; }

    /// <summary>Parked coordinator token.</summary>
    public TokenId Owner { get; internal init; }

    /// <summary>Canonical <see cref="ForEachPartitionProcessNode"/> identity.</summary>
    public ExecutionNodeId Node { get; internal init; }

    /// <summary>Zero-based occurrence in the owner token history.</summary>
    public long Occurrence { get; internal init; }

    /// <summary>Finite work set in canonical progress-identity order.</summary>
    public ImmutableArray<ProcessPartitionWorkState> Work { get; internal init; }

    /// <summary>Whether the bounded occurrence is finalized after a successful join or owner termination/cancellation.</summary>
    public bool Resolved { get; internal init; }
}

/// <summary>Durable progress state for one explicit recurrence across activations.</summary>
public sealed record ProcessRecurrenceState
{
    /// <summary>Creates one replay-stable recurrence occurrence.</summary>
    /// <param name="registrationId">Opaque recurrence occurrence identity.</param>
    /// <param name="token">Token executing the recurrence.</param>
    /// <param name="node">Canonical recurrence node.</param>
    /// <param name="occurrence">Zero-based originating occurrence in the token history.</param>
    /// <param name="repeatCount">Number of admitted repeat decisions.</param>
    /// <param name="unchangedProgressCount">Consecutive repeat decisions with unchanged progress.</param>
    /// <param name="lastProgress">Last exact typed progress value, or null before the first repeat.</param>
    /// <param name="active">Whether later execution may continue this recurrence occurrence.</param>
    [JsonConstructor]
    internal ProcessRecurrenceState(
        string registrationId,
        TokenId token,
        ExecutionNodeId node,
        long occurrence,
        int repeatCount,
        int unchangedProgressCount,
        PortableValue? lastProgress,
        bool active)
    {
        RegistrationId = registrationId;
        Token = token;
        Node = node;
        Occurrence = occurrence;
        RepeatCount = repeatCount;
        UnchangedProgressCount = unchangedProgressCount;
        LastProgress = lastProgress;
        Active = active;
    }

    /// <summary>Opaque replay-stable recurrence occurrence identity.</summary>
    public string RegistrationId { get; internal init; }

    /// <summary>Token executing the recurrence.</summary>
    public TokenId Token { get; internal init; }

    /// <summary>Canonical recurrence node.</summary>
    public ExecutionNodeId Node { get; internal init; }

    /// <summary>Zero-based originating occurrence in the token history.</summary>
    public long Occurrence { get; internal init; }

    /// <summary>Number of admitted repeat decisions.</summary>
    public int RepeatCount { get; internal init; }

    /// <summary>Consecutive repeat decisions with unchanged progress.</summary>
    public int UnchangedProgressCount { get; internal init; }

    /// <summary>Last exact typed progress value, or null before the first repeat.</summary>
    public PortableValue? LastProgress { get; internal init; }

    /// <summary>Whether later execution may continue this recurrence occurrence.</summary>
    public bool Active { get; internal init; }
}

/// <summary>Kind of durable semantic wait held by a Process token.</summary>
public enum ProcessWaitKind
{
    /// <summary>No kind was supplied; invalid for a wait.</summary>
    Unspecified = 0,

    /// <summary>A closed AwaitMatch clause set.</summary>
    AwaitMatch = 1,

    /// <summary>A single absolute Timer node.</summary>
    Timer = 2,

    /// <summary>An explicit authored DurableCut.</summary>
    DurableCut = 3,

    /// <summary>An emitted Request awaiting one terminal Reply.</summary>
    Request = 4,

    /// <summary>A bounded partition coordinator awaiting child capacity or terminal outcomes.</summary>
    PartitionBatch = 5,

    /// <summary>An explicit recurrence cut that may resume only in a later activation.</summary>
    RepeatAcrossActivation = 6
}

/// <summary>Computed deadline of one durable timer clause.</summary>
/// <param name="Clause">Clause identity, or the owning Timer node identity.</param>
/// <param name="DueAtUtc">Absolute UTC eligibility instant computed when the wait was registered.</param>
/// <param name="Priority">Authored arbitration priority.</param>
public sealed record ProcessTimerState(
    ExecutionNodeId Clause,
    DateTimeOffset DueAtUtc,
    int Priority);

/// <summary>Complete durable semantic wait state for one token-node occurrence.</summary>
public sealed record ProcessWaitState
{
    [JsonConstructor]
    internal ProcessWaitState(
        ProcessWaitRegistrationId registrationId,
        TokenId token,
        ExecutionNodeId node,
        long occurrence,
        ProcessWaitKind kind,
        DateTimeOffset registeredAtUtc,
        ImmutableArray<ProcessTimerState> timers,
        bool active,
        ExecutionNodeId? winnerClause = null,
        EmissionId? winnerInput = null,
        EmissionId? obligationEmission = null)
    {
        RegistrationId = registrationId;
        Token = token;
        Node = node;
        Occurrence = occurrence;
        Kind = kind;
        RegisteredAtUtc = registeredAtUtc;
        Timers = timers.IsDefault ? [] : timers;
        Active = active;
        WinnerClause = winnerClause;
        WinnerInput = winnerInput;
        ObligationEmission = obligationEmission;
    }

    /// <summary>Stable identity of this exact durable wait occurrence.</summary>
    public ProcessWaitRegistrationId RegistrationId { get; internal init; }

    /// <summary>Token held by the wait.</summary>
    public TokenId Token { get; internal init; }

    /// <summary>Canonical wait node.</summary>
    public ExecutionNodeId Node { get; internal init; }

    /// <summary>Zero-based token step that supplied the replay basis for this exact wait registration.</summary>
    public long Occurrence { get; internal init; }

    /// <summary>Semantic wait kind.</summary>
    public ProcessWaitKind Kind { get; internal init; }

    /// <summary>Explicit UTC registration time.</summary>
    public DateTimeOffset RegisteredAtUtc { get; internal init; }

    /// <summary>Computed absolute timers retained without reevaluation.</summary>
    public ImmutableArray<ProcessTimerState> Timers { get; internal init; }

    /// <summary>Whether the wait may still choose a winner or resume.</summary>
    public bool Active { get; internal init; }

    /// <summary>Winning AwaitMatch clause after arbitration.</summary>
    public ExecutionNodeId? WinnerClause { get; internal init; }

    /// <summary>Winning interaction identity, or null when a timer won.</summary>
    public EmissionId? WinnerInput { get; internal init; }

    /// <summary>Logical Request emission discharged by this wait, when <see cref="Kind"/> is Request.</summary>
    public EmissionId? ObligationEmission { get; internal init; }
}

/// <summary>Disposition assigned to one presented Process input.</summary>
public enum ProcessInputAdmissionDisposition
{
    /// <summary>No disposition was supplied.</summary>
    Unspecified = 0,

    /// <summary>The input was retained before a compatible wait existed.</summary>
    Buffered = 1,

    /// <summary>The input won and advanced exactly one continuation.</summary>
    Consumed = 2,

    /// <summary>The same logical input had already been admitted.</summary>
    Duplicate = 3,

    /// <summary>The input targeted a wait that already selected another winner.</summary>
    Late = 4,

    /// <summary>The input targeted an incompatible Process attempt or continuation.</summary>
    Stale = 5,

    /// <summary>No compatible target or retained wait tombstone was found.</summary>
    MissingTarget = 6,

    /// <summary>The authored policy rejected the input.</summary>
    Rejected = 7,

    /// <summary>The authored policy retained the input only as observable evidence.</summary>
    Observed = 8,

    /// <summary>The authored policy routed the input to a dead-letter interpretation.</summary>
    DeadLettered = 9,

    /// <summary>The logical emission identity was reused for different canonical input content or target.</summary>
    IdentityConflict = 10,

    /// <summary>The owning token became terminal before the buffered input could be consumed.</summary>
    TerminalUnconsumed = 11
}

/// <summary>Semantic classification that caused one presented Process input to receive its policy disposition.</summary>
public enum ProcessInputAdmissionReason
{
    /// <summary>No classification was supplied; invalid for canonical admission evidence.</summary>
    Unspecified = 0,

    /// <summary>The input arrived before a compatible durable wait existed and was retained for later inspection.</summary>
    Early = 1,

    /// <summary>The input won a compatible wait and advanced exactly one continuation.</summary>
    Consumed = 2,

    /// <summary>The same logical input and canonical content had already been presented.</summary>
    Duplicate = 3,

    /// <summary>The input targeted a compatible wait occurrence that had already resolved.</summary>
    Late = 4,

    /// <summary>The input targeted an incompatible Process attempt, continuation, or guarded wait state.</summary>
    Stale = 5,

    /// <summary>No unambiguous compatible token or wait occurrence could receive the input.</summary>
    MissingTarget = 6,

    /// <summary>Another input or timer won the same exclusive AwaitMatch arbitration.</summary>
    Superseded = 7,

    /// <summary>The logical emission identity was reused for different canonical input content or target.</summary>
    IdentityConflict = 8,

    /// <summary>The owning token became terminal before its retained early input could be consumed.</summary>
    TerminalUnconsumed = 9,

    /// <summary>The presented interaction envelope violated its exact canonical contract.</summary>
    InvalidEnvelope = 10,

    /// <summary>A valid envelope did not satisfy the exact outstanding Request or authored terminal-outcome contract.</summary>
    ContractMismatch = 11,

    /// <summary>The input matched an already active wait and was staged for deterministic arbitration.</summary>
    WaitCandidate = 12
}

/// <summary>One input retained until a compatible durable wait can inspect it.</summary>
/// <param name="Input">Exact canonical input and target.</param>
/// <param name="BufferedAtUtc">Explicit UTC admission time.</param>
public sealed record ProcessBufferedInput(ProcessActivationInput Input, DateTimeOffset BufferedAtUtc);

/// <summary>Durable semantic disposition evidence for one presented canonical input occurrence.</summary>
/// <param name="Input">Exact canonical envelope and token target used to detect replay or identity conflict.</param>
/// <param name="Disposition">Policy action or fallback disposition applied to the input.</param>
/// <param name="Reason">Semantic classification that caused the disposition.</param>
/// <param name="ObservedAtUtc">Explicit UTC decision time.</param>
/// <param name="WaitRegistrationId">Compatible wait registration when one was resolved.</param>
public sealed record ProcessInputReceipt(
    ProcessActivationInput Input,
    ProcessInputAdmissionDisposition Disposition,
    ProcessInputAdmissionReason Reason,
    DateTimeOffset ObservedAtUtc,
    ProcessWaitRegistrationId? WaitRegistrationId = null)
{
    /// <summary>Stable logical input identity projected from <see cref="Input"/>.</summary>
    public EmissionId Emission => Input.Envelope.Context.EmissionId;

    /// <summary>Exact token target projected from <see cref="Input"/>.</summary>
    public ProcessTokenInteractionTarget Target => Input.Target;

    /// <summary>Determines whether this receipt carries a valid closed reason and compatible policy disposition.</summary>
    /// <returns><see langword="true"/> when the admission evidence is canonical; otherwise, <see langword="false"/>.</returns>
    public bool IsValidAdmissionEvidence() =>
        IsValidAdmissionEvidence(Disposition, Reason, WaitRegistrationId);

    /// <summary>
    /// Determines whether an input-admission reason, policy disposition, and wait occurrence form valid canonical
    /// evidence.
    /// </summary>
    /// <param name="disposition">Policy action or fallback disposition to validate.</param>
    /// <param name="reason">Semantic admission classification to validate.</param>
    /// <param name="waitRegistrationId">Exact wait occurrence participating in the decision, when required.</param>
    /// <returns>
    /// <see langword="true"/> when the values are closed, mutually compatible, and carry the required wait
    /// occurrence; otherwise, <see langword="false"/>.
    /// </returns>
    public static bool IsValidAdmissionEvidence(
        ProcessInputAdmissionDisposition disposition,
        ProcessInputAdmissionReason reason,
        ProcessWaitRegistrationId? waitRegistrationId)
    {
        if (!Enum.IsDefined(disposition)
            || disposition == ProcessInputAdmissionDisposition.Unspecified
            || !Enum.IsDefined(reason)
            || reason == ProcessInputAdmissionReason.Unspecified)
        {
            return false;
        }

        var pairIsValid = reason switch
        {
            ProcessInputAdmissionReason.Early =>
                disposition == ProcessInputAdmissionDisposition.Buffered,
            ProcessInputAdmissionReason.Consumed =>
                disposition == ProcessInputAdmissionDisposition.Consumed,
            ProcessInputAdmissionReason.Duplicate => disposition is
                ProcessInputAdmissionDisposition.Buffered
                or ProcessInputAdmissionDisposition.Consumed
                or ProcessInputAdmissionDisposition.Duplicate
                or ProcessInputAdmissionDisposition.Late
                or ProcessInputAdmissionDisposition.Stale
                or ProcessInputAdmissionDisposition.MissingTarget
                or ProcessInputAdmissionDisposition.Rejected
                or ProcessInputAdmissionDisposition.Observed
                or ProcessInputAdmissionDisposition.DeadLettered,
            ProcessInputAdmissionReason.Late => disposition is
                ProcessInputAdmissionDisposition.Late
                or ProcessInputAdmissionDisposition.Rejected
                or ProcessInputAdmissionDisposition.Observed
                or ProcessInputAdmissionDisposition.Consumed,
            ProcessInputAdmissionReason.Stale => disposition is
                ProcessInputAdmissionDisposition.Stale
                or ProcessInputAdmissionDisposition.Rejected
                or ProcessInputAdmissionDisposition.Observed
                or ProcessInputAdmissionDisposition.Consumed,
            ProcessInputAdmissionReason.MissingTarget => disposition is
                ProcessInputAdmissionDisposition.MissingTarget
                or ProcessInputAdmissionDisposition.Rejected
                or ProcessInputAdmissionDisposition.Observed
                or ProcessInputAdmissionDisposition.DeadLettered,
            ProcessInputAdmissionReason.Superseded => disposition is
                ProcessInputAdmissionDisposition.Late
                or ProcessInputAdmissionDisposition.Rejected
                or ProcessInputAdmissionDisposition.Observed
                or ProcessInputAdmissionDisposition.Consumed,
            ProcessInputAdmissionReason.IdentityConflict =>
                disposition == ProcessInputAdmissionDisposition.IdentityConflict,
            ProcessInputAdmissionReason.TerminalUnconsumed =>
                disposition == ProcessInputAdmissionDisposition.TerminalUnconsumed,
            ProcessInputAdmissionReason.InvalidEnvelope or ProcessInputAdmissionReason.ContractMismatch =>
                disposition == ProcessInputAdmissionDisposition.Rejected,
            ProcessInputAdmissionReason.WaitCandidate =>
                disposition == ProcessInputAdmissionDisposition.Buffered,
            _ => false
        };

        if (!pairIsValid)
            return false;

        return reason switch
        {
            ProcessInputAdmissionReason.WaitCandidate
                or ProcessInputAdmissionReason.Consumed
                or ProcessInputAdmissionReason.Late
                or ProcessInputAdmissionReason.Superseded
                or ProcessInputAdmissionReason.ContractMismatch => waitRegistrationId is not null,
            ProcessInputAdmissionReason.Early
                or ProcessInputAdmissionReason.TerminalUnconsumed => waitRegistrationId is null,
            _ => true
        };
    }
}

/// <summary>One emitted Request obligation awaiting a terminal Reply.</summary>
/// <param name="Token">Token parked on the Request node.</param>
/// <param name="Node">Canonical Request node identity.</param>
/// <param name="Emission">Stable logical Request emission identity.</param>
/// <param name="Contract">Exact typed Request contract.</param>
/// <param name="RegisteredAtUtc">Explicit UTC Request emission time.</param>
public sealed record ProcessOutstandingRequest(
    TokenId Token,
    ExecutionNodeId Node,
    EmissionId Emission,
    RequestContractReference Contract,
    DateTimeOffset RegisteredAtUtc);

/// <summary>Complete immutable semantic continuation of one canonical Process attempt.</summary>
public sealed class ProcessContinuationState
{
    internal ProcessContinuationState(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation,
        long completedActivationCount,
        ImmutableArray<ProcessTokenState> tokens,
        ImmutableArray<ProcessForkState> forks,
        ImmutableArray<ProcessChildState> children,
        ImmutableArray<ProcessPartitionState> partitions,
        ImmutableArray<ProcessRecurrenceState> recurrences,
        ImmutableArray<ProcessWaitState> waits,
        ImmutableArray<ProcessBufferedInput> bufferedInputs,
        ImmutableArray<ProcessInputReceipt> inputReceipts,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests,
        ExecutionTerminalOutcome terminal)
        : this(
            definition,
            continuation,
            completedActivationCount,
            tokens,
            forks,
            children,
            partitions,
            recurrences,
            waits,
            bufferedInputs,
            inputReceipts,
            outstandingRequests,
            terminal,
            cancellationFinalization: null)
    {
    }

    [JsonConstructor]
    internal ProcessContinuationState(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation,
        long completedActivationCount,
        ImmutableArray<ProcessTokenState> tokens,
        ImmutableArray<ProcessForkState> forks,
        ImmutableArray<ProcessChildState> children,
        ImmutableArray<ProcessPartitionState> partitions,
        ImmutableArray<ProcessRecurrenceState> recurrences,
        ImmutableArray<ProcessWaitState> waits,
        ImmutableArray<ProcessBufferedInput> bufferedInputs,
        ImmutableArray<ProcessInputReceipt> inputReceipts,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests,
        ExecutionTerminalOutcome terminal,
        ProcessCancellationFinalizationState? cancellationFinalization)
    {
        Definition = definition;
        Continuation = continuation;
        CompletedActivationCount = completedActivationCount;
        Tokens = tokens.IsDefault ? [] : tokens;
        Forks = forks.IsDefault ? [] : forks;
        Children = children.IsDefault ? [] : children;
        Partitions = partitions.IsDefault ? [] : partitions;
        Recurrences = recurrences.IsDefault ? [] : recurrences;
        Waits = waits.IsDefault ? [] : waits;
        BufferedInputs = bufferedInputs.IsDefault ? [] : bufferedInputs;
        InputReceipts = inputReceipts.IsDefault ? [] : inputReceipts;
        OutstandingRequests = outstandingRequests.IsDefault ? [] : outstandingRequests;
        Terminal = terminal;
        CancellationFinalization = cancellationFinalization;
    }

    /// <summary>Exact pinned Process definition identity, revision, and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Logical Process instance and exact current attempt.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Number of finite activations committed into this semantic continuation.</summary>
    public long CompletedActivationCount { get; }

    /// <summary>Complete token set in stable token-identity order.</summary>
    public ImmutableArray<ProcessTokenState> Tokens { get; }

    /// <summary>Fork and Join membership in stable registration order.</summary>
    public ImmutableArray<ProcessForkState> Forks { get; }

    /// <summary>Exact child Process occurrences in stable registration order.</summary>
    public ImmutableArray<ProcessChildState> Children { get; }

    /// <summary>Bounded partition-work occurrences in stable registration order.</summary>
    public ImmutableArray<ProcessPartitionState> Partitions { get; }

    /// <summary>Retained recurrence occurrences in stable registration order.</summary>
    public ImmutableArray<ProcessRecurrenceState> Recurrences { get; }

    /// <summary>Active waits and retained wait tombstones in stable registration order.</summary>
    public ImmutableArray<ProcessWaitState> Waits { get; }

    /// <summary>Early interaction inputs not yet consumed by a compatible wait.</summary>
    public ImmutableArray<ProcessBufferedInput> BufferedInputs { get; }

    /// <summary>Input admission and disposition ledger in stable logical identity order.</summary>
    public ImmutableArray<ProcessInputReceipt> InputReceipts { get; }

    /// <summary>Logical Request obligations awaiting terminal Replies.</summary>
    public ImmutableArray<ProcessOutstandingRequest> OutstandingRequests { get; }

    /// <summary>Terminal outcome, or the canonical nonterminal outcome value.</summary>
    public ExecutionTerminalOutcome Terminal { get; }

    /// <summary>Authored cancellation-finalization state, absent for ordinary or immediately cancelled Processes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProcessCancellationFinalizationState? CancellationFinalization { get; }
}
