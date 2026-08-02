using System.Collections.Immutable;

namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Identifies one bounded subject slot in the Motion DQ onboarding case.</summary>
public enum MotionDqSubjectKind
{
    /// <summary>The individual applying for service.</summary>
    Applicant,

    /// <summary>The invited or owner-operator driver.</summary>
    Driver,

    /// <summary>The carrier or owner-operator authority.</summary>
    CarrierOwnerOperator,

    /// <summary>The truck authority.</summary>
    Truck,

    /// <summary>The trailer authority.</summary>
    Trailer
}

/// <summary>Identifies the authoritative macro milestone of one onboarding case.</summary>
public enum MotionDqCaseMilestone
{
    /// <summary>No profile has been resolved for the case.</summary>
    Uninitialized,

    /// <summary>The case has pinned its resolved profile and requirement graph.</summary>
    ProfileResolved,

    /// <summary>Prequalification has been accepted.</summary>
    PrequalificationSubmitted,

    /// <summary>The full application has been accepted and awaits caseworker review.</summary>
    FullApplicationSubmitted,

    /// <summary>The caseworker placed the application on hold.</summary>
    Held,

    /// <summary>The application was admitted and insurance terms may be fulfilled.</summary>
    InsuranceTerms,

    /// <summary>Insurance terms were accepted and post-terms requirements may run in parallel.</summary>
    PostTerms,

    /// <summary>All required evidence is settled and subjects may be activated.</summary>
    Activation,

    /// <summary>Every required subject activation completed.</summary>
    Completed,

    /// <summary>The application was determined not eligible.</summary>
    NotEligible,

    /// <summary>The onboarding case was cancelled.</summary>
    Cancelled
}

/// <summary>Identifies the semantic caseworker decision independently of its delivery channel.</summary>
public enum MotionDqReviewDecisionKind
{
    /// <summary>Admit the application to insurance-terms fulfillment.</summary>
    Hire,

    /// <summary>Pause onboarding until a later, independently identified review decision.</summary>
    Hold,

    /// <summary>Conclude that the application is not eligible.</summary>
    NotEligible
}

/// <summary>Identifies the authoritative state of one independently owned case requirement.</summary>
public enum MotionDqRequirementStatus
{
    /// <summary>No authoritative evaluation has settled the requirement.</summary>
    Pending,

    /// <summary>An authoritative evaluation satisfied the requirement.</summary>
    Satisfied,

    /// <summary>An authoritative evaluation concluded that the requirement is not satisfied.</summary>
    Unsatisfied
}

/// <summary>Identifies whether one independently owned subject authority has been activated.</summary>
public enum MotionDqActivationStatus
{
    /// <summary>The subject is not active.</summary>
    Pending,

    /// <summary>The subject was activated by an admitted gate decision.</summary>
    Active
}

/// <summary>
/// Identifies the finite business result of a Motion DQ entity Transition.
/// </summary>
public enum MotionDqTransitionOutcome
{
    /// <summary>The case pinned its resolved onboarding profile.</summary>
    ProfileResolved,

    /// <summary>The case accepted its prequalification submission.</summary>
    PrequalificationSubmitted,

    /// <summary>The case accepted its full application.</summary>
    FullApplicationSubmitted,

    /// <summary>The case recorded the supplied review decision.</summary>
    ReviewDecisionRecorded,

    /// <summary>The review decision could not be classified in the supported finite decision set.</summary>
    ReviewDecisionUnrecognized,

    /// <summary>The case recorded cancellation.</summary>
    Cancelled,

    /// <summary>The case advanced across one supported, gate-admitted macro-milestone edge.</summary>
    MilestoneAdvanced,

    /// <summary>The requirement accepted the evaluation as authoritative.</summary>
    RequirementEvaluationAccepted,

    /// <summary>The requirement had already observed the same evaluation identity.</summary>
    RequirementEvaluationDuplicate,

    /// <summary>The requirement retained a later authoritative settlement and recorded this evaluation as superseded.</summary>
    RequirementEvaluationSuperseded,

    /// <summary>The evaluation disposition could not be classified in the supported finite result set.</summary>
    RequirementEvaluationUnrecognized,

    /// <summary>An observed evaluation identity was reused for different semantic evidence.</summary>
    RequirementEvaluationIdentityConflict,

    /// <summary>The subject authority admitted activation.</summary>
    SubjectActivated,

    /// <summary>The case is not at the milestone required by the Transition.</summary>
    InvalidMilestone,

    /// <summary>The submitted profile identity or revision does not match the case-pinned profile.</summary>
    ProfileMismatch,

    /// <summary>The supplied case reference does not match the receiving case authority.</summary>
    CaseReferenceMismatch,

    /// <summary>The supplied application reference does not match the case-pinned application.</summary>
    ApplicationReferenceMismatch,

    /// <summary>The submission did not satisfy its declared entry requirements.</summary>
    RequirementsUnsatisfied,

    /// <summary>The evaluation targets a different independently owned requirement.</summary>
    RequirementMismatch,

    /// <summary>The requested milestone edge is not one of the finite onboarding edges.</summary>
    UnsupportedMilestoneEdge,

    /// <summary>The supplied gate identity does not authorize the requested milestone edge.</summary>
    MilestoneGateMismatch,

    /// <summary>The supplied durable decision identity is empty.</summary>
    DecisionIdentityRequired,

    /// <summary>The onboarding case identity to be made authoritative is empty.</summary>
    CaseIdentityRequired,

    /// <summary>The schema identity to be pinned by the onboarding case is empty.</summary>
    SchemaIdentityRequired,

    /// <summary>The profile identity to be pinned by the onboarding case is empty.</summary>
    ProfileIdentityRequired,

    /// <summary>The profile revision to be pinned by the onboarding case is empty.</summary>
    ProfileRevisionRequired,

    /// <summary>The application identity to be made authoritative is empty.</summary>
    ApplicationIdentityRequired,

    /// <summary>The supplied durable cancellation identity is empty.</summary>
    CancellationIdentityRequired,

    /// <summary>The supplied durable requirement-evaluation identity is empty.</summary>
    EvaluationIdentityRequired,

    /// <summary>The supplied durable evidence reference is empty.</summary>
    EvidenceIdentityRequired,

    /// <summary>The requested subject kind does not match the subject authority.</summary>
    SubjectKindMismatch,

    /// <summary>The supplied activation gate does not match the subject authority.</summary>
    ActivationGateMismatch,

    /// <summary>The supplied gate decision did not satisfy the requested case milestone edge.</summary>
    MilestoneGateUnsatisfied,

    /// <summary>The activation gate has not admitted the subject.</summary>
    ActivationGateUnsatisfied,

    /// <summary>A driver activation lacks the carrier activation decision on which it depends.</summary>
    DriverCarrierGateRequired,

    /// <summary>A non-driver activation supplied carrier evidence that is meaningful only for a dependent driver.</summary>
    UnexpectedParentCarrierProof,

    /// <summary>The subject authority was already activated.</summary>
    AlreadyActive,

    /// <summary>The case is already terminal and cannot be cancelled again.</summary>
    AlreadyTerminal
}

/// <summary>References one resolved onboarding block by semantic identity.</summary>
/// <param name="Id">Stable block identity within the profile revision.</param>
public sealed record MotionDqBlockReference(string Id);

/// <summary>References one independently owned requirement by semantic identity.</summary>
/// <param name="Id">Stable requirement identity within the profile revision.</param>
public sealed record MotionDqRequirementReference(string Id);

/// <summary>References one requirement authority within exactly one onboarding case.</summary>
public sealed record MotionDqCaseRequirementReference
{
    /// <summary>Creates one case-scoped requirement reference.</summary>
    /// <param name="caseId">Stable onboarding-case identifier.</param>
    /// <param name="requirementId">Stable profile-resolved requirement identifier.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="caseId"/> or <paramref name="requirementId"/> is null, empty, or whitespace.
    /// </exception>
    public MotionDqCaseRequirementReference(string caseId, string requirementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirementId);
        CaseId = caseId;
        RequirementId = requirementId;
    }

    /// <summary>Stable onboarding-case identifier.</summary>
    public string CaseId { get; }

    /// <summary>Stable profile-resolved requirement identifier.</summary>
    public string RequirementId { get; }
}

/// <summary>References one evidence need by semantic identity.</summary>
/// <param name="Id">Stable evidence-need identity within the profile revision.</param>
public sealed record MotionDqEvidenceNeedReference(string Id);

/// <summary>References one explicit activation or progress gate by semantic identity.</summary>
/// <param name="Id">Stable gate identity within the profile revision.</param>
public sealed record MotionDqGateReference(string Id);

/// <summary>References one bounded subject authority without copying its business state into Process state.</summary>
/// <param name="ApplicationId">Application that owns the subject slot.</param>
/// <param name="Kind">Kind of authority referenced by the slot.</param>
/// <param name="SubjectId">Stable identifier of the independently transitioned subject.</param>
/// <param name="ParentApplicationId">Optional parent application for invited-driver coordination.</param>
public sealed record MotionDqSubjectReference(
    string ApplicationId,
    MotionDqSubjectKind Kind,
    string SubjectId,
    string? ParentApplicationId);

/// <summary>
/// Attributable evidence that one exact carrier authority admitted one exact activation decision.
/// </summary>
public sealed record MotionDqCarrierActivationProof
{
    /// <summary>Creates a carrier activation proof suitable for an exact dependent-driver gate.</summary>
    /// <param name="carrierSubject">Carrier or owner-operator authority that admitted activation.</param>
    /// <param name="activationDecisionId">Stable identity of the admitted carrier activation decision.</param>
    /// <param name="evidenceId">Stable reference to durable activation evidence.</param>
    /// <exception cref="ArgumentException">
    /// The subject is not a carrier or owner-operator, or an identity is null, empty, or whitespace.
    /// </exception>
    public MotionDqCarrierActivationProof(
        MotionDqSubjectReference carrierSubject,
        string activationDecisionId,
        string evidenceId)
    {
        ArgumentNullException.ThrowIfNull(carrierSubject);
        if (carrierSubject.Kind != MotionDqSubjectKind.CarrierOwnerOperator)
        {
            throw new ArgumentException(
                "A dependent-driver activation proof must reference a carrier or owner-operator subject.",
                nameof(carrierSubject));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(activationDecisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        CarrierSubject = carrierSubject;
        ActivationDecisionId = activationDecisionId;
        EvidenceId = evidenceId;
    }

    /// <summary>Carrier or owner-operator authority that admitted activation.</summary>
    public MotionDqSubjectReference CarrierSubject { get; }

    /// <summary>Stable identity of the admitted carrier activation decision.</summary>
    public string ActivationDecisionId { get; }

    /// <summary>Stable reference to durable carrier activation evidence.</summary>
    public string EvidenceId { get; }
}

/// <summary>Declares one bounded subject slot and its activation dependency in a resolved profile.</summary>
/// <param name="Kind">Kind of authority populated by the slot.</param>
/// <param name="ActivationGate">Gate that must admit activation of this subject.</param>
/// <param name="DependsOnSubject">Optional subject whose admitted activation decision is required first.</param>
public sealed record MotionDqSubjectSlot(
    MotionDqSubjectKind Kind,
    MotionDqGateReference ActivationGate,
    MotionDqSubjectKind? DependsOnSubject);

/// <summary>
/// Immutable, versioned resolution of onboarding blocks, requirements, evidence needs, gates, and bounded subjects.
/// </summary>
/// <param name="SchemaId">Versioned schema identity used to interpret the profile.</param>
/// <param name="ProfileId">Stable profile identity.</param>
/// <param name="Revision">Pinned semantic revision of the profile.</param>
/// <param name="Blocks">Resolved ordered block references.</param>
/// <param name="Requirements">Resolved independently owned requirement references.</param>
/// <param name="EvidenceNeeds">Resolved evidence-need references.</param>
/// <param name="Gates">Resolved progress and activation gate references.</param>
/// <param name="SubjectSlots">Bounded subject slots populated by this profile.</param>
public sealed record MotionDqResolvedProfile(
    string SchemaId,
    string ProfileId,
    string Revision,
    ImmutableArray<MotionDqBlockReference> Blocks,
    ImmutableArray<MotionDqRequirementReference> Requirements,
    ImmutableArray<MotionDqEvidenceNeedReference> EvidenceNeeds,
    ImmutableArray<MotionDqGateReference> Gates,
    ImmutableArray<MotionDqSubjectSlot> SubjectSlots);

/// <summary>Canonical Motion DQ onboarding profile fixtures used by execution-kernel conformance scenarios.</summary>
public static class MotionDqProfileCatalog
{
    /// <summary>The insurance-terms requirement that precedes the parallel post-terms fan-out.</summary>
    public static readonly MotionDqRequirementReference InsuranceTermsRequirement =
        new(MotionDqVocabulary.Requirements.InsuranceTerms);

    /// <summary>The drug-test requirement.</summary>
    public static readonly MotionDqRequirementReference DrugTestRequirement =
        new(MotionDqVocabulary.Requirements.DrugTest);

    /// <summary>The Clearinghouse requirement.</summary>
    public static readonly MotionDqRequirementReference ClearinghouseRequirement =
        new(MotionDqVocabulary.Requirements.Clearinghouse);

    /// <summary>The vehicle qualification requirement.</summary>
    public static readonly MotionDqRequirementReference VehicleRequirement =
        new(MotionDqVocabulary.Requirements.Vehicle);

    /// <summary>The business qualification requirement.</summary>
    public static readonly MotionDqRequirementReference BusinessRequirement =
        new(MotionDqVocabulary.Requirements.Business);

    /// <summary>The equipment qualification requirement.</summary>
    public static readonly MotionDqRequirementReference EquipmentRequirement =
        new(MotionDqVocabulary.Requirements.Equipment);

    /// <summary>The permit requirement.</summary>
    public static readonly MotionDqRequirementReference PermitRequirement =
        new(MotionDqVocabulary.Requirements.Permit);

    /// <summary>The random-pool enrollment requirement.</summary>
    public static readonly MotionDqRequirementReference RandomPoolRequirement =
        new(MotionDqVocabulary.Requirements.RandomPool);

    /// <summary>The carrier activation gate which must precede dependent-driver activation.</summary>
    public static readonly MotionDqGateReference CarrierActivationGate =
        new(MotionDqVocabulary.Gates.CarrierActivation);

    /// <summary>The dependent-driver activation gate.</summary>
    public static readonly MotionDqGateReference DriverActivationGate =
        new(MotionDqVocabulary.Gates.DriverActivation);

    /// <summary>The gate proving acceptance of the exact insurance-terms revision.</summary>
    public static readonly MotionDqGateReference InsuranceTermsAcceptedGate =
        new(MotionDqVocabulary.Gates.InsuranceTermsAccepted);

    /// <summary>The gate proving convergence of every post-terms requirement branch.</summary>
    public static readonly MotionDqGateReference PostTermsCompleteGate =
        new(MotionDqVocabulary.Gates.PostTermsComplete);

    /// <summary>The gate proving completion of every independently admitted subject activation.</summary>
    public static readonly MotionDqGateReference ActivationCompleteGate =
        new(MotionDqVocabulary.Gates.ActivationComplete);

    /// <summary>The version-one profile used by the Motion DQ onboarding Process fixture.</summary>
    public static readonly MotionDqResolvedProfile Version1 = new(
        SchemaId: MotionDqVocabulary.SchemaId,
        ProfileId: MotionDqVocabulary.ProfileId,
        Revision: MotionDqVocabulary.ProfileRevision,
        Blocks:
        [
            new(MotionDqVocabulary.Blocks.Prequalification),
            new(MotionDqVocabulary.Blocks.FullApplication),
            new(MotionDqVocabulary.Blocks.CaseworkerReview),
            new(MotionDqVocabulary.Blocks.InsuranceTerms),
            new(MotionDqVocabulary.Blocks.DrugTest),
            new(MotionDqVocabulary.Blocks.Clearinghouse),
            new(MotionDqVocabulary.Blocks.Vehicle),
            new(MotionDqVocabulary.Blocks.Business),
            new(MotionDqVocabulary.Blocks.Equipment),
            new(MotionDqVocabulary.Blocks.Permit),
            new(MotionDqVocabulary.Blocks.RandomPool),
            new(MotionDqVocabulary.Blocks.Activation)
        ],
        Requirements:
        [
            InsuranceTermsRequirement,
            DrugTestRequirement,
            ClearinghouseRequirement,
            VehicleRequirement,
            BusinessRequirement,
            EquipmentRequirement,
            PermitRequirement,
            RandomPoolRequirement
        ],
        EvidenceNeeds:
        [
            new(MotionDqVocabulary.EvidenceNeeds.InsuranceTerms),
            new(MotionDqVocabulary.EvidenceNeeds.DrugTest),
            new(MotionDqVocabulary.EvidenceNeeds.Clearinghouse),
            new(MotionDqVocabulary.EvidenceNeeds.Vehicle),
            new(MotionDqVocabulary.EvidenceNeeds.Business),
            new(MotionDqVocabulary.EvidenceNeeds.Equipment),
            new(MotionDqVocabulary.EvidenceNeeds.Permit),
            new(MotionDqVocabulary.EvidenceNeeds.RandomPool)
        ],
        Gates:
        [
            new(MotionDqVocabulary.Gates.ReviewAdmission),
            InsuranceTermsAcceptedGate,
            PostTermsCompleteGate,
            ActivationCompleteGate,
            new(MotionDqVocabulary.Gates.ApplicantActivation),
            DriverActivationGate,
            CarrierActivationGate,
            new(MotionDqVocabulary.Gates.TruckActivation),
            new(MotionDqVocabulary.Gates.TrailerActivation)
        ],
        SubjectSlots:
        [
            new(MotionDqSubjectKind.Applicant, new(MotionDqVocabulary.Gates.ApplicantActivation), DependsOnSubject: null),
            new(MotionDqSubjectKind.Driver, DriverActivationGate, MotionDqSubjectKind.CarrierOwnerOperator),
            new(MotionDqSubjectKind.CarrierOwnerOperator, CarrierActivationGate, DependsOnSubject: null),
            new(MotionDqSubjectKind.Truck, new(MotionDqVocabulary.Gates.TruckActivation), DependsOnSubject: null),
            new(MotionDqSubjectKind.Trailer, new(MotionDqVocabulary.Gates.TrailerActivation), DependsOnSubject: null)
        ]);

    /// <summary>Creates the complete immutable profile-resolution input for a new onboarding case.</summary>
    /// <param name="caseId">Stable identifier of the case that will pin <see cref="Version1"/>.</param>
    /// <returns>A resolution containing the exact blocks, requirements, evidence needs, gates, and subject slots in version one.</returns>
    /// <exception cref="ArgumentException"><paramref name="caseId"/> is null, empty, or whitespace.</exception>
    public static MotionDqCaseProfileResolution CreateCaseProfileResolution(string caseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        return new(CaseId: caseId, Profile: Version1);
    }

    /// <summary>Scopes one profile requirement to an exact onboarding case.</summary>
    /// <param name="caseId">Stable onboarding-case identifier.</param>
    /// <param name="requirement">Profile-resolved requirement to scope.</param>
    /// <returns>A single canonical subject/reference value suitable for Process and Transition invocation.</returns>
    /// <exception cref="ArgumentException"><paramref name="caseId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="requirement"/> is <see langword="null"/>.</exception>
    public static MotionDqCaseRequirementReference ScopeRequirement(
        string caseId,
        MotionDqRequirementReference requirement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(requirement);
        return new(caseId: caseId, requirementId: requirement.Id);
    }
}

static class MotionDqVocabulary
{
    public const string SchemaId = "motion-dq/onboarding-case-schema/v1";
    public const string ProfileId = "motion-dq/onboarding/v1";
    public const string ProfileRevision = "revision/1";

    public static class Blocks
    {
        public const string Prequalification = "block/prequalification";
        public const string FullApplication = "block/full-application";
        public const string CaseworkerReview = "block/caseworker-review";
        public const string InsuranceTerms = "block/insurance-terms";
        public const string DrugTest = "block/drug-test";
        public const string Clearinghouse = "block/clearinghouse";
        public const string Vehicle = "block/vehicle";
        public const string Business = "block/business";
        public const string Equipment = "block/equipment";
        public const string Permit = "block/permit";
        public const string RandomPool = "block/random-pool";
        public const string Activation = "block/activation";
    }

    public static class Requirements
    {
        public const string InsuranceTerms = "requirement/insurance-terms";
        public const string DrugTest = "requirement/drug-test";
        public const string Clearinghouse = "requirement/clearinghouse";
        public const string Vehicle = "requirement/vehicle";
        public const string Business = "requirement/business";
        public const string Equipment = "requirement/equipment";
        public const string Permit = "requirement/permit";
        public const string RandomPool = "requirement/random-pool";
    }

    public static class EvidenceNeeds
    {
        public const string InsuranceTerms = "evidence/insurance-terms";
        public const string DrugTest = "evidence/drug-test";
        public const string Clearinghouse = "evidence/clearinghouse";
        public const string Vehicle = "evidence/vehicle";
        public const string Business = "evidence/business";
        public const string Equipment = "evidence/equipment";
        public const string Permit = "evidence/permit";
        public const string RandomPool = "evidence/random-pool";
    }

    public static class Gates
    {
        public const string ReviewAdmission = "gate/review-admission";
        public const string InsuranceTermsAccepted = "gate/insurance-terms-accepted";
        public const string PostTermsComplete = "gate/post-terms-complete";
        public const string ActivationComplete = "gate/activation-complete";
        public const string ApplicantActivation = "gate/activation/applicant";
        public const string DriverActivation = "gate/activation/driver";
        public const string CarrierActivation = "gate/activation/carrier-owner-operator";
        public const string TruckActivation = "gate/activation/truck";
        public const string TrailerActivation = "gate/activation/trailer";
    }
}
