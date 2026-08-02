namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Identifies the finite result of evaluating a required gate.</summary>
public enum MotionDqGateDisposition
{
    /// <summary>The gate's declared condition is satisfied.</summary>
    Satisfied,

    /// <summary>The gate's declared condition is not satisfied.</summary>
    Unsatisfied
}

/// <summary>Supplies the versioned profile resolution pinned by a new onboarding case.</summary>
/// <param name="CaseId">Stable onboarding-case identifier.</param>
/// <param name="Profile">Single canonical resolved profile pinned by the case.</param>
public sealed record MotionDqCaseProfileResolution(
    string CaseId,
    MotionDqResolvedProfile Profile);

/// <summary>Supplies one prequalification submission against the case-pinned profile.</summary>
/// <param name="CaseId">Case receiving the submission.</param>
/// <param name="ApplicationId">Application introduced by the submission.</param>
/// <param name="ProfileId">Profile identity used to collect the submission.</param>
/// <param name="ProfileRevision">Profile revision used to collect the submission.</param>
/// <param name="RequirementGate">Result of the prequalification entry gate.</param>
public sealed record MotionDqPrequalificationSubmission(
    string CaseId,
    string ApplicationId,
    string ProfileId,
    string ProfileRevision,
    MotionDqGateDisposition RequirementGate);

/// <summary>Supplies the full application after prequalification.</summary>
/// <param name="CaseId">Case receiving the submission.</param>
/// <param name="ApplicationId">Application receiving the full submission.</param>
/// <param name="RequirementGate">Result of the full-application completeness gate.</param>
public sealed record MotionDqFullApplicationSubmission(
    string CaseId,
    string ApplicationId,
    MotionDqGateDisposition RequirementGate);

/// <summary>Supplies one independently identified caseworker decision.</summary>
/// <param name="DecisionId">Stable decision identity used for idempotency and downstream gates.</param>
/// <param name="CaseId">Case reviewed by the caseworker.</param>
/// <param name="ApplicationId">Application reviewed by the caseworker.</param>
/// <param name="Kind">Finite semantic decision.</param>
/// <param name="ReasonCode">Inspectable business reason code.</param>
public sealed record MotionDqReviewDecision(
    string DecisionId,
    string CaseId,
    string ApplicationId,
    MotionDqReviewDecisionKind Kind,
    string ReasonCode);

/// <summary>Supplies one independently identified case cancellation.</summary>
/// <param name="CancellationId">Stable cancellation identity.</param>
/// <param name="CaseId">Case being cancelled.</param>
/// <param name="ReasonCode">Inspectable cancellation reason.</param>
public sealed record MotionDqCancellation(
    string CancellationId,
    string CaseId,
    string ReasonCode);

/// <summary>Supplies one exact, gate-admitted case macro-milestone edge.</summary>
/// <param name="DecisionId">Stable identity of the gate decision authorizing this movement.</param>
/// <param name="CaseId">Case whose macro milestone may advance.</param>
/// <param name="ExpectedMilestone">Milestone that must currently be authoritative.</param>
/// <param name="NextMilestone">Requested destination milestone.</param>
/// <param name="GateId">Exact profile gate authorizing the requested edge.</param>
/// <param name="GateDisposition">Result of evaluating the gate.</param>
public sealed record MotionDqCaseMilestoneAdmission(
    string DecisionId,
    string CaseId,
    MotionDqCaseMilestone ExpectedMilestone,
    MotionDqCaseMilestone NextMilestone,
    string GateId,
    MotionDqGateDisposition GateDisposition);

/// <summary>Requests creation of the human caseworker review task.</summary>
/// <param name="CaseId">Case requiring review.</param>
/// <param name="ApplicationId">Application requiring review.</param>
public sealed record MotionDqReviewTaskRequest(string CaseId, string ApplicationId);

/// <summary>References the task owned by the external task module.</summary>
/// <param name="TaskId">Stable task identity; task lifecycle is not copied into Process state.</param>
public sealed record MotionDqReviewTaskReference(string TaskId);

/// <summary>
/// Provider-neutral request to fulfill one evidence-backed requirement; vendor and manual routes use this exact payload.
/// </summary>
/// <param name="Requirement">Exact case-scoped requirement authority being fulfilled.</param>
/// <param name="EvidenceNeedId">Evidence need the selected adapter must fulfill.</param>
public sealed record MotionDqRequirementFulfillmentRequest(
    MotionDqCaseRequirementReference Requirement,
    string EvidenceNeedId);

/// <summary>Records one endogenous evaluation of externally obtained requirement evidence.</summary>
/// <param name="EvaluationId">Stable identity used to classify duplicate and superseded deliveries.</param>
/// <param name="Requirement">Exact case-scoped requirement authority targeted by the evaluation.</param>
/// <param name="Disposition">Semantic evaluation of the supplied evidence.</param>
/// <param name="EvidenceId">Reference to evidence retained by its owning module or provider adapter.</param>
public sealed record MotionDqRequirementEvaluationReceipt(
    string EvaluationId,
    MotionDqCaseRequirementReference Requirement,
    MotionDqGateDisposition Disposition,
    string EvidenceId);

/// <summary>Supplies one explicit gate decision to an independently owned subject authority.</summary>
/// <param name="DecisionId">Stable activation decision identity.</param>
/// <param name="Kind">Kind of subject authority receiving the decision.</param>
/// <param name="GateId">Profile-resolved activation gate being applied.</param>
/// <param name="GateDisposition">Result of evaluating the activation gate.</param>
/// <param name="ParentCarrierProof">
/// Exact expected carrier subject, decision, and durable evidence for a dependent driver; otherwise null.
/// </param>
public sealed record MotionDqSubjectActivationAdmission(
    string DecisionId,
    MotionDqSubjectKind Kind,
    string GateId,
    MotionDqGateDisposition GateDisposition,
    MotionDqCarrierActivationProof? ParentCarrierProof);
