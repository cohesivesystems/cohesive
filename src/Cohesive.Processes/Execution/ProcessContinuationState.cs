using System.Collections.Immutable;
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
    Request = 4
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
    internal ProcessWaitState(
        string registrationId,
        TokenId token,
        ExecutionNodeId node,
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
        Kind = kind;
        RegisteredAtUtc = registeredAtUtc;
        Timers = timers.IsDefault ? [] : timers;
        Active = active;
        WinnerClause = winnerClause;
        WinnerInput = winnerInput;
        ObligationEmission = obligationEmission;
    }

    /// <summary>Opaque stable wait-registration identity.</summary>
    public string RegistrationId { get; internal init; }

    /// <summary>Token held by the wait.</summary>
    public TokenId Token { get; internal init; }

    /// <summary>Canonical wait node.</summary>
    public ExecutionNodeId Node { get; internal init; }

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

/// <summary>One input retained until a compatible durable wait can inspect it.</summary>
/// <param name="Input">Exact canonical input and target.</param>
/// <param name="BufferedAtUtc">Explicit UTC admission time.</param>
public sealed record ProcessBufferedInput(ProcessActivationInput Input, DateTimeOffset BufferedAtUtc);

/// <summary>Durable semantic disposition evidence for one presented canonical input occurrence.</summary>
/// <param name="Input">Exact canonical envelope and token target used to detect replay or identity conflict.</param>
/// <param name="Disposition">Semantic admission disposition.</param>
/// <param name="ObservedAtUtc">Explicit UTC decision time.</param>
/// <param name="WaitRegistrationId">Compatible wait registration when one was resolved.</param>
public sealed record ProcessInputReceipt(
    ProcessActivationInput Input,
    ProcessInputAdmissionDisposition Disposition,
    DateTimeOffset ObservedAtUtc,
    string? WaitRegistrationId = null)
{
    /// <summary>Stable logical input identity projected from <see cref="Input"/>.</summary>
    public EmissionId Emission => Input.Envelope.Context.EmissionId;

    /// <summary>Exact token target projected from <see cref="Input"/>.</summary>
    public ProcessTokenInteractionTarget Target => Input.Target;
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
        ImmutableArray<ProcessWaitState> waits,
        ImmutableArray<ProcessBufferedInput> bufferedInputs,
        ImmutableArray<ProcessInputReceipt> inputReceipts,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests,
        ExecutionTerminalOutcome terminal)
    {
        Definition = definition;
        Continuation = continuation;
        CompletedActivationCount = completedActivationCount;
        Tokens = tokens.IsDefault ? [] : tokens;
        Forks = forks.IsDefault ? [] : forks;
        Waits = waits.IsDefault ? [] : waits;
        BufferedInputs = bufferedInputs.IsDefault ? [] : bufferedInputs;
        InputReceipts = inputReceipts.IsDefault ? [] : inputReceipts;
        OutstandingRequests = outstandingRequests.IsDefault ? [] : outstandingRequests;
        Terminal = terminal;
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
}
