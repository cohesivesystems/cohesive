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
    Paused,
    /// <summary>Disable admission and drain admitted work; reactivation must be explicit.</summary>
    Deactivated
}

/// <summary>Lifecycle operations authorized by the host, independently of provider dispatch policy.</summary>
public enum IntegrationLifecycleAction
{
    /// <summary>Change desired admission state.</summary>
    Change,
    /// <summary>Admit a new exact Process session.</summary>
    Admit,
    /// <summary>Record successful completion of an admitted Process.</summary>
    Complete
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
/// <param name="Revision">Monotonic desired-state revision, initially zero.</param>
/// <param name="Desired">Desired admission state, initially Deactivated.</param>
/// <param name="LastChange">Last accepted command, or null before explicit activation.</param>
/// <param name="Pending">Admitted sessions without confirmed successful Process completion.</param>
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
