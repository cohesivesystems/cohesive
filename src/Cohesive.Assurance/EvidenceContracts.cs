namespace Cohesive.Assurance;

/// <summary>Outcome of the interpretation attempt, separate from its semantic verdict.</summary>
public enum AssuranceAttemptStatus
{
    /// <summary>The supported batch was evaluated; its semantic verdict may still be inconclusive.</summary>
    Completed,
    /// <summary>The supplied observations or mappings are contradictory or malformed.</summary>
    InvalidObservations,
    /// <summary>The required interpretation or arithmetic cannot be evaluated.</summary>
    Unsupported,
    /// <summary>An explicit evaluation bound prevented complete evaluation.</summary>
    BudgetExceeded,
    /// <summary>The verifier timed out without establishing a result.</summary>
    TimedOut,
    /// <summary>The attempt was cancelled without establishing a result.</summary>
    Cancelled,
    /// <summary>The solver or checker returned unknown.</summary>
    Unknown,
    /// <summary>The declared exploration was not completed.</summary>
    Incomplete
}

/// <summary>Conclusion for one exact claim, target and obligation.</summary>
public enum ProofState
{
    /// <summary>Neither established nor refuted.</summary>
    Unknown,
    /// <summary>Established over the full declared scope.</summary>
    Proven,
    /// <summary>Established only under explicitly retained residual bounds or assumptions.</summary>
    Conditional,
    /// <summary>Refuted for this exact target and obligation.</summary>
    Disproven
}

/// <summary>Semantic strength of an evidence method; independent of the tool name.</summary>
public enum EvidenceKind
{
    /// <summary>Observed finite behavior; success cannot prove universal correctness.</summary>
    TraceObservation,
    /// <summary>Complete exploration of an explicitly encoded domain.</summary>
    ExhaustiveCheck,
    /// <summary>A verifier reports a theorem or refutation for an encoded obligation.</summary>
    FormalProof,
    /// <summary>An attributable declaration, never independently checked behavior.</summary>
    ProviderDeclaration
}

/// <summary>Distinct obligations that cannot discharge one another implicitly.</summary>
public enum ProofObligation
{
    /// <summary>The modeled behavior satisfies the claim.</summary>
    Model,
    /// <summary>The verification encoding preserves the source semantics.</summary>
    Encoding,
    /// <summary>The concrete realization satisfies the claim.</summary>
    Realization
}

/// <summary>The kind of object whose behavior is being assessed.</summary>
public enum EvidenceTargetKind
{
    /// <summary>A semantic or reference model.</summary>
    Model,
    /// <summary>An exact implementation candidate.</summary>
    Implementation,
    /// <summary>An exact deployed realization.</summary>
    Deployment
}

/// <summary>Scope of a completed method, not a percentage of universal correctness.</summary>
public enum EvidenceCoverage
{
    /// <summary>Only selected observations or explored cases.</summary>
    Sampled,
    /// <summary>All of the explicitly declared claim scope.</summary>
    FullScope,
    /// <summary>A complete narrower domain with named residuals.</summary>
    RestrictedScope
}

/// <summary>Support reported by a versioned interpreter for this exact claim.</summary>
public enum InterpretationSupport
{
    /// <summary>The interpreter declares that the claim is supported.</summary>
    Supported,
    /// <summary>Required semantics cannot be interpreted.</summary>
    Unsupported,
    /// <summary>Support has not been established.</summary>
    Undetermined
}

/// <summary>Authorized treatment of a requirement, independent of proof state.</summary>
public enum ClaimDispositionKind
{
    /// <summary>No exception or requirement change.</summary>
    None,
    /// <summary>Accepted risk with explicit authority, reason and expiry.</summary>
    Waived,
    /// <summary>An authorized semantic revision, retaining the prior claim.</summary>
    RequirementRevised
}

/// <summary>Exact assessment boundary; changing any component requires a new assessment.</summary>
/// <param name="Kind">Model, implementation or deployed system.</param>
/// <param name="Target">Exact target revision.</param>
/// <param name="Configuration">Exact effective realization configuration.</param>
/// <param name="Scope">Exact declared claim scope, not merely a solver bound.</param>
/// <param name="Obligation">Model, encoding or realization obligation.</param>
public sealed record EvidenceSubject(EvidenceTargetKind Kind, ClaimReference Target,
    ClaimReference Configuration, ClaimReference Scope, ProofObligation Obligation);

/// <summary>Authority-recorded disposition; never alters a proof verdict.</summary>
/// <param name="Kind">Treatment of the claim.</param>
/// <param name="Authority">Exact authority/acceptance artifact; required for nonempty dispositions.</param>
/// <param name="Reason">Human-reviewable reason; required for nonempty dispositions.</param>
/// <param name="ExpiresAt">Required waiver expiry, assessed against an explicit clock value.</param>
/// <param name="Revision">Accepted semantic diff/revision artifact for requirement changes.</param>
public sealed record ClaimDisposition(ClaimDispositionKind Kind, ClaimReference? Authority = null,
    string? Reason = null, DateTimeOffset? ExpiresAt = null, ClaimReference? Revision = null);
