using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Integrations;

/// <summary>Operator intent to close an admitted failed session after all effects have been accounted for.</summary>
/// <param name="Id">Stable command identity, reused unchanged on retry.</param>
/// <param name="Admission">Original exact admission receipt.</param>
/// <param name="ExpectedRevision">Observed lifecycle control revision.</param>
/// <param name="EvidenceReference">Immutable host-owned evidence identifying the failed attempt and accounting for every dispatched effect.</param>
/// <param name="Authorization">Authenticated operator and policy decision evidence.</param>
/// <param name="AtUtc">UTC command observation time, no earlier than admission.</param>
public sealed record IntegrationFailureResolutionRequest(string Id, IntegrationAdmission Admission, long ExpectedRevision,
    string EvidenceReference, ProcessControlAuthorizationContext Authorization, DateTimeOffset AtUtc);

/// <summary>Disposition of an explicit failed-session resolution.</summary>
public enum IntegrationFailureResolutionOutcome
{
    /// <summary>The failed session was removed from pending work, with evidence retained.</summary>
    Applied,
    /// <summary>The original exact command receipt was returned.</summary>
    Replayed,
    /// <summary>The host denied this operator action.</summary>
    Unauthorized,
    /// <summary>The lifecycle control revision changed; inspect before issuing a new command.</summary>
    RevisionConflict,
    /// <summary>The command identity was reused with different intent or attribution.</summary>
    IdentityConflict,
    /// <summary>The supplied receipt is not the original admission.</summary>
    AdmissionMismatch,
    /// <summary>The session already has successful completion or a different failure-resolution receipt.</summary>
    AlreadyClosed,
    /// <summary>No host verifier is configured or it could not establish complete effect accounting.</summary>
    EvidenceRejected,
    /// <summary>The verified Process evidence does not describe this exact failed attempt.</summary>
    DecisionMismatch,
    /// <summary>The control revision cannot advance further.</summary>
    RevisionExhausted
}

/// <summary>Retained resolution attribution and verified terminal failure; not successful completion or accepted coverage.</summary>
public sealed class IntegrationFailureResolution
{
    internal IntegrationFailureResolution(IntegrationFailureResolutionRequest command, ProcessActivationDecision decision, long revision)
    { Command = command; Decision = decision; Revision = revision; }
    /// <summary>Original operator intent and immutable evidence locator.</summary>
    public IntegrationFailureResolutionRequest Command { get; }
    /// <summary>Host-verified terminal failure of the exact admitted Process attempt.</summary>
    public ProcessActivationDecision Decision { get; }
    /// <summary>Control revision after this resolution; desired admission state is unchanged.</summary>
    public long Revision { get; }
}
