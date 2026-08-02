using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;

namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Authoritative onboarding-case observation shape used by the Motion DQ Transition fixtures.</summary>
public sealed class MotionDqOnboardingCaseEntity : Entity<MotionDqOnboardingCaseEntity>
{
    /// <summary>Declares the versioned case, profile-resolution, application, and macro-milestone fields.</summary>
    public MotionDqOnboardingCaseEntity()
    {
        CaseId = WriteOnceField<string>(nameof(CaseId));
        SchemaId = WriteOnceField<string>(nameof(SchemaId));
        ProfileId = WriteOnceField<string>(nameof(ProfileId));
        ProfileRevision = WriteOnceField<string>(nameof(ProfileRevision));
        ApplicationId = WriteOnceField<string>(nameof(ApplicationId));
        ResolvedBlocks = WriteOnceField<IReadOnlyList<MotionDqBlockReference>>(nameof(ResolvedBlocks));
        ResolvedRequirements = WriteOnceField<IReadOnlyList<MotionDqRequirementReference>>(nameof(ResolvedRequirements));
        ResolvedEvidenceNeeds = WriteOnceField<IReadOnlyList<MotionDqEvidenceNeedReference>>(nameof(ResolvedEvidenceNeeds));
        ResolvedGates = WriteOnceField<IReadOnlyList<MotionDqGateReference>>(nameof(ResolvedGates));
        ResolvedSubjectSlots = WriteOnceField<IReadOnlyList<MotionDqSubjectSlot>>(nameof(ResolvedSubjectSlots));
        Milestone = Field(nameof(Milestone), MotionDqCaseMilestone.Uninitialized);
        LastReviewDecisionId = Field(nameof(LastReviewDecisionId), initialValue: "");
        LastMilestoneDecisionId = Field(nameof(LastMilestoneDecisionId), initialValue: "");
        CancellationId = Field(nameof(CancellationId), initialValue: "");
    }

    /// <summary>Stable identity of the onboarding case.</summary>
    public Field<string> CaseId { get; }

    /// <summary>Versioned schema identity pinned when the case is resolved.</summary>
    public Field<string> SchemaId { get; }

    /// <summary>Stable onboarding-profile identity pinned by the case.</summary>
    public Field<string> ProfileId { get; }

    /// <summary>Exact semantic profile revision pinned by the case.</summary>
    public Field<string> ProfileRevision { get; }

    /// <summary>Application identity attached by the accepted prequalification submission.</summary>
    public Field<string> ApplicationId { get; }

    /// <summary>Resolved ordered onboarding blocks.</summary>
    public Field<IReadOnlyList<MotionDqBlockReference>> ResolvedBlocks { get; }

    /// <summary>Resolved independently owned requirement references.</summary>
    public Field<IReadOnlyList<MotionDqRequirementReference>> ResolvedRequirements { get; }

    /// <summary>Resolved evidence-need references.</summary>
    public Field<IReadOnlyList<MotionDqEvidenceNeedReference>> ResolvedEvidenceNeeds { get; }

    /// <summary>Resolved progress and activation gates.</summary>
    public Field<IReadOnlyList<MotionDqGateReference>> ResolvedGates { get; }

    /// <summary>Resolved bounded subject slots.</summary>
    public Field<IReadOnlyList<MotionDqSubjectSlot>> ResolvedSubjectSlots { get; }

    /// <summary>Authoritative macro milestone; detailed requirement and subject state remains in separate entities.</summary>
    public Field<MotionDqCaseMilestone> Milestone { get; }

    /// <summary>Most recently admitted caseworker decision identity.</summary>
    public Field<string> LastReviewDecisionId { get; }

    /// <summary>Most recently admitted macro-milestone gate decision identity.</summary>
    public Field<string> LastMilestoneDecisionId { get; }

    /// <summary>Admitted cancellation identity, or the empty string before cancellation.</summary>
    public Field<string> CancellationId { get; }
}

/// <summary>Authoritative observation shape for one independently transitioned case requirement.</summary>
public sealed class MotionDqCaseRequirementEntity : Entity<MotionDqCaseRequirementEntity>
{
    /// <summary>Declares requirement identity, settlement, and append-only evaluation evidence.</summary>
    public MotionDqCaseRequirementEntity()
    {
        CaseId = WriteOnceField<string>(nameof(CaseId));
        RequirementId = WriteOnceField<string>(nameof(RequirementId));
        Status = Field(nameof(Status), MotionDqRequirementStatus.Pending);
        AuthoritativeEvaluationId = Field(nameof(AuthoritativeEvaluationId), initialValue: "");
        ObservedEvaluationIds = Field<IReadOnlyList<string>>(nameof(ObservedEvaluationIds), initialValue: []);
        Evaluations = Field<IReadOnlyList<MotionDqRequirementEvaluationReceipt>>(nameof(Evaluations), initialValue: []);
    }

    /// <summary>Stable onboarding case that owns this requirement authority.</summary>
    public Field<string> CaseId { get; }

    /// <summary>Stable profile-resolved requirement identity within <see cref="CaseId"/>.</summary>
    public Field<string> RequirementId { get; }

    /// <summary>Authoritative settlement state.</summary>
    public Field<MotionDqRequirementStatus> Status { get; }

    /// <summary>Identity of the evaluation that authoritatively settled the requirement.</summary>
    public Field<string> AuthoritativeEvaluationId { get; }

    /// <summary>
    /// Complete idempotency index of observed evaluation identities, updated atomically with <see cref="Evaluations"/>.
    /// </summary>
    public Field<IReadOnlyList<string>> ObservedEvaluationIds { get; }

    /// <summary>Append-only endogenous evidence-evaluation history.</summary>
    public Field<IReadOnlyList<MotionDqRequirementEvaluationReceipt>> Evaluations { get; }
}

/// <summary>Authoritative observation shape for one independently transitioned subject activation.</summary>
public sealed class MotionDqSubjectActivationEntity : Entity<MotionDqSubjectActivationEntity>
{
    /// <summary>Declares subject kind, profile-resolved gate, and admitted activation decision.</summary>
    public MotionDqSubjectActivationEntity()
    {
        Kind = WriteOnceField<MotionDqSubjectKind>(nameof(Kind));
        ActivationGateId = WriteOnceField<string>(nameof(ActivationGateId));
        Status = Field(nameof(Status), MotionDqActivationStatus.Pending);
        LastActivationDecisionId = Field(nameof(LastActivationDecisionId), initialValue: "");
        RequiredParentCarrierProof = Field<MotionDqCarrierActivationProof?>(nameof(RequiredParentCarrierProof), initialValue: null);
        AdmittedParentCarrierProof = Field<MotionDqCarrierActivationProof?>(nameof(AdmittedParentCarrierProof), initialValue: null);
    }

    /// <summary>Kind of independently owned subject authority.</summary>
    public Field<MotionDqSubjectKind> Kind { get; }

    /// <summary>Profile-resolved activation gate owned by the subject authority.</summary>
    public Field<string> ActivationGateId { get; }

    /// <summary>Authoritative activation state.</summary>
    public Field<MotionDqActivationStatus> Status { get; }

    /// <summary>Identity of the admitted activation decision, or the empty string before activation.</summary>
    public Field<string> LastActivationDecisionId { get; }

    /// <summary>
    /// Exact carrier subject, activation decision, and evidence expected by a dependent driver, otherwise null.
    /// </summary>
    /// <remarks>
    /// The subject authority checks exact proof equality. Process coordination remains responsible for obtaining this
    /// expected value from the authoritative carrier activation occurrence before invoking the driver Transition.
    /// </remarks>
    public Field<MotionDqCarrierActivationProof?> RequiredParentCarrierProof { get; }

    /// <summary>Carrier activation proof admitted by the driver Transition, otherwise null.</summary>
    public Field<MotionDqCarrierActivationProof?> AdmittedParentCarrierProof { get; }
}

/// <summary>Typed handles for every canonical Transition authored by the Motion DQ fixture.</summary>
public sealed class MotionDqTransitionDefinitions
{
    internal MotionDqTransitionDefinitions(
        Transition<MotionDqOnboardingCaseEntity, MotionDqCaseProfileResolution, MotionDqTransitionOutcome> resolveCaseProfile,
        Transition<MotionDqOnboardingCaseEntity, MotionDqPrequalificationSubmission, MotionDqTransitionOutcome> submitPrequalification,
        Transition<MotionDqOnboardingCaseEntity, MotionDqFullApplicationSubmission, MotionDqTransitionOutcome> submitFullApplication,
        Transition<MotionDqOnboardingCaseEntity, MotionDqReviewDecision, MotionDqTransitionOutcome> recordReviewDecision,
        Transition<MotionDqOnboardingCaseEntity, MotionDqCaseMilestoneAdmission, MotionDqTransitionOutcome> advanceCaseMilestone,
        Transition<MotionDqOnboardingCaseEntity, MotionDqCancellation, MotionDqTransitionOutcome> cancelCase,
        Transition<MotionDqCaseRequirementEntity, MotionDqRequirementEvaluationReceipt, MotionDqTransitionOutcome> applyRequirementEvaluation,
        Transition<MotionDqSubjectActivationEntity, MotionDqSubjectActivationAdmission, MotionDqTransitionOutcome> activateSubject)
    {
        ResolveCaseProfile = resolveCaseProfile;
        SubmitPrequalification = submitPrequalification;
        SubmitFullApplication = submitFullApplication;
        RecordReviewDecision = recordReviewDecision;
        AdvanceCaseMilestone = advanceCaseMilestone;
        CancelCase = cancelCase;
        ApplyRequirementEvaluation = applyRequirementEvaluation;
        ActivateSubject = activateSubject;
        Documents =
        [
            ResolveCaseProfile.Document,
            SubmitPrequalification.Document,
            SubmitFullApplication.Document,
            RecordReviewDecision.Document,
            AdvanceCaseMilestone.Document,
            CancelCase.Document,
            ApplyRequirementEvaluation.Document,
            ActivateSubject.Document
        ];
    }

    /// <summary>Transition that pins the resolved profile on a new case.</summary>
    public Transition<MotionDqOnboardingCaseEntity, MotionDqCaseProfileResolution, MotionDqTransitionOutcome> ResolveCaseProfile { get; }

    /// <summary>Transition that accepts a profile-compatible prequalification.</summary>
    public Transition<MotionDqOnboardingCaseEntity, MotionDqPrequalificationSubmission, MotionDqTransitionOutcome> SubmitPrequalification { get; }

    /// <summary>Transition that accepts the full application.</summary>
    public Transition<MotionDqOnboardingCaseEntity, MotionDqFullApplicationSubmission, MotionDqTransitionOutcome> SubmitFullApplication { get; }

    /// <summary>Transition that records Hire, Hold, or Not Eligible as a finite semantic decision.</summary>
    public Transition<MotionDqOnboardingCaseEntity, MotionDqReviewDecision, MotionDqTransitionOutcome> RecordReviewDecision { get; }

    /// <summary>Generic finite Transition for the three supported post-Hire case milestone edges.</summary>
    public Transition<MotionDqOnboardingCaseEntity, MotionDqCaseMilestoneAdmission, MotionDqTransitionOutcome> AdvanceCaseMilestone { get; }

    /// <summary>Transition that cancels a nonterminal case.</summary>
    public Transition<MotionDqOnboardingCaseEntity, MotionDqCancellation, MotionDqTransitionOutcome> CancelCase { get; }

    /// <summary>Generic requirement Transition with explicit accepted, duplicate, and superseded outcomes.</summary>
    public Transition<MotionDqCaseRequirementEntity, MotionDqRequirementEvaluationReceipt, MotionDqTransitionOutcome> ApplyRequirementEvaluation { get; }

    /// <summary>Generic subject activation Transition with explicit per-subject and dependent-driver gates.</summary>
    public Transition<MotionDqSubjectActivationEntity, MotionDqSubjectActivationAdmission, MotionDqTransitionOutcome> ActivateSubject { get; }

    /// <summary>All canonical Transition documents in dependency order.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Documents { get; }
}

/// <summary>Authors the canonical Transition IR used by the Motion DQ execution-kernel fixture.</summary>
public static class MotionDqTransitions
{
    /// <summary>Authors a fresh deterministic set of canonical Transition documents.</summary>
    /// <returns>Typed handles over canonical documents; no CLR callback remains runtime authority.</returns>
    /// <exception cref="TransitionExpressionTranslationException">A fixture expression falls outside the portable Transition subset.</exception>
    /// <exception cref="InvalidOperationException">The authored structured Transition graph is contradictory.</exception>
    public static MotionDqTransitionDefinitions Author() => new(
        AuthorResolveCaseProfile(),
        AuthorSubmitPrequalification(),
        AuthorSubmitFullApplication(),
        AuthorRecordReviewDecision(),
        AuthorAdvanceCaseMilestone(),
        AuthorCancelCase(),
        AuthorApplyRequirementEvaluation(),
        AuthorActivateSubject());

    static Transition<MotionDqOnboardingCaseEntity, MotionDqCaseProfileResolution, MotionDqTransitionOutcome> AuthorResolveCaseProfile() =>
        TransitionAuthoring.Create<MotionDqOnboardingCaseEntity, MotionDqCaseProfileResolution, MotionDqTransitionOutcome>(
            MotionDqOnboardingCaseEntity.Instance.Definition.Shape,
            Metadata(
                Identities.ResolveProfile.Definition,
                Identities.ResolveProfile.Body,
                displayName: "Resolve Motion DQ onboarding profile",
                description: "Pins the complete versioned profile resolution on a new onboarding case."),
            transition => transition
                .Requires(
                    Identities.ResolveProfile.CaseIdentityProvided,
                    (_, input) => input.CaseId != "",
                    (_, _) => MotionDqTransitionOutcome.CaseIdentityRequired)
                .Requires(
                    Identities.ResolveProfile.SchemaIdentityProvided,
                    (_, input) => input.Profile.SchemaId != "",
                    (_, _) => MotionDqTransitionOutcome.SchemaIdentityRequired)
                .Requires(
                    Identities.ResolveProfile.ProfileIdentityProvided,
                    (_, input) => input.Profile.ProfileId != "",
                    (_, _) => MotionDqTransitionOutcome.ProfileIdentityRequired)
                .Requires(
                    Identities.ResolveProfile.ProfileRevisionProvided,
                    (_, input) => input.Profile.Revision != "",
                    (_, _) => MotionDqTransitionOutcome.ProfileRevisionRequired)
                .Requires(
                    Identities.ResolveProfile.InitialCase,
                    (entity, _) => entity.Milestone == MotionDqCaseMilestone.Uninitialized,
                    (_, _) => MotionDqTransitionOutcome.InvalidMilestone)
                .Set(Identities.ResolveProfile.SetCaseId, entity => entity.CaseId, (_, input) => input.CaseId)
                .Set(Identities.ResolveProfile.SetSchemaId, entity => entity.SchemaId, (_, input) => input.Profile.SchemaId)
                .Set(Identities.ResolveProfile.SetProfileId, entity => entity.ProfileId, (_, input) => input.Profile.ProfileId)
                .Set(Identities.ResolveProfile.SetProfileRevision, entity => entity.ProfileRevision, (_, input) => input.Profile.Revision)
                .Set(Identities.ResolveProfile.SetBlocks, entity => entity.ResolvedBlocks, (_, input) => input.Profile.Blocks)
                .Set(Identities.ResolveProfile.SetRequirements, entity => entity.ResolvedRequirements, (_, input) => input.Profile.Requirements)
                .Set(Identities.ResolveProfile.SetEvidenceNeeds, entity => entity.ResolvedEvidenceNeeds, (_, input) => input.Profile.EvidenceNeeds)
                .Set(Identities.ResolveProfile.SetGates, entity => entity.ResolvedGates, (_, input) => input.Profile.Gates)
                .Set(Identities.ResolveProfile.SetSubjectSlots, entity => entity.ResolvedSubjectSlots, (_, input) => input.Profile.SubjectSlots)
                .Set(Identities.ResolveProfile.SetMilestone, entity => entity.Milestone, MotionDqCaseMilestone.ProfileResolved)
                .Return(
                    Identities.ResolveProfile.Outcome,
                    TransitionOutcomeDisposition.Applied,
                    MotionDqTransitionOutcome.ProfileResolved));

    static Transition<MotionDqOnboardingCaseEntity, MotionDqPrequalificationSubmission, MotionDqTransitionOutcome> AuthorSubmitPrequalification() =>
        TransitionAuthoring.Create<MotionDqOnboardingCaseEntity, MotionDqPrequalificationSubmission, MotionDqTransitionOutcome>(
            MotionDqOnboardingCaseEntity.Instance.Definition.Shape,
            Metadata(
                Identities.Prequalification.Definition,
                Identities.Prequalification.Body,
                displayName: "Submit Motion DQ prequalification",
                description: "Admits prequalification only against the exact case-pinned profile revision."),
            transition => transition
                .Requires(
                    Identities.Prequalification.ApplicationIdentityProvided,
                    (_, input) => input.ApplicationId != "",
                    (_, _) => MotionDqTransitionOutcome.ApplicationIdentityRequired)
                .Requires(
                    Identities.Prequalification.ProfileResolved,
                    (entity, _) => entity.Milestone == MotionDqCaseMilestone.ProfileResolved,
                    (_, _) => MotionDqTransitionOutcome.InvalidMilestone)
                .Requires(
                    Identities.Prequalification.CaseMatches,
                    (entity, input) => entity.CaseId == input.CaseId,
                    (_, _) => MotionDqTransitionOutcome.CaseReferenceMismatch)
                .Requires(
                    Identities.Prequalification.ProfileMatches,
                    (entity, input) => entity.ProfileId == input.ProfileId && entity.ProfileRevision == input.ProfileRevision,
                    (_, _) => MotionDqTransitionOutcome.ProfileMismatch)
                .Requires(
                    Identities.Prequalification.RequirementsSatisfied,
                    (_, input) => input.RequirementGate == MotionDqGateDisposition.Satisfied,
                    (_, _) => MotionDqTransitionOutcome.RequirementsUnsatisfied)
                .Set(Identities.Prequalification.SetApplicationId, entity => entity.ApplicationId, (_, input) => input.ApplicationId)
                .Set(
                    Identities.Prequalification.SetMilestone,
                    entity => entity.Milestone,
                    MotionDqCaseMilestone.PrequalificationSubmitted)
                .Return(
                    Identities.Prequalification.Outcome,
                    TransitionOutcomeDisposition.Applied,
                    MotionDqTransitionOutcome.PrequalificationSubmitted));

    static Transition<MotionDqOnboardingCaseEntity, MotionDqFullApplicationSubmission, MotionDqTransitionOutcome> AuthorSubmitFullApplication() =>
        TransitionAuthoring.Create<MotionDqOnboardingCaseEntity, MotionDqFullApplicationSubmission, MotionDqTransitionOutcome>(
            MotionDqOnboardingCaseEntity.Instance.Definition.Shape,
            Metadata(
                Identities.FullApplication.Definition,
                Identities.FullApplication.Body,
                displayName: "Submit Motion DQ full application",
                description: "Admits a complete full application after prequalification."),
            transition => transition
                .Requires(
                    Identities.FullApplication.Prequalified,
                    (entity, _) => entity.Milestone == MotionDqCaseMilestone.PrequalificationSubmitted,
                    (_, _) => MotionDqTransitionOutcome.InvalidMilestone)
                .Requires(
                    Identities.FullApplication.CaseMatches,
                    (entity, input) => entity.CaseId == input.CaseId,
                    (_, _) => MotionDqTransitionOutcome.CaseReferenceMismatch)
                .Requires(
                    Identities.FullApplication.ApplicationMatches,
                    (entity, input) => entity.ApplicationId == input.ApplicationId,
                    (_, _) => MotionDqTransitionOutcome.ApplicationReferenceMismatch)
                .Requires(
                    Identities.FullApplication.RequirementsSatisfied,
                    (_, input) => input.RequirementGate == MotionDqGateDisposition.Satisfied,
                    (_, _) => MotionDqTransitionOutcome.RequirementsUnsatisfied)
                .Set(
                    Identities.FullApplication.SetMilestone,
                    entity => entity.Milestone,
                    MotionDqCaseMilestone.FullApplicationSubmitted)
                .Return(
                    Identities.FullApplication.Outcome,
                    TransitionOutcomeDisposition.Applied,
                    MotionDqTransitionOutcome.FullApplicationSubmitted));

    static Transition<MotionDqOnboardingCaseEntity, MotionDqReviewDecision, MotionDqTransitionOutcome> AuthorRecordReviewDecision() =>
        TransitionAuthoring.Create<MotionDqOnboardingCaseEntity, MotionDqReviewDecision, MotionDqTransitionOutcome>(
            MotionDqOnboardingCaseEntity.Instance.Definition.Shape,
            Metadata(
                Identities.Review.Definition,
                Identities.Review.Body,
                displayName: "Record Motion DQ caseworker decision",
                description: "Applies one finite Hire, Hold, or Not Eligible decision to the onboarding case."),
            transition =>
            {
                transition.Requires(
                    Identities.Review.DecisionIdentityProvided,
                    (_, input) => input.DecisionId != "",
                    (_, _) => MotionDqTransitionOutcome.DecisionIdentityRequired);
                transition.Requires(
                    Identities.Review.AwaitingReview,
                    (entity, _) => entity.Milestone == MotionDqCaseMilestone.FullApplicationSubmitted
                        || entity.Milestone == MotionDqCaseMilestone.Held,
                    (_, _) => MotionDqTransitionOutcome.InvalidMilestone);
                transition.Requires(
                    Identities.Review.CaseMatches,
                    (entity, input) => entity.CaseId == input.CaseId,
                    (_, _) => MotionDqTransitionOutcome.CaseReferenceMismatch);
                transition.Requires(
                    Identities.Review.ApplicationMatches,
                    (entity, input) => entity.ApplicationId == input.ApplicationId,
                    (_, _) => MotionDqTransitionOutcome.ApplicationReferenceMismatch);
                transition.Match(
                    Identities.Review.Decision,
                    (_, input) => input.Kind,
                    match => match
                        .Case(
                            Identities.Review.Hire,
                            MotionDqReviewDecisionKind.Hire,
                            branch => branch
                                .Set(Identities.Review.HireDecisionId, entity => entity.LastReviewDecisionId, (_, input) => input.DecisionId)
                                .Set(Identities.Review.HireMilestone, entity => entity.Milestone, MotionDqCaseMilestone.InsuranceTerms)
                                .Return(
                                    Identities.Review.HireOutcome,
                                    TransitionOutcomeDisposition.Applied,
                                    MotionDqTransitionOutcome.ReviewDecisionRecorded))
                        .Case(
                            Identities.Review.Hold,
                            MotionDqReviewDecisionKind.Hold,
                            branch => branch
                                .Set(Identities.Review.HoldDecisionId, entity => entity.LastReviewDecisionId, (_, input) => input.DecisionId)
                                .Set(Identities.Review.HoldMilestone, entity => entity.Milestone, MotionDqCaseMilestone.Held)
                                .Return(
                                    Identities.Review.HoldOutcome,
                                    TransitionOutcomeDisposition.Applied,
                                    MotionDqTransitionOutcome.ReviewDecisionRecorded))
                        .Case(
                            Identities.Review.NotEligible,
                            MotionDqReviewDecisionKind.NotEligible,
                            branch => branch
                                .Set(Identities.Review.NotEligibleDecisionId, entity => entity.LastReviewDecisionId, (_, input) => input.DecisionId)
                                .Set(Identities.Review.NotEligibleMilestone, entity => entity.Milestone, MotionDqCaseMilestone.NotEligible)
                                .Return(
                                    Identities.Review.NotEligibleOutcome,
                                    TransitionOutcomeDisposition.Applied,
                                    MotionDqTransitionOutcome.ReviewDecisionRecorded))
                        .Fallback(
                            Identities.Review.Unrecognized,
                            branch => branch.Return(
                                Identities.Review.UnrecognizedOutcome,
                                TransitionOutcomeDisposition.DomainRejected,
                                MotionDqTransitionOutcome.ReviewDecisionUnrecognized)));
            });

    static Transition<MotionDqOnboardingCaseEntity, MotionDqCaseMilestoneAdmission, MotionDqTransitionOutcome> AuthorAdvanceCaseMilestone() =>
        TransitionAuthoring.Create<MotionDqOnboardingCaseEntity, MotionDqCaseMilestoneAdmission, MotionDqTransitionOutcome>(
            MotionDqOnboardingCaseEntity.Instance.Definition.Shape,
            Metadata(
                Identities.Milestone.Definition,
                Identities.Milestone.Body,
                displayName: "Advance Motion DQ case milestone",
                description: "Applies one of the three finite post-Hire case edges under an exact durable gate decision."),
            transition =>
            {
                transition.Requires(
                    Identities.Milestone.CaseMatches,
                    (entity, input) => entity.CaseId == input.CaseId,
                    (_, _) => MotionDqTransitionOutcome.CaseReferenceMismatch);
                transition.Requires(
                    Identities.Milestone.CurrentMatches,
                    (entity, input) => entity.Milestone == input.ExpectedMilestone,
                    (_, _) => MotionDqTransitionOutcome.InvalidMilestone);
                transition.Requires(
                    Identities.Milestone.DecisionProvided,
                    (_, input) => input.DecisionId != "",
                    (_, _) => MotionDqTransitionOutcome.DecisionIdentityRequired);
                transition.Choose(
                    Identities.Milestone.Edge,
                    choice => choice
                        .Case(
                            Identities.Milestone.InsuranceTermsEdge,
                            (_, input) => input.ExpectedMilestone == MotionDqCaseMilestone.InsuranceTerms
                                && input.NextMilestone == MotionDqCaseMilestone.PostTerms,
                            branch => branch.Choose(
                                Identities.Milestone.InsuranceTermsGate,
                                gate => gate
                                    .Case(
                                        Identities.Milestone.InsuranceTermsGateMismatch,
                                        (_, input) => input.GateId != MotionDqVocabulary.Gates.InsuranceTermsAccepted,
                                        rejected => rejected.Return(
                                            Identities.Milestone.InsuranceTermsGateMismatchOutcome,
                                            TransitionOutcomeDisposition.DomainRejected,
                                            MotionDqTransitionOutcome.MilestoneGateMismatch))
                                    .Case(
                                        Identities.Milestone.InsuranceTermsSatisfied,
                                        (_, input) => input.GateDisposition == MotionDqGateDisposition.Satisfied,
                                        admitted => admitted
                                            .Set(
                                                Identities.Milestone.SetInsuranceTermsDecision,
                                                entity => entity.LastMilestoneDecisionId,
                                                (_, input) => input.DecisionId)
                                            .Set(
                                                Identities.Milestone.SetPostTerms,
                                                entity => entity.Milestone,
                                                MotionDqCaseMilestone.PostTerms)
                                            .Return(
                                                Identities.Milestone.InsuranceTermsOutcome,
                                                TransitionOutcomeDisposition.Applied,
                                                MotionDqTransitionOutcome.MilestoneAdvanced))
                                    .Fallback(
                                        Identities.Milestone.InsuranceTermsUnsatisfied,
                                        rejected => rejected.Return(
                                            Identities.Milestone.InsuranceTermsUnsatisfiedOutcome,
                                            TransitionOutcomeDisposition.DomainRejected,
                                            MotionDqTransitionOutcome.MilestoneGateUnsatisfied))))
                        .Case(
                            Identities.Milestone.PostTermsEdge,
                            (_, input) => input.ExpectedMilestone == MotionDqCaseMilestone.PostTerms
                                && input.NextMilestone == MotionDqCaseMilestone.Activation,
                            branch => branch.Choose(
                                Identities.Milestone.PostTermsGate,
                                gate => gate
                                    .Case(
                                        Identities.Milestone.PostTermsGateMismatch,
                                        (_, input) => input.GateId != MotionDqVocabulary.Gates.PostTermsComplete,
                                        rejected => rejected.Return(
                                            Identities.Milestone.PostTermsGateMismatchOutcome,
                                            TransitionOutcomeDisposition.DomainRejected,
                                            MotionDqTransitionOutcome.MilestoneGateMismatch))
                                    .Case(
                                        Identities.Milestone.PostTermsSatisfied,
                                        (_, input) => input.GateDisposition == MotionDqGateDisposition.Satisfied,
                                        admitted => admitted
                                            .Set(
                                                Identities.Milestone.SetPostTermsDecision,
                                                entity => entity.LastMilestoneDecisionId,
                                                (_, input) => input.DecisionId)
                                            .Set(
                                                Identities.Milestone.SetActivation,
                                                entity => entity.Milestone,
                                                MotionDqCaseMilestone.Activation)
                                            .Return(
                                                Identities.Milestone.PostTermsOutcome,
                                                TransitionOutcomeDisposition.Applied,
                                                MotionDqTransitionOutcome.MilestoneAdvanced))
                                    .Fallback(
                                        Identities.Milestone.PostTermsUnsatisfied,
                                        rejected => rejected.Return(
                                            Identities.Milestone.PostTermsUnsatisfiedOutcome,
                                            TransitionOutcomeDisposition.DomainRejected,
                                            MotionDqTransitionOutcome.MilestoneGateUnsatisfied))))
                        .Case(
                            Identities.Milestone.ActivationEdge,
                            (_, input) => input.ExpectedMilestone == MotionDqCaseMilestone.Activation
                                && input.NextMilestone == MotionDqCaseMilestone.Completed,
                            branch => branch.Choose(
                                Identities.Milestone.ActivationGate,
                                gate => gate
                                    .Case(
                                        Identities.Milestone.ActivationGateMismatch,
                                        (_, input) => input.GateId != MotionDqVocabulary.Gates.ActivationComplete,
                                        rejected => rejected.Return(
                                            Identities.Milestone.ActivationGateMismatchOutcome,
                                            TransitionOutcomeDisposition.DomainRejected,
                                            MotionDqTransitionOutcome.MilestoneGateMismatch))
                                    .Case(
                                        Identities.Milestone.ActivationSatisfied,
                                        (_, input) => input.GateDisposition == MotionDqGateDisposition.Satisfied,
                                        admitted => admitted
                                            .Set(
                                                Identities.Milestone.SetActivationDecision,
                                                entity => entity.LastMilestoneDecisionId,
                                                (_, input) => input.DecisionId)
                                            .Set(
                                                Identities.Milestone.SetCompleted,
                                                entity => entity.Milestone,
                                                MotionDqCaseMilestone.Completed)
                                            .Return(
                                                Identities.Milestone.ActivationOutcome,
                                                TransitionOutcomeDisposition.Applied,
                                                MotionDqTransitionOutcome.MilestoneAdvanced))
                                    .Fallback(
                                        Identities.Milestone.ActivationUnsatisfied,
                                        rejected => rejected.Return(
                                            Identities.Milestone.ActivationUnsatisfiedOutcome,
                                            TransitionOutcomeDisposition.DomainRejected,
                                            MotionDqTransitionOutcome.MilestoneGateUnsatisfied))))
                        .Fallback(
                            Identities.Milestone.UnsupportedEdge,
                            branch => branch.Return(
                                Identities.Milestone.UnsupportedEdgeOutcome,
                                TransitionOutcomeDisposition.DomainRejected,
                                MotionDqTransitionOutcome.UnsupportedMilestoneEdge)));
            });

    static Transition<MotionDqOnboardingCaseEntity, MotionDqCancellation, MotionDqTransitionOutcome> AuthorCancelCase() =>
        TransitionAuthoring.Create<MotionDqOnboardingCaseEntity, MotionDqCancellation, MotionDqTransitionOutcome>(
            MotionDqOnboardingCaseEntity.Instance.Definition.Shape,
            Metadata(
                Identities.Cancel.Definition,
                Identities.Cancel.Body,
                displayName: "Cancel Motion DQ onboarding case",
                description: "Records cancellation independently of the delivery channel."),
            transition => transition
                .Requires(
                    Identities.Cancel.CancellationIdentityProvided,
                    (_, input) => input.CancellationId != "",
                    (_, _) => MotionDqTransitionOutcome.CancellationIdentityRequired)
                .Requires(
                    Identities.Cancel.NotTerminal,
                    (entity, _) => entity.Milestone != MotionDqCaseMilestone.Completed
                        && entity.Milestone != MotionDqCaseMilestone.NotEligible
                        && entity.Milestone != MotionDqCaseMilestone.Cancelled,
                    (_, _) => MotionDqTransitionOutcome.AlreadyTerminal)
                .Requires(
                    Identities.Cancel.CaseMatches,
                    (entity, input) => entity.CaseId == input.CaseId,
                    (_, _) => MotionDqTransitionOutcome.CaseReferenceMismatch)
                .Set(Identities.Cancel.SetCancellationId, entity => entity.CancellationId, (_, input) => input.CancellationId)
                .Set(Identities.Cancel.SetMilestone, entity => entity.Milestone, MotionDqCaseMilestone.Cancelled)
                .Return(
                    Identities.Cancel.Outcome,
                    TransitionOutcomeDisposition.Applied,
                    MotionDqTransitionOutcome.Cancelled));

    static Transition<MotionDqCaseRequirementEntity, MotionDqRequirementEvaluationReceipt, MotionDqTransitionOutcome> AuthorApplyRequirementEvaluation() =>
        TransitionAuthoring.Create<MotionDqCaseRequirementEntity, MotionDqRequirementEvaluationReceipt, MotionDqTransitionOutcome>(
            MotionDqCaseRequirementEntity.Instance.Definition.Shape,
            Metadata(
                Identities.Requirement.Definition,
                Identities.Requirement.Body,
                displayName: "Apply Motion DQ requirement evaluation",
                description: "Classifies an endogenous evidence evaluation as accepted, duplicate, conflicting, or superseded."),
            transition =>
            {
                transition.Requires(
                    Identities.Requirement.EvaluationIdentityProvided,
                    (_, input) => input.EvaluationId != "",
                    (_, _) => MotionDqTransitionOutcome.EvaluationIdentityRequired);
                transition.Requires(
                    Identities.Requirement.EvidenceIdentityProvided,
                    (_, input) => input.EvidenceId != "",
                    (_, _) => MotionDqTransitionOutcome.EvidenceIdentityRequired);
                transition.Requires(
                    Identities.Requirement.CaseMatches,
                    (entity, input) => entity.CaseId == input.Requirement.CaseId,
                    (_, _) => MotionDqTransitionOutcome.CaseReferenceMismatch);
                transition.Requires(
                    Identities.Requirement.RequirementMatches,
                    (entity, input) => entity.RequirementId == input.Requirement.RequirementId,
                    (_, _) => MotionDqTransitionOutcome.RequirementMismatch);
                transition.Choose(
                    Identities.Requirement.Classification,
                    choice => choice
                        .Case(
                            Identities.Requirement.Duplicate,
                            (entity, input) => entity.Evaluations.Contains(input),
                            branch => branch.Return(
                                Identities.Requirement.DuplicateOutcome,
                                TransitionOutcomeDisposition.NoChange,
                                MotionDqTransitionOutcome.RequirementEvaluationDuplicate))
                        .Case(
                            Identities.Requirement.IdentityConflict,
                            (entity, input) => entity.ObservedEvaluationIds.Contains(input.EvaluationId),
                            branch => branch.Return(
                                Identities.Requirement.IdentityConflictOutcome,
                                TransitionOutcomeDisposition.DomainRejected,
                                MotionDqTransitionOutcome.RequirementEvaluationIdentityConflict))
                        .Case(
                            Identities.Requirement.Superseded,
                            (entity, _) => entity.Status != MotionDqRequirementStatus.Pending,
                            branch => branch
                                .Append(Identities.Requirement.AppendSuperseded, entity => entity.Evaluations, (_, input) => input)
                                .Append(
                                    Identities.Requirement.AppendSupersededEvaluationId,
                                    entity => entity.ObservedEvaluationIds,
                                    (_, input) => input.EvaluationId)
                                .Return(
                                    Identities.Requirement.SupersededOutcome,
                                    TransitionOutcomeDisposition.Applied,
                                    MotionDqTransitionOutcome.RequirementEvaluationSuperseded))
                        .Fallback(
                            Identities.Requirement.Accepted,
                            branch => branch
                                .Append(Identities.Requirement.AppendAccepted, entity => entity.Evaluations, (_, input) => input)
                                .Append(
                                    Identities.Requirement.AppendAcceptedEvaluationId,
                                    entity => entity.ObservedEvaluationIds,
                                    (_, input) => input.EvaluationId)
                                .Set(Identities.Requirement.SetAuthoritativeEvaluationId, entity => entity.AuthoritativeEvaluationId, (_, input) => input.EvaluationId)
                                .Match(
                                    Identities.Requirement.Disposition,
                                    (_, input) => input.Disposition,
                                    match => match
                                        .Case(
                                            Identities.Requirement.Satisfied,
                                            MotionDqGateDisposition.Satisfied,
                                            satisfied => satisfied
                                                .Set(Identities.Requirement.SetSatisfied, entity => entity.Status, MotionDqRequirementStatus.Satisfied)
                                                .Return(
                                                    Identities.Requirement.SatisfiedOutcome,
                                                    TransitionOutcomeDisposition.Applied,
                                                    MotionDqTransitionOutcome.RequirementEvaluationAccepted))
                                        .Case(
                                            Identities.Requirement.Unsatisfied,
                                            MotionDqGateDisposition.Unsatisfied,
                                            unsatisfied => unsatisfied
                                                .Set(Identities.Requirement.SetUnsatisfied, entity => entity.Status, MotionDqRequirementStatus.Unsatisfied)
                                                .Return(
                                                    Identities.Requirement.UnsatisfiedOutcome,
                                                    TransitionOutcomeDisposition.Applied,
                                                    MotionDqTransitionOutcome.RequirementEvaluationAccepted))
                                        .Fallback(
                                            Identities.Requirement.Unrecognized,
                                            unrecognized => unrecognized.Return(
                                                Identities.Requirement.UnrecognizedOutcome,
                                                TransitionOutcomeDisposition.DomainRejected,
                                                MotionDqTransitionOutcome.RequirementEvaluationUnrecognized)))));
                transition.Invariant(
                    Identities.Requirement.IndexAligned,
                    entity => entity.ObservedEvaluationIds.Count == entity.Evaluations.Count);
                transition.Invariant(
                    Identities.Requirement.AuthorityObserved,
                    entity => entity.Status == MotionDqRequirementStatus.Pending
                        || entity.ObservedEvaluationIds.Contains(entity.AuthoritativeEvaluationId));
            });

    static Transition<MotionDqSubjectActivationEntity, MotionDqSubjectActivationAdmission, MotionDqTransitionOutcome> AuthorActivateSubject() =>
        TransitionAuthoring.Create<MotionDqSubjectActivationEntity, MotionDqSubjectActivationAdmission, MotionDqTransitionOutcome>(
            MotionDqSubjectActivationEntity.Instance.Definition.Shape,
            Metadata(
                Identities.Activation.Definition,
                Identities.Activation.Body,
                displayName: "Activate Motion DQ subject",
                description: "Applies an explicit per-subject activation gate and the dependent-driver carrier gate."),
            transition => transition
                .Requires(
                    Identities.Activation.DecisionIdentityProvided,
                    (_, input) => input.DecisionId != "",
                    (_, _) => MotionDqTransitionOutcome.DecisionIdentityRequired)
                .Requires(
                    Identities.Activation.KindMatches,
                    (entity, input) => entity.Kind == input.Kind,
                    (_, _) => MotionDqTransitionOutcome.SubjectKindMismatch)
                .Requires(
                    Identities.Activation.GateMatches,
                    (entity, input) => entity.ActivationGateId == input.GateId,
                    (_, _) => MotionDqTransitionOutcome.ActivationGateMismatch)
                .Requires(
                    Identities.Activation.Pending,
                    (entity, _) => entity.Status == MotionDqActivationStatus.Pending,
                    (_, _) => MotionDqTransitionOutcome.AlreadyActive)
                .Requires(
                    Identities.Activation.GateSatisfied,
                    (_, input) => input.GateDisposition == MotionDqGateDisposition.Satisfied,
                    (_, _) => MotionDqTransitionOutcome.ActivationGateUnsatisfied)
                .Requires(
                    Identities.Activation.DriverCarrierGate,
                    (_, input) => input.Kind != MotionDqSubjectKind.Driver
                        || input.ParentCarrierProof != null,
                    (_, _) => MotionDqTransitionOutcome.DriverCarrierGateRequired)
                .Requires(
                    Identities.Activation.NonDriverProofAbsent,
                    (_, input) => input.Kind == MotionDqSubjectKind.Driver
                        || input.ParentCarrierProof == null,
                    (_, _) => MotionDqTransitionOutcome.UnexpectedParentCarrierProof)
                .Requires(
                    Identities.Activation.DriverCarrierProofMatches,
                    (entity, input) => input.Kind != MotionDqSubjectKind.Driver
                        || entity.RequiredParentCarrierProof == input.ParentCarrierProof,
                    (_, _) => MotionDqTransitionOutcome.DriverCarrierGateRequired)
                .Set(
                    Identities.Activation.SetDecisionId,
                    entity => entity.LastActivationDecisionId,
                    (_, input) => input.DecisionId)
                .Set(
                    Identities.Activation.SetAdmittedParentCarrierProof,
                    entity => entity.AdmittedParentCarrierProof,
                    (_, input) => input.ParentCarrierProof)
                .Set(
                    Identities.Activation.SetStatus,
                    entity => entity.Status,
                    MotionDqActivationStatus.Active)
                .Return(
                    Identities.Activation.Outcome,
                    TransitionOutcomeDisposition.Applied,
                    MotionDqTransitionOutcome.SubjectActivated));

    static TransitionAuthoringMetadata Metadata(
        ExecutionDefinitionId definition,
        ExecutionNodeId body,
        string displayName,
        string description) => new(
        definition,
        Identities.Revision,
        body,
        new(
            new(TransitionAuthoring.Producer),
            new("fixtures/motion-dq/onboarding"),
            DocumentOrigin.Generated),
        displayName: displayName,
        description: description);

    static class Identities
    {
        public static readonly ExecutionRevisionId Revision = new("revision/1");

        public static class ResolveProfile
        {
            public static readonly ExecutionDefinitionId Definition = new("transition/motion-dq/case/resolve-profile");
            public static readonly ExecutionNodeId Body = new("motion-dq/resolve-profile/body");
            public static readonly ExecutionNodeId CaseIdentityProvided = new("motion-dq/resolve-profile/admit/case-identity");
            public static readonly ExecutionNodeId SchemaIdentityProvided = new("motion-dq/resolve-profile/admit/schema-identity");
            public static readonly ExecutionNodeId ProfileIdentityProvided = new("motion-dq/resolve-profile/admit/profile-identity");
            public static readonly ExecutionNodeId ProfileRevisionProvided = new("motion-dq/resolve-profile/admit/profile-revision");
            public static readonly ExecutionNodeId InitialCase = new("motion-dq/resolve-profile/admit/uninitialized");
            public static readonly ExecutionNodeId SetCaseId = new("motion-dq/resolve-profile/set/case-id");
            public static readonly ExecutionNodeId SetSchemaId = new("motion-dq/resolve-profile/set/schema-id");
            public static readonly ExecutionNodeId SetProfileId = new("motion-dq/resolve-profile/set/profile-id");
            public static readonly ExecutionNodeId SetProfileRevision = new("motion-dq/resolve-profile/set/profile-revision");
            public static readonly ExecutionNodeId SetBlocks = new("motion-dq/resolve-profile/set/blocks");
            public static readonly ExecutionNodeId SetRequirements = new("motion-dq/resolve-profile/set/requirements");
            public static readonly ExecutionNodeId SetEvidenceNeeds = new("motion-dq/resolve-profile/set/evidence-needs");
            public static readonly ExecutionNodeId SetGates = new("motion-dq/resolve-profile/set/gates");
            public static readonly ExecutionNodeId SetSubjectSlots = new("motion-dq/resolve-profile/set/subject-slots");
            public static readonly ExecutionNodeId SetMilestone = new("motion-dq/resolve-profile/set/milestone");
            public static readonly ExecutionNodeId Outcome = new("motion-dq/resolve-profile/outcome/resolved");
        }

        public static class Prequalification
        {
            public static readonly ExecutionDefinitionId Definition = new("transition/motion-dq/case/submit-prequalification");
            public static readonly ExecutionNodeId Body = new("motion-dq/prequalification/body");
            public static readonly ExecutionNodeId ApplicationIdentityProvided = new("motion-dq/prequalification/admit/application-identity");
            public static readonly ExecutionNodeId ProfileResolved = new("motion-dq/prequalification/admit/profile-resolved");
            public static readonly ExecutionNodeId CaseMatches = new("motion-dq/prequalification/admit/case");
            public static readonly ExecutionNodeId ProfileMatches = new("motion-dq/prequalification/admit/profile");
            public static readonly ExecutionNodeId RequirementsSatisfied = new("motion-dq/prequalification/admit/requirements");
            public static readonly ExecutionNodeId SetApplicationId = new("motion-dq/prequalification/set/application-id");
            public static readonly ExecutionNodeId SetMilestone = new("motion-dq/prequalification/set/milestone");
            public static readonly ExecutionNodeId Outcome = new("motion-dq/prequalification/outcome/submitted");
        }

        public static class FullApplication
        {
            public static readonly ExecutionDefinitionId Definition = new("transition/motion-dq/case/submit-full-application");
            public static readonly ExecutionNodeId Body = new("motion-dq/full-application/body");
            public static readonly ExecutionNodeId Prequalified = new("motion-dq/full-application/admit/prequalified");
            public static readonly ExecutionNodeId CaseMatches = new("motion-dq/full-application/admit/case");
            public static readonly ExecutionNodeId ApplicationMatches = new("motion-dq/full-application/admit/application");
            public static readonly ExecutionNodeId RequirementsSatisfied = new("motion-dq/full-application/admit/requirements");
            public static readonly ExecutionNodeId SetMilestone = new("motion-dq/full-application/set/milestone");
            public static readonly ExecutionNodeId Outcome = new("motion-dq/full-application/outcome/submitted");
        }

        public static class Review
        {
            public static readonly ExecutionDefinitionId Definition = new("transition/motion-dq/case/record-review-decision");
            public static readonly ExecutionNodeId Body = new("motion-dq/review/body");
            public static readonly ExecutionNodeId DecisionIdentityProvided = new("motion-dq/review/admit/decision-identity");
            public static readonly ExecutionNodeId AwaitingReview = new("motion-dq/review/admit/awaiting-review");
            public static readonly ExecutionNodeId CaseMatches = new("motion-dq/review/admit/case");
            public static readonly ExecutionNodeId ApplicationMatches = new("motion-dq/review/admit/application");
            public static readonly ExecutionNodeId Decision = new("motion-dq/review/match/decision");
            public static readonly ExecutionNodeId Hire = new("motion-dq/review/case/hire");
            public static readonly ExecutionNodeId HireDecisionId = new("motion-dq/review/hire/set/decision-id");
            public static readonly ExecutionNodeId HireMilestone = new("motion-dq/review/hire/set/milestone");
            public static readonly ExecutionNodeId HireOutcome = new("motion-dq/review/hire/outcome");
            public static readonly ExecutionNodeId Hold = new("motion-dq/review/case/hold");
            public static readonly ExecutionNodeId HoldDecisionId = new("motion-dq/review/hold/set/decision-id");
            public static readonly ExecutionNodeId HoldMilestone = new("motion-dq/review/hold/set/milestone");
            public static readonly ExecutionNodeId HoldOutcome = new("motion-dq/review/hold/outcome");
            public static readonly ExecutionNodeId NotEligible = new("motion-dq/review/case/not-eligible");
            public static readonly ExecutionNodeId NotEligibleDecisionId = new("motion-dq/review/not-eligible/set/decision-id");
            public static readonly ExecutionNodeId NotEligibleMilestone = new("motion-dq/review/not-eligible/set/milestone");
            public static readonly ExecutionNodeId NotEligibleOutcome = new("motion-dq/review/not-eligible/outcome");
            public static readonly ExecutionNodeId Unrecognized = new("motion-dq/review/fallback/unrecognized");
            public static readonly ExecutionNodeId UnrecognizedOutcome = new("motion-dq/review/unrecognized/outcome");
        }

        public static class Milestone
        {
            public static readonly ExecutionDefinitionId Definition = new("transition/motion-dq/case/advance-milestone");
            public static readonly ExecutionNodeId Body = new("motion-dq/milestone/body");
            public static readonly ExecutionNodeId CaseMatches = new("motion-dq/milestone/admit/case");
            public static readonly ExecutionNodeId CurrentMatches = new("motion-dq/milestone/admit/current");
            public static readonly ExecutionNodeId DecisionProvided = new("motion-dq/milestone/admit/decision");
            public static readonly ExecutionNodeId Edge = new("motion-dq/milestone/choice/edge");
            public static readonly ExecutionNodeId InsuranceTermsEdge = new("motion-dq/milestone/edge/insurance-terms");
            public static readonly ExecutionNodeId InsuranceTermsGate = new("motion-dq/milestone/insurance-terms/choice/gate");
            public static readonly ExecutionNodeId InsuranceTermsGateMismatch = new("motion-dq/milestone/insurance-terms/gate/mismatch");
            public static readonly ExecutionNodeId InsuranceTermsGateMismatchOutcome = new("motion-dq/milestone/insurance-terms/gate/mismatch/outcome");
            public static readonly ExecutionNodeId InsuranceTermsSatisfied = new("motion-dq/milestone/insurance-terms/gate/satisfied");
            public static readonly ExecutionNodeId SetInsuranceTermsDecision = new("motion-dq/milestone/insurance-terms/set/decision");
            public static readonly ExecutionNodeId SetPostTerms = new("motion-dq/milestone/insurance-terms/set/post-terms");
            public static readonly ExecutionNodeId InsuranceTermsOutcome = new("motion-dq/milestone/insurance-terms/outcome");
            public static readonly ExecutionNodeId InsuranceTermsUnsatisfied = new("motion-dq/milestone/insurance-terms/gate/unsatisfied");
            public static readonly ExecutionNodeId InsuranceTermsUnsatisfiedOutcome = new("motion-dq/milestone/insurance-terms/gate/unsatisfied/outcome");
            public static readonly ExecutionNodeId PostTermsEdge = new("motion-dq/milestone/edge/post-terms");
            public static readonly ExecutionNodeId PostTermsGate = new("motion-dq/milestone/post-terms/choice/gate");
            public static readonly ExecutionNodeId PostTermsGateMismatch = new("motion-dq/milestone/post-terms/gate/mismatch");
            public static readonly ExecutionNodeId PostTermsGateMismatchOutcome = new("motion-dq/milestone/post-terms/gate/mismatch/outcome");
            public static readonly ExecutionNodeId PostTermsSatisfied = new("motion-dq/milestone/post-terms/gate/satisfied");
            public static readonly ExecutionNodeId SetPostTermsDecision = new("motion-dq/milestone/post-terms/set/decision");
            public static readonly ExecutionNodeId SetActivation = new("motion-dq/milestone/post-terms/set/activation");
            public static readonly ExecutionNodeId PostTermsOutcome = new("motion-dq/milestone/post-terms/outcome");
            public static readonly ExecutionNodeId PostTermsUnsatisfied = new("motion-dq/milestone/post-terms/gate/unsatisfied");
            public static readonly ExecutionNodeId PostTermsUnsatisfiedOutcome = new("motion-dq/milestone/post-terms/gate/unsatisfied/outcome");
            public static readonly ExecutionNodeId ActivationEdge = new("motion-dq/milestone/edge/activation");
            public static readonly ExecutionNodeId ActivationGate = new("motion-dq/milestone/activation/choice/gate");
            public static readonly ExecutionNodeId ActivationGateMismatch = new("motion-dq/milestone/activation/gate/mismatch");
            public static readonly ExecutionNodeId ActivationGateMismatchOutcome = new("motion-dq/milestone/activation/gate/mismatch/outcome");
            public static readonly ExecutionNodeId ActivationSatisfied = new("motion-dq/milestone/activation/gate/satisfied");
            public static readonly ExecutionNodeId SetActivationDecision = new("motion-dq/milestone/activation/set/decision");
            public static readonly ExecutionNodeId SetCompleted = new("motion-dq/milestone/activation/set/completed");
            public static readonly ExecutionNodeId ActivationOutcome = new("motion-dq/milestone/activation/outcome");
            public static readonly ExecutionNodeId ActivationUnsatisfied = new("motion-dq/milestone/activation/gate/unsatisfied");
            public static readonly ExecutionNodeId ActivationUnsatisfiedOutcome = new("motion-dq/milestone/activation/gate/unsatisfied/outcome");
            public static readonly ExecutionNodeId UnsupportedEdge = new("motion-dq/milestone/edge/unsupported");
            public static readonly ExecutionNodeId UnsupportedEdgeOutcome = new("motion-dq/milestone/edge/unsupported/outcome");
        }

        public static class Cancel
        {
            public static readonly ExecutionDefinitionId Definition = new("transition/motion-dq/case/cancel");
            public static readonly ExecutionNodeId Body = new("motion-dq/cancel/body");
            public static readonly ExecutionNodeId CancellationIdentityProvided = new("motion-dq/cancel/admit/cancellation-identity");
            public static readonly ExecutionNodeId NotTerminal = new("motion-dq/cancel/admit/not-terminal");
            public static readonly ExecutionNodeId CaseMatches = new("motion-dq/cancel/admit/case");
            public static readonly ExecutionNodeId SetCancellationId = new("motion-dq/cancel/set/cancellation-id");
            public static readonly ExecutionNodeId SetMilestone = new("motion-dq/cancel/set/milestone");
            public static readonly ExecutionNodeId Outcome = new("motion-dq/cancel/outcome/cancelled");
        }

        public static class Requirement
        {
            public static readonly ExecutionDefinitionId Definition = new("transition/motion-dq/requirement/apply-evaluation");
            public static readonly ExecutionNodeId Body = new("motion-dq/requirement/body");
            public static readonly ExecutionNodeId EvaluationIdentityProvided = new("motion-dq/requirement/admit/evaluation-identity");
            public static readonly ExecutionNodeId EvidenceIdentityProvided = new("motion-dq/requirement/admit/evidence-identity");
            public static readonly ExecutionNodeId CaseMatches = new("motion-dq/requirement/admit/case");
            public static readonly ExecutionNodeId RequirementMatches = new("motion-dq/requirement/admit/identity");
            public static readonly ExecutionNodeId Classification = new("motion-dq/requirement/choice/classification");
            public static readonly ExecutionNodeId Duplicate = new("motion-dq/requirement/case/duplicate");
            public static readonly ExecutionNodeId DuplicateOutcome = new("motion-dq/requirement/duplicate/outcome");
            public static readonly ExecutionNodeId IdentityConflict = new("motion-dq/requirement/case/identity-conflict");
            public static readonly ExecutionNodeId IdentityConflictOutcome = new("motion-dq/requirement/identity-conflict/outcome");
            public static readonly ExecutionNodeId Superseded = new("motion-dq/requirement/case/superseded");
            public static readonly ExecutionNodeId AppendSuperseded = new("motion-dq/requirement/superseded/append-evaluation");
            public static readonly ExecutionNodeId AppendSupersededEvaluationId = new("motion-dq/requirement/superseded/append-evaluation-id");
            public static readonly ExecutionNodeId SupersededOutcome = new("motion-dq/requirement/superseded/outcome");
            public static readonly ExecutionNodeId Accepted = new("motion-dq/requirement/fallback/accepted");
            public static readonly ExecutionNodeId AppendAccepted = new("motion-dq/requirement/accepted/append-evaluation");
            public static readonly ExecutionNodeId AppendAcceptedEvaluationId = new("motion-dq/requirement/accepted/append-evaluation-id");
            public static readonly ExecutionNodeId SetAuthoritativeEvaluationId = new("motion-dq/requirement/accepted/set-authoritative-evaluation-id");
            public static readonly ExecutionNodeId Disposition = new("motion-dq/requirement/match/disposition");
            public static readonly ExecutionNodeId Satisfied = new("motion-dq/requirement/case/satisfied");
            public static readonly ExecutionNodeId SetSatisfied = new("motion-dq/requirement/satisfied/set-status");
            public static readonly ExecutionNodeId SatisfiedOutcome = new("motion-dq/requirement/satisfied/outcome");
            public static readonly ExecutionNodeId Unsatisfied = new("motion-dq/requirement/case/unsatisfied");
            public static readonly ExecutionNodeId SetUnsatisfied = new("motion-dq/requirement/unsatisfied/set-status");
            public static readonly ExecutionNodeId UnsatisfiedOutcome = new("motion-dq/requirement/unsatisfied/outcome");
            public static readonly ExecutionNodeId Unrecognized = new("motion-dq/requirement/fallback/unrecognized");
            public static readonly ExecutionNodeId UnrecognizedOutcome = new("motion-dq/requirement/unrecognized/outcome");
            public static readonly ExecutionNodeId IndexAligned = new("motion-dq/requirement/invariant/index-aligned");
            public static readonly ExecutionNodeId AuthorityObserved = new("motion-dq/requirement/invariant/authority-observed");
        }

        public static class Activation
        {
            public static readonly ExecutionDefinitionId Definition = new("transition/motion-dq/subject/activate");
            public static readonly ExecutionNodeId Body = new("motion-dq/activation/body");
            public static readonly ExecutionNodeId DecisionIdentityProvided = new("motion-dq/activation/admit/decision-identity");
            public static readonly ExecutionNodeId KindMatches = new("motion-dq/activation/admit/kind");
            public static readonly ExecutionNodeId GateMatches = new("motion-dq/activation/admit/gate");
            public static readonly ExecutionNodeId Pending = new("motion-dq/activation/admit/pending");
            public static readonly ExecutionNodeId GateSatisfied = new("motion-dq/activation/admit/satisfied");
            public static readonly ExecutionNodeId DriverCarrierGate = new("motion-dq/activation/admit/driver-carrier");
            public static readonly ExecutionNodeId NonDriverProofAbsent = new("motion-dq/activation/admit/non-driver-proof-absent");
            public static readonly ExecutionNodeId DriverCarrierProofMatches = new("motion-dq/activation/admit/driver-carrier-proof");
            public static readonly ExecutionNodeId SetDecisionId = new("motion-dq/activation/set/decision-id");
            public static readonly ExecutionNodeId SetAdmittedParentCarrierProof = new("motion-dq/activation/set/parent-carrier-proof");
            public static readonly ExecutionNodeId SetStatus = new("motion-dq/activation/set/status");
            public static readonly ExecutionNodeId Outcome = new("motion-dq/activation/outcome/active");
        }
    }
}
