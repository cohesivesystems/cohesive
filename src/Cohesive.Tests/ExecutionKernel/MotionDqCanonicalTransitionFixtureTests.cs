using System.Text;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.MotionDq;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class MotionDqCanonicalTransitionFixtureTests
{
    [Fact]
    public void VersionOneProfile_HasExactVersionedMembershipAndBoundedSubjectDependencies()
    {
        var profile = MotionDqProfileCatalog.Version1;

        Assert.Equal("motion-dq/onboarding-case-schema/v1", profile.SchemaId);
        Assert.Equal("motion-dq/onboarding/v1", profile.ProfileId);
        Assert.Equal("revision/1", profile.Revision);
        Assert.Equal(
            [
                "block/prequalification",
                "block/full-application",
                "block/caseworker-review",
                "block/insurance-terms",
                "block/drug-test",
                "block/clearinghouse",
                "block/vehicle",
                "block/business",
                "block/equipment",
                "block/permit",
                "block/random-pool",
                "block/activation"
            ],
            profile.Blocks.Select(static block => block.Id));
        Assert.Equal(
            [
                "requirement/insurance-terms",
                "requirement/drug-test",
                "requirement/clearinghouse",
                "requirement/vehicle",
                "requirement/business",
                "requirement/equipment",
                "requirement/permit",
                "requirement/random-pool"
            ],
            profile.Requirements.Select(static requirement => requirement.Id));
        Assert.Equal(
            [
                "evidence/insurance-terms",
                "evidence/drug-test",
                "evidence/clearinghouse",
                "evidence/vehicle",
                "evidence/business",
                "evidence/equipment",
                "evidence/permit",
                "evidence/random-pool"
            ],
            profile.EvidenceNeeds.Select(static evidenceNeed => evidenceNeed.Id));
        Assert.Equal(
            [
                "gate/review-admission",
                "gate/insurance-terms-accepted",
                "gate/post-terms-complete",
                "gate/activation-complete",
                "gate/activation/applicant",
                "gate/activation/driver",
                "gate/activation/carrier-owner-operator",
                "gate/activation/truck",
                "gate/activation/trailer"
            ],
            profile.Gates.Select(static gate => gate.Id));

        Assert.Equal(5, profile.SubjectSlots.Length);
        Assert.Equal(
            Enum.GetValues<MotionDqSubjectKind>().Order(),
            profile.SubjectSlots.Select(static slot => slot.Kind).Order());
        Assert.Equal(
            profile.SubjectSlots.Length,
            profile.SubjectSlots.Select(static slot => slot.Kind).Distinct().Count());

        var driver = Assert.Single(
            profile.SubjectSlots,
            static slot => slot.Kind == MotionDqSubjectKind.Driver);
        Assert.Equal(MotionDqSubjectKind.CarrierOwnerOperator, driver.DependsOnSubject);
        Assert.Equal("gate/activation/driver", driver.ActivationGate.Id);
        Assert.All(
            profile.SubjectSlots.Where(static slot => slot.Kind != MotionDqSubjectKind.Driver),
            static slot => Assert.Null(slot.DependsOnSubject));
        Assert.Equal(
            "gate/activation/carrier-owner-operator",
            Assert.Single(
                profile.SubjectSlots,
                static slot => slot.Kind == MotionDqSubjectKind.CarrierOwnerOperator).ActivationGate.Id);

        var resolution = MotionDqProfileCatalog.CreateCaseProfileResolution(caseId: "case-181");
        Assert.Equal("case-181", resolution.CaseId);
        Assert.Same(profile, resolution.Profile);
    }

    [Fact]
    public void TypedAuthoring_IsDeterministicAndEveryDocumentStrictlyRoundTripsAndCompiles()
    {
        var first = MotionDqTransitions.Author();
        var second = MotionDqTransitions.Author();

        Assert.Equal(8, first.Documents.Length);
        Assert.Equal(
            [
                "transition/motion-dq/case/resolve-profile",
                "transition/motion-dq/case/submit-prequalification",
                "transition/motion-dq/case/submit-full-application",
                "transition/motion-dq/case/record-review-decision",
                "transition/motion-dq/case/advance-milestone",
                "transition/motion-dq/case/cancel",
                "transition/motion-dq/requirement/apply-evaluation",
                "transition/motion-dq/subject/activate"
            ],
            first.Documents.Select(static document => document.Metadata.DefinitionId.Value));

        for (var index = 0; index < first.Documents.Length; index++)
        {
            var document = first.Documents[index];
            var repeated = second.Documents[index];
            var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(document);

            Assert.Equal(document, repeated);
            Assert.Equal(document.Metadata.Fingerprint, repeated.Metadata.Fingerprint);
            Assert.Equal(
                ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document),
                ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(repeated));

            var validation = TransitionDefinitionDocuments.TryDeserialize(
                Encoding.UTF8.GetString(canonical),
                out var restoredDocument,
                out var restoredDefinition);

            Assert.True(validation.IsValid, Format(validation));
            Assert.NotNull(restoredDocument);
            Assert.NotNull(restoredDefinition);
            Assert.Equal(document, restoredDocument);
            Assert.Equal(document.GetDefinition<CanonicalTransitionDefinition>(), restoredDefinition);
            Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument));
            Assert.Equal(document.Metadata.Fingerprint, restoredDocument.Metadata.Fingerprint);

            var compilation = TransitionStaticCompiler.Compile(restoredDocument);
            Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
            Assert.NotNull(compilation.Plan);
        }
    }

    [Fact]
    public void DurableIdentityAdmissions_RejectEmptyValuesBeforeAuthoritativeMutation()
    {
        var definitions = MotionDqTransitions.Author();
        var resolution = MotionDqProfileCatalog.CreateCaseProfileResolution(caseId: CaseId);
        var resolvePlan = Compile(definitions.ResolveCaseProfile.Document);
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(resolvePlan, resolution with { CaseId = "" }),
            MotionDqTransitionOutcome.CaseIdentityRequired);
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(resolvePlan, resolution with { Profile = resolution.Profile with { SchemaId = "" } }),
            MotionDqTransitionOutcome.SchemaIdentityRequired);
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(resolvePlan, resolution with { Profile = resolution.Profile with { ProfileId = "" } }),
            MotionDqTransitionOutcome.ProfileIdentityRequired);
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(resolvePlan, resolution with { Profile = resolution.Profile with { Revision = "" } }),
            MotionDqTransitionOutcome.ProfileRevisionRequired);

        var prequalificationPlan = Compile(definitions.SubmitPrequalification.Document);
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(
                prequalificationPlan,
                new MotionDqPrequalificationSubmission(
                    CaseId: CaseId,
                    ApplicationId: "",
                    ProfileId: resolution.Profile.ProfileId,
                    ProfileRevision: resolution.Profile.Revision,
                    RequirementGate: MotionDqGateDisposition.Satisfied)),
            MotionDqTransitionOutcome.ApplicationIdentityRequired);

        var reviewPlan = Compile(definitions.RecordReviewDecision.Document);
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(
                reviewPlan,
                new MotionDqReviewDecision(
                    DecisionId: "",
                    CaseId: CaseId,
                    ApplicationId: ApplicationId,
                    Kind: MotionDqReviewDecisionKind.Hire,
                    ReasonCode: "fixture")),
            MotionDqTransitionOutcome.DecisionIdentityRequired);

        var cancelPlan = Compile(definitions.CancelCase.Document);
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(
                cancelPlan,
                new MotionDqCancellation(
                    CancellationId: "",
                    CaseId: CaseId,
                    ReasonCode: "fixture")),
            MotionDqTransitionOutcome.CancellationIdentityRequired);

        var requirementPlan = Compile(definitions.ApplyRequirementEvaluation.Document);
        var requirement = MotionDqProfileCatalog.ScopeRequirement(
            caseId: CaseId,
            requirement: MotionDqProfileCatalog.DrugTestRequirement);
        var receipt = new MotionDqRequirementEvaluationReceipt(
            EvaluationId: "evaluation/identity-boundary",
            Requirement: requirement,
            Disposition: MotionDqGateDisposition.Satisfied,
            EvidenceId: "evidence/identity-boundary");
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(requirementPlan, receipt with { EvaluationId = "" }),
            MotionDqTransitionOutcome.EvaluationIdentityRequired);
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(requirementPlan, receipt with { EvidenceId = "" }),
            MotionDqTransitionOutcome.EvidenceIdentityRequired);

        var activationPlan = Compile(definitions.ActivateSubject.Document);
        AssertAdmissionRejectedWithoutCommitArtifacts(
            Decide(
                activationPlan,
                new MotionDqSubjectActivationAdmission(
                    DecisionId: "",
                    Kind: MotionDqSubjectKind.CarrierOwnerOperator,
                    GateId: MotionDqProfileCatalog.CarrierActivationGate.Id,
                    GateDisposition: MotionDqGateDisposition.Satisfied,
                    ParentCarrierProof: null)),
            MotionDqTransitionOutcome.DecisionIdentityRequired);
    }

    [Fact]
    public void CaseTransitions_EnforceProfileApplicationReviewAndCancellationGates()
    {
        var definitions = MotionDqTransitions.Author();
        var resolvePlan = Compile(definitions.ResolveCaseProfile.Document);
        var resolution = MotionDqProfileCatalog.CreateCaseProfileResolution(caseId: CaseId);

        var resolved = Decide(
            resolvePlan,
            resolution,
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.CaseId), ""),
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.SchemaId), ""),
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.ProfileId), ""),
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.ProfileRevision), ""),
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.ResolvedBlocks), Array.Empty<MotionDqBlockReference>()),
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.ResolvedRequirements), Array.Empty<MotionDqRequirementReference>()),
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.ResolvedEvidenceNeeds), Array.Empty<MotionDqEvidenceNeedReference>()),
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.ResolvedGates), Array.Empty<MotionDqGateReference>()),
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.ResolvedSubjectSlots), Array.Empty<MotionDqSubjectSlot>()),
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.Uninitialized));

        AssertDecision(resolved, TransitionDecisionKind.Applied, MotionDqTransitionOutcome.ProfileResolved);
        AssertPatch(resolved, nameof(MotionDqOnboardingCaseEntity.CaseId), CaseId);
        AssertPatch(resolved, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.ProfileResolved);

        var duplicateResolution = Decide(
            resolvePlan,
            resolution,
            Entry(resolvePlan, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.ProfileResolved));
        AssertDecision(
            duplicateResolution,
            TransitionDecisionKind.AdmissionRejected,
            MotionDqTransitionOutcome.InvalidMilestone);
        Assert.Empty(duplicateResolution.Patch);

        var prequalificationPlan = Compile(definitions.SubmitPrequalification.Document);
        var prequalification = new MotionDqPrequalificationSubmission(
            CaseId: CaseId,
            ApplicationId: ApplicationId,
            ProfileId: MotionDqProfileCatalog.Version1.ProfileId,
            ProfileRevision: MotionDqProfileCatalog.Version1.Revision,
            RequirementGate: MotionDqGateDisposition.Satisfied);
        var acceptedPrequalification = Decide(
            prequalificationPlan,
            prequalification,
            CaseEntry(prequalificationPlan, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.ProfileResolved),
            CaseEntry(prequalificationPlan, nameof(MotionDqOnboardingCaseEntity.CaseId), CaseId),
            CaseEntry(prequalificationPlan, nameof(MotionDqOnboardingCaseEntity.ProfileId), MotionDqProfileCatalog.Version1.ProfileId),
            CaseEntry(prequalificationPlan, nameof(MotionDqOnboardingCaseEntity.ProfileRevision), MotionDqProfileCatalog.Version1.Revision),
            Entry(prequalificationPlan, nameof(MotionDqOnboardingCaseEntity.ApplicationId), ""));
        AssertDecision(
            acceptedPrequalification,
            TransitionDecisionKind.Applied,
            MotionDqTransitionOutcome.PrequalificationSubmitted);
        AssertPatch(acceptedPrequalification, nameof(MotionDqOnboardingCaseEntity.ApplicationId), ApplicationId);

        var rejectedPrequalification = Decide(
            prequalificationPlan,
            prequalification with { RequirementGate = MotionDqGateDisposition.Unsatisfied },
            CaseEntry(prequalificationPlan, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.ProfileResolved),
            CaseEntry(prequalificationPlan, nameof(MotionDqOnboardingCaseEntity.CaseId), CaseId),
            CaseEntry(prequalificationPlan, nameof(MotionDqOnboardingCaseEntity.ProfileId), MotionDqProfileCatalog.Version1.ProfileId),
            CaseEntry(prequalificationPlan, nameof(MotionDqOnboardingCaseEntity.ProfileRevision), MotionDqProfileCatalog.Version1.Revision));
        AssertDecision(
            rejectedPrequalification,
            TransitionDecisionKind.AdmissionRejected,
            MotionDqTransitionOutcome.RequirementsUnsatisfied);

        var fullApplicationPlan = Compile(definitions.SubmitFullApplication.Document);
        var fullApplication = new MotionDqFullApplicationSubmission(
            CaseId: CaseId,
            ApplicationId: ApplicationId,
            RequirementGate: MotionDqGateDisposition.Satisfied);
        var acceptedFullApplication = Decide(
            fullApplicationPlan,
            fullApplication,
            CaseEntry(fullApplicationPlan, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.PrequalificationSubmitted),
            CaseEntry(fullApplicationPlan, nameof(MotionDqOnboardingCaseEntity.CaseId), CaseId),
            CaseEntry(fullApplicationPlan, nameof(MotionDqOnboardingCaseEntity.ApplicationId), ApplicationId));
        AssertDecision(
            acceptedFullApplication,
            TransitionDecisionKind.Applied,
            MotionDqTransitionOutcome.FullApplicationSubmitted);
        AssertPatch(
            acceptedFullApplication,
            nameof(MotionDqOnboardingCaseEntity.Milestone),
            MotionDqCaseMilestone.FullApplicationSubmitted);

        var rejectedFullApplication = Decide(
            fullApplicationPlan,
            fullApplication with { RequirementGate = MotionDqGateDisposition.Unsatisfied },
            CaseEntry(fullApplicationPlan, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.PrequalificationSubmitted),
            CaseEntry(fullApplicationPlan, nameof(MotionDqOnboardingCaseEntity.CaseId), CaseId),
            CaseEntry(fullApplicationPlan, nameof(MotionDqOnboardingCaseEntity.ApplicationId), ApplicationId));
        AssertDecision(
            rejectedFullApplication,
            TransitionDecisionKind.AdmissionRejected,
            MotionDqTransitionOutcome.RequirementsUnsatisfied);

        var reviewPlan = Compile(definitions.RecordReviewDecision.Document);
        AssertReviewDecision(reviewPlan, MotionDqReviewDecisionKind.Hire, MotionDqCaseMilestone.InsuranceTerms);
        AssertReviewDecision(reviewPlan, MotionDqReviewDecisionKind.Hold, MotionDqCaseMilestone.Held);
        AssertReviewDecision(reviewPlan, MotionDqReviewDecisionKind.NotEligible, MotionDqCaseMilestone.NotEligible);

        var cancelPlan = Compile(definitions.CancelCase.Document);
        var cancelled = Decide(
            cancelPlan,
            new MotionDqCancellation(CancellationId: "cancel-181", CaseId: CaseId, ReasonCode: "applicant-request"),
            CaseEntry(cancelPlan, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.InsuranceTerms),
            CaseEntry(cancelPlan, nameof(MotionDqOnboardingCaseEntity.CaseId), CaseId),
            CaseEntry(cancelPlan, nameof(MotionDqOnboardingCaseEntity.CancellationId), ""));
        AssertDecision(cancelled, TransitionDecisionKind.Applied, MotionDqTransitionOutcome.Cancelled);
        AssertPatch(cancelled, nameof(MotionDqOnboardingCaseEntity.CancellationId), "cancel-181");
        AssertPatch(cancelled, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.Cancelled);
    }

    [Fact]
    public void CaseMilestoneTransition_AdmitsOnlyExactCurrentEdgeAndGateEvidence()
    {
        var plan = Compile(MotionDqTransitions.Author().AdvanceCaseMilestone.Document);
        AssertMilestoneEdge(
            plan,
            expected: MotionDqCaseMilestone.InsuranceTerms,
            next: MotionDqCaseMilestone.PostTerms,
            gateId: MotionDqProfileCatalog.InsuranceTermsAcceptedGate.Id);
        AssertMilestoneEdge(
            plan,
            expected: MotionDqCaseMilestone.PostTerms,
            next: MotionDqCaseMilestone.Activation,
            gateId: MotionDqProfileCatalog.PostTermsCompleteGate.Id);
        AssertMilestoneEdge(
            plan,
            expected: MotionDqCaseMilestone.Activation,
            next: MotionDqCaseMilestone.Completed,
            gateId: MotionDqProfileCatalog.ActivationCompleteGate.Id);

        var wrongGate = MilestoneDecision(
            expected: MotionDqCaseMilestone.PostTerms,
            next: MotionDqCaseMilestone.Activation,
            gateId: MotionDqProfileCatalog.InsuranceTermsAcceptedGate.Id);
        AssertDecision(
            DecideMilestone(plan, wrongGate, MotionDqCaseMilestone.PostTerms),
            TransitionDecisionKind.DomainRejected,
            MotionDqTransitionOutcome.MilestoneGateMismatch);

        var unsatisfied = MilestoneDecision(
            expected: MotionDqCaseMilestone.PostTerms,
            next: MotionDqCaseMilestone.Activation,
            gateId: MotionDqProfileCatalog.PostTermsCompleteGate.Id) with
        {
            GateDisposition = MotionDqGateDisposition.Unsatisfied
        };
        AssertDecision(
            DecideMilestone(plan, unsatisfied, MotionDqCaseMilestone.PostTerms),
            TransitionDecisionKind.DomainRejected,
            MotionDqTransitionOutcome.MilestoneGateUnsatisfied);

        var unsupported = MilestoneDecision(
            expected: MotionDqCaseMilestone.InsuranceTerms,
            next: MotionDqCaseMilestone.Completed,
            gateId: MotionDqProfileCatalog.InsuranceTermsAcceptedGate.Id);
        AssertDecision(
            DecideMilestone(plan, unsupported, MotionDqCaseMilestone.InsuranceTerms),
            TransitionDecisionKind.DomainRejected,
            MotionDqTransitionOutcome.UnsupportedMilestoneEdge);

        var stale = MilestoneDecision(
            expected: MotionDqCaseMilestone.InsuranceTerms,
            next: MotionDqCaseMilestone.PostTerms,
            gateId: MotionDqProfileCatalog.InsuranceTermsAcceptedGate.Id);
        AssertDecision(
            DecideMilestone(plan, stale, MotionDqCaseMilestone.PostTerms),
            TransitionDecisionKind.AdmissionRejected,
            MotionDqTransitionOutcome.InvalidMilestone);

        AssertDecision(
            DecideMilestone(
                plan,
                stale with { CaseId = "case/other" },
                MotionDqCaseMilestone.InsuranceTerms),
            TransitionDecisionKind.AdmissionRejected,
            MotionDqTransitionOutcome.CaseReferenceMismatch);
    }

    [Fact]
    public void RequirementTransition_ClassifiesReplayCollisionAndSupersededEvidenceWithoutReplacingAuthority()
    {
        var plan = Compile(MotionDqTransitions.Author().ApplyRequirementEvaluation.Document);
        var requirement = MotionDqProfileCatalog.ScopeRequirement(
            caseId: CaseId,
            requirement: MotionDqProfileCatalog.DrugTestRequirement);
        var acceptedReceipt = new MotionDqRequirementEvaluationReceipt(
            EvaluationId: "evaluation/accepted",
            Requirement: requirement,
            Disposition: MotionDqGateDisposition.Satisfied,
            EvidenceId: "evidence-result/accepted");
        var accepted = Decide(
            plan,
            acceptedReceipt,
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.CaseId), requirement.CaseId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.RequirementId), requirement.RequirementId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Status), MotionDqRequirementStatus.Pending),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.AuthoritativeEvaluationId), ""),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.ObservedEvaluationIds), Array.Empty<string>()),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Evaluations), Array.Empty<MotionDqRequirementEvaluationReceipt>()));
        AssertDecision(
            accepted,
            TransitionDecisionKind.Applied,
            MotionDqTransitionOutcome.RequirementEvaluationAccepted);
        AssertPatch(
            accepted,
            nameof(MotionDqCaseRequirementEntity.AuthoritativeEvaluationId),
            acceptedReceipt.EvaluationId);
        AssertPatch(
            accepted,
            nameof(MotionDqCaseRequirementEntity.ObservedEvaluationIds),
            new[] { acceptedReceipt.EvaluationId });
        AssertPatch(
            accepted,
            nameof(MotionDqCaseRequirementEntity.Status),
            MotionDqRequirementStatus.Satisfied);
        AssertPatch(
            accepted,
            nameof(MotionDqCaseRequirementEntity.Evaluations),
            new[] { acceptedReceipt });

        var duplicate = Decide(
            plan,
            acceptedReceipt,
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.CaseId), requirement.CaseId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.RequirementId), requirement.RequirementId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Status), MotionDqRequirementStatus.Satisfied),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.AuthoritativeEvaluationId), acceptedReceipt.EvaluationId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.ObservedEvaluationIds), new[] { acceptedReceipt.EvaluationId }),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Evaluations), new[] { acceptedReceipt }));
        AssertDecision(
            duplicate,
            TransitionDecisionKind.NoChange,
            MotionDqTransitionOutcome.RequirementEvaluationDuplicate);
        Assert.Empty(duplicate.Patch);

        var supersededReceipt = acceptedReceipt with
        {
            EvaluationId = "evaluation/late",
            EvidenceId = "evidence-result/late"
        };
        var superseded = Decide(
            plan,
            supersededReceipt,
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.CaseId), requirement.CaseId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.RequirementId), requirement.RequirementId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Status), MotionDqRequirementStatus.Satisfied),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.AuthoritativeEvaluationId), acceptedReceipt.EvaluationId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.ObservedEvaluationIds), new[] { acceptedReceipt.EvaluationId }),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Evaluations), new[] { acceptedReceipt }));
        AssertDecision(
            superseded,
            TransitionDecisionKind.Applied,
            MotionDqTransitionOutcome.RequirementEvaluationSuperseded);
        AssertPatch(
            superseded,
            nameof(MotionDqCaseRequirementEntity.ObservedEvaluationIds),
            new[] { acceptedReceipt.EvaluationId, supersededReceipt.EvaluationId });
        AssertPatch(
            superseded,
            nameof(MotionDqCaseRequirementEntity.Evaluations),
            new[] { acceptedReceipt, supersededReceipt });
        Assert.DoesNotContain(
            superseded.Patch,
            static patch => patch.Path.ToString() is nameof(MotionDqCaseRequirementEntity.AuthoritativeEvaluationId)
                or nameof(MotionDqCaseRequirementEntity.Status));

        var observedIds = new[] { acceptedReceipt.EvaluationId, supersededReceipt.EvaluationId };
        var observedEvaluations = new[] { acceptedReceipt, supersededReceipt };
        var replayAfterInterveningEvidence = Decide(
            plan,
            acceptedReceipt,
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.CaseId), requirement.CaseId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.RequirementId), requirement.RequirementId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Status), MotionDqRequirementStatus.Satisfied),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.AuthoritativeEvaluationId), acceptedReceipt.EvaluationId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.ObservedEvaluationIds), observedIds),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Evaluations), observedEvaluations));
        AssertDecision(
            replayAfterInterveningEvidence,
            TransitionDecisionKind.NoChange,
            MotionDqTransitionOutcome.RequirementEvaluationDuplicate);
        Assert.Empty(replayAfterInterveningEvidence.Patch);

        var identityCollision = Decide(
            plan,
            acceptedReceipt with { EvidenceId = "evidence-result/collision" },
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.CaseId), requirement.CaseId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.RequirementId), requirement.RequirementId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Status), MotionDqRequirementStatus.Satisfied),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.AuthoritativeEvaluationId), acceptedReceipt.EvaluationId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.ObservedEvaluationIds), observedIds),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Evaluations), observedEvaluations));
        AssertDecision(
            identityCollision,
            TransitionDecisionKind.DomainRejected,
            MotionDqTransitionOutcome.RequirementEvaluationIdentityConflict);
        Assert.Empty(identityCollision.Patch);

        var otherCaseReceipt = acceptedReceipt with
        {
            Requirement = new(caseId: "case/other", requirementId: requirement.RequirementId)
        };
        var crossCaseCollision = Decide(
            plan,
            otherCaseReceipt,
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.CaseId), requirement.CaseId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.RequirementId), requirement.RequirementId));
        AssertDecision(
            crossCaseCollision,
            TransitionDecisionKind.AdmissionRejected,
            MotionDqTransitionOutcome.CaseReferenceMismatch);

        var invariantFailure = Decide(
            plan,
            acceptedReceipt,
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.CaseId), requirement.CaseId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.RequirementId), requirement.RequirementId),
            RequirementEntry(plan, nameof(MotionDqCaseRequirementEntity.Status), MotionDqRequirementStatus.Satisfied),
            RequirementEntry(
                plan,
                nameof(MotionDqCaseRequirementEntity.AuthoritativeEvaluationId),
                acceptedReceipt.EvaluationId),
            RequirementEntry(
                plan,
                nameof(MotionDqCaseRequirementEntity.ObservedEvaluationIds),
                Array.Empty<string>()),
            RequirementEntry(
                plan,
                nameof(MotionDqCaseRequirementEntity.Evaluations),
                new[] { acceptedReceipt }));

        Assert.Equal(TransitionDecisionKind.InvalidDefinition, invariantFailure.Kind);
        Assert.Contains(
            invariantFailure.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionExecutionDiagnosticCodes.InvariantViolated);
        Assert.Empty(invariantFailure.Patch);
        Assert.Empty(invariantFailure.Emissions);
        Assert.Contains(
            invariantFailure.Evidence.Trace,
            static item => item.Kind == TransitionTraceEventKind.InvariantEvaluated);
    }

    [Fact]
    public void SubjectActivation_IsIndependentAndDriverRequiresCarrierDecisionEvidence()
    {
        var plan = Compile(MotionDqTransitions.Author().ActivateSubject.Document);
        var carrier = new MotionDqSubjectActivationAdmission(
            DecisionId: "activation/carrier/1",
            Kind: MotionDqSubjectKind.CarrierOwnerOperator,
            GateId: MotionDqProfileCatalog.CarrierActivationGate.Id,
            GateDisposition: MotionDqGateDisposition.Satisfied,
            ParentCarrierProof: null);
        var activatedCarrier = DecideActivation(plan, carrier);
        AssertDecision(
            activatedCarrier,
            TransitionDecisionKind.Applied,
            MotionDqTransitionOutcome.SubjectActivated);
        AssertPatch(
            activatedCarrier,
            nameof(MotionDqSubjectActivationEntity.LastActivationDecisionId),
            carrier.DecisionId);
        AssertPatch(activatedCarrier, nameof(MotionDqSubjectActivationEntity.Status), MotionDqActivationStatus.Active);

        (MotionDqSubjectKind Kind, string GateId)[] independentSubjects =
        [
            (MotionDqSubjectKind.Applicant, "gate/activation/applicant"),
            (MotionDqSubjectKind.Truck, "gate/activation/truck"),
            (MotionDqSubjectKind.Trailer, "gate/activation/trailer")
        ];
        foreach (var subject in independentSubjects)
        {
            var admission = new MotionDqSubjectActivationAdmission(
                DecisionId: $"activation/{subject.Kind}/1",
                Kind: subject.Kind,
                GateId: subject.GateId,
                GateDisposition: MotionDqGateDisposition.Satisfied,
                ParentCarrierProof: null);
            AssertDecision(
                DecideActivation(plan, admission),
                TransitionDecisionKind.Applied,
                MotionDqTransitionOutcome.SubjectActivated);
        }

        var driverWithoutCarrier = new MotionDqSubjectActivationAdmission(
            DecisionId: "activation/driver/1",
            Kind: MotionDqSubjectKind.Driver,
            GateId: MotionDqProfileCatalog.DriverActivationGate.Id,
            GateDisposition: MotionDqGateDisposition.Satisfied,
            ParentCarrierProof: null);
        AssertDecision(
            DecideActivation(plan, driverWithoutCarrier),
            TransitionDecisionKind.AdmissionRejected,
            MotionDqTransitionOutcome.DriverCarrierGateRequired);

        var carrierProof = new MotionDqCarrierActivationProof(
            carrierSubject: new(
                ApplicationId: ApplicationId,
                Kind: MotionDqSubjectKind.CarrierOwnerOperator,
                SubjectId: "carrier/181",
                ParentApplicationId: null),
            activationDecisionId: carrier.DecisionId,
            evidenceId: "transition-evidence/carrier/181");
        var wrongCarrierProof = new MotionDqCarrierActivationProof(
            carrierSubject: carrierProof.CarrierSubject with { SubjectId = "carrier/other" },
            activationDecisionId: carrier.DecisionId,
            evidenceId: carrierProof.EvidenceId);
        AssertDecision(
            DecideActivation(
                plan,
                new(
                    DecisionId: "activation/truck/unexpected-carrier-proof",
                    Kind: MotionDqSubjectKind.Truck,
                    GateId: "gate/activation/truck",
                    GateDisposition: MotionDqGateDisposition.Satisfied,
                    ParentCarrierProof: carrierProof)),
            TransitionDecisionKind.AdmissionRejected,
            MotionDqTransitionOutcome.UnexpectedParentCarrierProof);
        AssertDecision(
            DecideActivation(
                plan,
                driverWithoutCarrier with { ParentCarrierProof = wrongCarrierProof },
                requiredParentCarrierProof: carrierProof),
            TransitionDecisionKind.AdmissionRejected,
            MotionDqTransitionOutcome.DriverCarrierGateRequired);

        var driverWithCarrier = driverWithoutCarrier with
        {
            ParentCarrierProof = carrierProof
        };
        var activatedDriver = DecideActivation(
            plan,
            driverWithCarrier,
            requiredParentCarrierProof: carrierProof);
        AssertDecision(
            activatedDriver,
            TransitionDecisionKind.Applied,
            MotionDqTransitionOutcome.SubjectActivated);
        AssertPatch(
            activatedDriver,
            nameof(MotionDqSubjectActivationEntity.AdmittedParentCarrierProof),
            carrierProof);
    }

    static void AssertReviewDecision(
        CompiledTransitionPlan plan,
        MotionDqReviewDecisionKind kind,
        MotionDqCaseMilestone expectedMilestone)
    {
        var decisionId = $"review/{kind}";
        var decision = Decide(
            plan,
            new MotionDqReviewDecision(
                DecisionId: decisionId,
                CaseId: CaseId,
                ApplicationId: ApplicationId,
                Kind: kind,
                ReasonCode: "fixture"),
            CaseEntry(plan, nameof(MotionDqOnboardingCaseEntity.Milestone), MotionDqCaseMilestone.FullApplicationSubmitted),
            CaseEntry(plan, nameof(MotionDqOnboardingCaseEntity.CaseId), CaseId),
            CaseEntry(plan, nameof(MotionDqOnboardingCaseEntity.ApplicationId), ApplicationId),
            CaseEntry(plan, nameof(MotionDqOnboardingCaseEntity.LastReviewDecisionId), ""));

        AssertDecision(
            decision,
            TransitionDecisionKind.Applied,
            MotionDqTransitionOutcome.ReviewDecisionRecorded);
        var expectedCase = kind switch
        {
            MotionDqReviewDecisionKind.Hire => "motion-dq/review/case/hire",
            MotionDqReviewDecisionKind.Hold => "motion-dq/review/case/hold",
            MotionDqReviewDecisionKind.NotEligible => "motion-dq/review/case/not-eligible",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported review decision kind.")
        };
        Assert.Equal(
            [expectedCase],
            decision.Evidence.SelectedCases.Select(static selectedCase => selectedCase.Value));
        AssertPatch(decision, nameof(MotionDqOnboardingCaseEntity.LastReviewDecisionId), decisionId);
        AssertPatch(decision, nameof(MotionDqOnboardingCaseEntity.Milestone), expectedMilestone);
    }

    static void AssertMilestoneEdge(
        CompiledTransitionPlan plan,
        MotionDqCaseMilestone expected,
        MotionDqCaseMilestone next,
        string gateId)
    {
        var admission = MilestoneDecision(expected, next, gateId);
        var decision = DecideMilestone(plan, admission, expected);
        AssertDecision(
            decision,
            TransitionDecisionKind.Applied,
            MotionDqTransitionOutcome.MilestoneAdvanced);
        AssertPatch(decision, nameof(MotionDqOnboardingCaseEntity.LastMilestoneDecisionId), admission.DecisionId);
        AssertPatch(decision, nameof(MotionDqOnboardingCaseEntity.Milestone), next);
    }

    static MotionDqCaseMilestoneAdmission MilestoneDecision(
        MotionDqCaseMilestone expected,
        MotionDqCaseMilestone next,
        string gateId) => new(
        DecisionId: $"milestone/{expected}/{next}",
        CaseId: CaseId,
        ExpectedMilestone: expected,
        NextMilestone: next,
        GateId: gateId,
        GateDisposition: MotionDqGateDisposition.Satisfied);

    static TransitionDecision DecideMilestone(
        CompiledTransitionPlan plan,
        MotionDqCaseMilestoneAdmission admission,
        MotionDqCaseMilestone current) => Decide(
        plan,
        admission,
        CaseEntry(plan, nameof(MotionDqOnboardingCaseEntity.CaseId), CaseId),
        CaseEntry(plan, nameof(MotionDqOnboardingCaseEntity.Milestone), current),
        CaseEntry(plan, nameof(MotionDqOnboardingCaseEntity.LastMilestoneDecisionId), ""));

    static TransitionDecision DecideActivation(
        CompiledTransitionPlan plan,
        MotionDqSubjectActivationAdmission admission,
        MotionDqCarrierActivationProof? requiredParentCarrierProof = null) => Decide(
        plan,
        admission,
        Entry(plan, nameof(MotionDqSubjectActivationEntity.Kind), admission.Kind),
        Entry(plan, nameof(MotionDqSubjectActivationEntity.ActivationGateId), admission.GateId),
        Entry(plan, nameof(MotionDqSubjectActivationEntity.Status), MotionDqActivationStatus.Pending),
        Entry(plan, nameof(MotionDqSubjectActivationEntity.LastActivationDecisionId), ""),
        Entry(plan, nameof(MotionDqSubjectActivationEntity.RequiredParentCarrierProof), requiredParentCarrierProof),
        Entry(plan, nameof(MotionDqSubjectActivationEntity.AdmittedParentCarrierProof), value: null));

    static TransitionDecision Decide<TInput>(
        CompiledTransitionPlan plan,
        TInput input,
        params TransitionObservationEntry[] observations)
    {
        var activation = new ActivationId($"ari-200/{plan.Document.Metadata.DefinitionId.Value}");
        var value = PortableValue.Concrete(
            plan.Definition.Input,
            ObservationValue.FromObject(input));
        var reference = TransitionReferenceInterpreter.DecideSparse(
            plan,
            activation,
            value,
            observations);
        var replay = TransitionReferenceInterpreter.DecideSparse(
            plan,
            activation,
            value,
            observations);

        Assert.Equivalent(reference, replay, strict: true);
        return reference;
    }

    static CompiledTransitionPlan Compile(ExecutionDefinitionDocument document)
    {
        var compilation = TransitionStaticCompiler.Compile(document);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        return Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
    }

    static TransitionObservationEntry CaseEntry(
        CompiledTransitionPlan plan,
        string path,
        object value) => Entry(plan, path, value);

    static TransitionObservationEntry RequirementEntry(
        CompiledTransitionPlan plan,
        string path,
        object value) => Entry(plan, path, value);

    static TransitionObservationEntry Entry(
        CompiledTransitionPlan plan,
        string path,
        object? value)
    {
        var fieldPath = FieldPath.FromField(path);
        var field = Assert.Single(
            Assert.IsType<ObjectTypeRef>(plan.Definition.Observation.Type).Fields,
            candidate => candidate.Name == path);
        var contract = new ValueContract(
            field.Type,
            cardinality: field.Cardinality,
            presence: field.Presence,
            nullability: field.Nullability);
        var portable = value is null
            ? PortableValue.Null(contract)
            : PortableValue.Concrete(contract, ObservationValue.FromObject(value));
        return new(TransitionObservationAccess.At(fieldPath), portable);
    }

    static void AssertDecision(
        TransitionDecision decision,
        TransitionDecisionKind expectedKind,
        MotionDqTransitionOutcome expectedOutcome)
    {
        Assert.True(
            decision.Kind == expectedKind,
            $"Expected '{expectedKind}' but received '{decision.Kind}'.{Environment.NewLine}"
            + string.Join(
                Environment.NewLine,
                decision.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}:{diagnostic.Location}:{diagnostic.Message}")));
        Assert.Equal(expectedOutcome.ToString(), decision.Outcome?.Value?.String);
    }

    static void AssertAdmissionRejectedWithoutCommitArtifacts(
        TransitionDecision decision,
        MotionDqTransitionOutcome expectedOutcome)
    {
        AssertDecision(decision, TransitionDecisionKind.AdmissionRejected, expectedOutcome);
        Assert.Empty(decision.Patch);
        Assert.Empty(decision.Emissions);
    }

    static void AssertPatch(TransitionDecision decision, string path, object expectedValue)
    {
        var patch = Assert.Single(decision.Patch, candidate => candidate.Path.ToString() == path);
        Assert.Equal(ObservationValue.FromObject(expectedValue), patch.After.Value);
    }

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Severity}:{diagnostic.Code}:{diagnostic.Location}:{diagnostic.Message}"));

    const string CaseId = "case-181";
    const string ApplicationId = "application-181";
}
