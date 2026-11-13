using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Integrations;

/// <summary>Desired admission state of one independently controlled ingestion consumer.</summary>
public enum IntegrationDesiredState
{
    /// <summary>Permit new sessions; existing sessions continue under their own Process and dispatch policies.</summary>
    Active,
    /// <summary>Stop new sessions and drain admitted work; resume by explicitly selecting Active.</summary>
    Paused
}

/// <summary>Lifecycle operations authorized by the host, independently of provider dispatch policy.</summary>
public enum IntegrationLifecycleAction
{
    /// <summary>Change desired admission state.</summary>
    Change,
    /// <summary>Admit a new exact Process session.</summary>
    Admit,
    /// <summary>Record successful completion of an admitted Process.</summary>
    Complete,
    /// <summary>Resolve a failed session using host-verified complete effect accounting.</summary>
    ResolveFailure
}

/// <summary>Attributable, revision-aware desired-state command; this is runtime intent, not a Process definition.</summary>
/// <param name="Id">Stable command identity reused on retries.</param>
/// <param name="ExpectedRevision">Exact lifecycle revision observed by the caller.</param>
/// <param name="Desired">New desired admission state.</param>
/// <param name="Authorization">Host-authenticated actor, authority and policy decision evidence.</param>
/// <param name="AtUtc">Explicit UTC command observation time.</param>
public sealed record IntegrationLifecycleChange(string Id, long ExpectedRevision, IntegrationDesiredState Desired,
    ProcessControlAuthorizationContext Authorization, DateTimeOffset AtUtc);

/// <summary>Retained admission to an exact Process session and integration binding.</summary>
/// <param name="Consumer">Stable independently controlled consumer.</param>
/// <param name="Continuation">Exact Process instance and attempt; retries cannot substitute another binding.</param>
/// <param name="Binding">Canonical admitted integration binding.</param>
/// <param name="Process">Canonical Process definition owning execution.</param>
/// <param name="LifecycleRevision">Admission revision, preserved across lifecycle changes.</param>
/// <param name="Authorization">Host-authenticated admission evidence.</param>
/// <param name="AtUtc">Original UTC admission time.</param>
public sealed record IntegrationAdmission(IngestionLedgerAddress Consumer, ProcessContinuationIdentity Continuation,
    ExecutionDefinitionReference Binding, ExecutionDefinitionReference Process, long LifecycleRevision,
    ProcessControlAuthorizationContext Authorization, DateTimeOffset AtUtc);

/// <summary>Immutable lifecycle inspection; execution health and accepted data coverage are separate observations.</summary>
/// <param name="Consumer">Controlled consumer.</param>
/// <param name="Revision">Monotonic control revision for desired-state changes and failure resolutions, initially zero.</param>
/// <param name="Desired">Desired admission state, initially Paused.</param>
/// <param name="LastChange">Last accepted desired-state command, or null before any desired-state change.</param>
/// <param name="Pending">Admitted sessions without successful completion or an explicit verified failure resolution.</param>
public sealed record IntegrationLifecycleSnapshot(IngestionLedgerAddress Consumer, long Revision,
    IntegrationDesiredState Desired, IntegrationLifecycleChange? LastChange, ImmutableArray<IntegrationAdmission> Pending)
{
    /// <summary>Whether a new session may be admitted, subject to revision and authorization checks.</summary>
    public bool Accepting => Desired == IntegrationDesiredState.Active;
    /// <summary>Whether admission is stopped while previously admitted work remains unresolved.</summary>
    public bool Draining => !Accepting && !Pending.IsEmpty;
}

/// <summary>Retained host-attributed completion evidence for an exact admitted session.</summary>
/// <param name="Admission">Original admission receipt.</param>
/// <param name="Decision">Original successful Process decision, including execution evidence.</param>
/// <param name="Authorization">Host-authenticated recorder and authorization evidence.</param>
public sealed record IntegrationCompletion(IntegrationAdmission Admission, ProcessActivationDecision Decision,
    ProcessControlAuthorizationContext Authorization);

/// <summary>Possible dispositions of a desired-state change.</summary>
public enum IntegrationChangeOutcome
{
    /// <summary>New desired state was accepted.</summary>
    Applied,
    /// <summary>The original change receipt was returned without mutation.</summary>
    Replayed,
    /// <summary>Host authorization denied the change.</summary>
    Unauthorized,
    /// <summary>Inspect current revision and reconcile intent before retrying.</summary>
    RevisionConflict,
    /// <summary>The command identity was reused with changed intent.</summary>
    IdentityConflict,
    /// <summary>The lifecycle revision cannot advance further.</summary>
    RevisionExhausted
}

/// <summary>Possible dispositions of session admission.</summary>
public enum IntegrationAdmissionOutcome
{
    /// <summary>A new session was admitted.</summary>
    Applied,
    /// <summary>The original admission receipt was returned without another admission.</summary>
    Replayed,
    /// <summary>Host authorization denied admission.</summary>
    Unauthorized,
    /// <summary>Inspect current revision and reconcile intent before retrying.</summary>
    RevisionConflict,
    /// <summary>The session identity was reused with changed intent.</summary>
    IdentityConflict,
    /// <summary>New sessions are stopped; a fresh revision alone will not permit admission.</summary>
    AdmissionStopped,
    /// <summary>The bound token belongs to a different controller.</summary>
    BindingMismatch
}

/// <summary>Possible dispositions of recording Process completion.</summary>
public enum IntegrationCompletionOutcome
{
    /// <summary>Successful completion was recorded.</summary>
    Applied,
    /// <summary>The original completion receipt was returned without mutation.</summary>
    Replayed,
    /// <summary>Completion already existed; the receipt retains a different original recorder.</summary>
    ReplayedWithOriginalRecorder,
    /// <summary>Host authorization denied completion recording.</summary>
    Unauthorized,
    /// <summary>Completion does not match an admitted session.</summary>
    CompletionMismatch,
    /// <summary>The Process has not confirmed successful completion.</summary>
    Unresolved,
    /// <summary>This admission was already closed through explicit failure resolution.</summary>
    ResolvedFailure
}

/// <summary>Typed operation result with a receipt only for accepted outcomes.</summary>
/// <typeparam name="TOutcome">Operation-specific disposition enum.</typeparam>
/// <typeparam name="TReceipt">Original immutable receipt returned on acceptance or replay.</typeparam>
public sealed class IntegrationLifecycleResult<TOutcome, TReceipt> where TOutcome : struct, Enum where TReceipt : class
{
    readonly TReceipt? receipt;
    internal IntegrationLifecycleResult(TOutcome outcome, TReceipt? receipt = null)
    { Outcome = outcome; this.receipt = receipt; }
    /// <summary>Machine-readable disposition.</summary>
    public TOutcome Outcome { get; }
    /// <summary>Whether this result carries an accepted original receipt.</summary>
    public bool IsAccepted => receipt is not null;
    /// <summary>Accepted original receipt; check IsAccepted or Outcome before accessing it.</summary>
    /// <exception cref="InvalidOperationException">The operation was rejected.</exception>
    public TReceipt Receipt => receipt ?? throw new InvalidOperationException($"Lifecycle operation was rejected: {Outcome}.");
}

/// <summary>Compact original change receipt; pending sessions are inspected separately.</summary>
/// <param name="Command">Accepted intent and actor.</param>
/// <param name="Revision">Resulting desired-state revision.</param>
public sealed record IntegrationLifecycleChangeReceipt(IntegrationLifecycleChange Command, long Revision);

/// <summary>Opaque validated binding owned by one controller. Reuse it across session admissions.</summary>
/// <remarks>This local binding currently belongs to InMemoryIntegrationLifecycle. A durable lifecycle owner
/// and its snapshot contract are not modeled by this type; no durable hosting capability is implied.</remarks>
public sealed class IntegrationAdmissionBinding
{
    internal InMemoryIntegrationLifecycle Owner { get; }
    internal IntegrationAdmissionBinding(InMemoryIntegrationLifecycle owner, ExecutionDefinitionReference binding, ExecutionDefinitionReference process)
    { Owner = owner; Binding = binding; Process = process; }
    /// <summary>Inspects the bound controller's current lifecycle and optional exact provider evidence.</summary>
    /// <param name="atUtc">Explicit UTC inspection boundary; clock skew is reported rather than rejected.</param>
    /// <param name="providerEvidence">Optional authenticated evidence for this exact binding.</param>
    /// <returns>A read-only view using the owner controller's snapshot, with retained timestamps and skew diagnostics.</returns>
    /// <exception cref="ArgumentException">Inspection time is not UTC or evidence belongs to another binding.</exception>
    public IntegrationInspection Inspect(DateTimeOffset atUtc, IntegrationHealthEvidence? providerEvidence = null) =>
        IntegrationInspection.Create(this, atUtc, providerEvidence);

    /// <summary>Exact validated canonical integration binding.</summary>
    public ExecutionDefinitionReference Binding { get; }
    /// <summary>Exact validated canonical Process.</summary>
    public ExecutionDefinitionReference Process { get; }
}

/// <summary>Attributable admission intent; reuse the complete request for an exact retry.</summary>
/// <param name="Binding">Token validated once by this controller's Bind method.</param>
/// <param name="Continuation">Stable Process instance and attempt.</param>
/// <param name="ExpectedRevision">Original expected lifecycle revision.</param>
/// <param name="Authorization">Host-authenticated actor and authority.</param>
/// <param name="AtUtc">Original UTC admission observation.</param>
public sealed record IntegrationAdmissionRequest(IntegrationAdmissionBinding Binding, ProcessContinuationIdentity Continuation,
    long ExpectedRevision, ProcessControlAuthorizationContext Authorization, DateTimeOffset AtUtc);

/// <summary>Intent to record successful completion of an admitted session.</summary>
/// <param name="Admission">Original admission receipt.</param>
/// <param name="Decision">Trusted Process completion evidence.</param>
/// <param name="Authorization">Host-authenticated completion recorder.</param>
public sealed record IntegrationCompletionRequest(IntegrationAdmission Admission, ProcessActivationDecision Decision,
    ProcessControlAuthorizationContext Authorization);
