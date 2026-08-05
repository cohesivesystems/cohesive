using Cohesive.Execution;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;

namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Human-facing expression authoring for the canonical Motion DQ onboarding Process.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class MotionDqProcessDefinition
{
    static readonly MotionDqTransitionDefinitions Transitions = MotionDqTransitions.Author();
    static readonly MotionDqInteractionContracts Interactions = MotionDqInteractionContracts.Version1;

    static async ProcessTask<MotionDqOnboardingOutcome> Run(
        ProcessContext process,
        MotionDqOnboardingInput input)
    {
        var caseId = input.Prequalification.CaseId;
        var applicationId = input.FullApplication.ApplicationId;

        var prequalification = await process.Transition<MotionDqTransitionOutcome>(
            transition: Transitions.SubmitPrequalification.Reference,
            subject: caseId,
            input: input.Prequalification,
            id: MotionDqProcess.Identities.SubmitPrequalification,
            nextRole: "completed",
            outputRole: "outcome");

        async ProcessTask PrequalificationAccepted()
        {
        }

        async ProcessTask PrequalificationRejected()
        {
            await process.Terminate(
                MotionDqOnboardingOutcome.CoordinationRejected,
                MotionDqProcess.Identities.CoordinationRejected);
        }

        await process.Match(
            value: prequalification,
            selection: CaseSelection.OrderedFirstMatch,
            completeness: BranchCompleteness.Fallback,
            cases:
            [
                process.Case(
                    MotionDqTransitionOutcome.PrequalificationSubmitted,
                    PrequalificationAccepted,
                    MotionDqProcess.Accepted(MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitPrequalification)),
                    role: "accepted",
                    edgeOwner: MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitPrequalification))
            ],
            fallback: PrequalificationRejected,
            id: MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitPrequalification),
            fallbackId: MotionDqProcess.Rejected(MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitPrequalification)),
            fallbackRole: "rejected",
            fallbackEdgeOwner: MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitPrequalification));

        var application = await process.Transition<MotionDqTransitionOutcome>(
            transition: Transitions.SubmitFullApplication.Reference,
            subject: caseId,
            input: input.FullApplication,
            id: MotionDqProcess.Identities.SubmitFullApplication,
            nextRole: "completed",
            outputRole: "outcome");

        async ProcessTask ApplicationAccepted()
        {
        }

        async ProcessTask ApplicationRejected()
        {
            await process.Terminate(
                MotionDqOnboardingOutcome.CoordinationRejected,
                MotionDqProcess.Identities.CoordinationRejected);
        }

        await process.Match(
            value: application,
            selection: CaseSelection.OrderedFirstMatch,
            completeness: BranchCompleteness.Fallback,
            cases:
            [
                process.Case(
                    MotionDqTransitionOutcome.FullApplicationSubmitted,
                    ApplicationAccepted,
                    MotionDqProcess.Accepted(MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitFullApplication)),
                    role: "accepted",
                    edgeOwner: MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitFullApplication))
            ],
            fallback: ApplicationRejected,
            id: MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitFullApplication),
            fallbackId: MotionDqProcess.Rejected(MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitFullApplication)),
            fallbackRole: "rejected",
            fallbackEdgeOwner: MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.SubmitFullApplication));

        async ProcessTask ReviewTaskValid()
        {
        }

        async ProcessTask ReviewTaskInvalid()
        {
            await process.Terminate(
                MotionDqOnboardingOutcome.CoordinationRejected,
                MotionDqProcess.Identities.CoordinationRejected);
        }

        await process.Choice(
            selection: CaseSelection.OrderedFirstMatch,
            completeness: BranchCompleteness.Fallback,
            cases:
            [
                process.When(
                    input.ReviewTask.CaseId == caseId
                    && input.ReviewTask.ApplicationId == applicationId,
                    ReviewTaskValid,
                    MotionDqProcess.Accepted(MotionDqProcess.Identities.ValidateReviewTask),
                    role: "accepted",
                    edgeOwner: MotionDqProcess.Identities.ValidateReviewTask)
            ],
            fallback: ReviewTaskInvalid,
            id: MotionDqProcess.Identities.ValidateReviewTask,
            fallbackId: MotionDqProcess.Rejected(MotionDqProcess.Identities.ValidateReviewTask),
            fallbackRole: "rejected",
            fallbackEdgeOwner: MotionDqProcess.Identities.ValidateReviewTask);

        async ProcessTask ReviewTaskCreated(MotionDqReviewTaskReference created)
        {
        }

        async ProcessTask ReviewTaskCreationFailed()
        {
            await process.Terminate(
                MotionDqOnboardingOutcome.ReviewTaskFailed,
                MotionDqProcess.Identities.ReviewTaskFailed);
        }

        await process.Effect(
            contract: Interactions.ReviewTaskRequest,
            input: input.ReviewTask,
            outcomes:
            [
                process.Outcome<MotionDqReviewTaskReference>(
                    MotionDqInteractionContracts.ReviewTaskCreatedOutcome,
                    ReviewTaskCreated,
                    id: MotionDqProcess.Identities.ReviewTaskCreatedOutcome,
                    role: "created",
                    edgeOwner: MotionDqProcess.Identities.CreateReviewTask,
                    outputRole: "created",
                    outputOwner: MotionDqProcess.Identities.CreateReviewTask),
                process.Outcome(
                    MotionDqInteractionContracts.ReviewTaskFailedOutcome,
                    ReviewTaskCreationFailed,
                    id: MotionDqProcess.Identities.ReviewTaskFailedOutcome,
                    role: "failed",
                    edgeOwner: MotionDqProcess.Identities.CreateReviewTask)
            ],
            id: MotionDqProcess.Identities.CreateReviewTask);

        async ProcessTask ReviewCancelled(MotionDqCancellation cancellation)
        {
            var cancelled = await process.Transition<MotionDqTransitionOutcome>(
                transition: Transitions.CancelCase.Reference,
                subject: caseId,
                input: cancellation,
                id: MotionDqProcess.Identities.RecordCancellation,
                nextRole: "completed",
                outputRole: "outcome");

            async ProcessTask CancellationAccepted()
            {
                await process.Succeed(
                    MotionDqOnboardingOutcome.Cancelled,
                    MotionDqProcess.Identities.Cancelled);
            }

            async ProcessTask CancellationRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var required = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.RecordCancellation);
            await process.Match(
                cancelled,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [
                    process.Case(
                        MotionDqTransitionOutcome.Cancelled,
                        CancellationAccepted,
                        MotionDqProcess.Accepted(required),
                        role: "accepted",
                        edgeOwner: required)
                ],
                CancellationRejected,
                required,
                MotionDqProcess.Rejected(required),
                "rejected",
                required);
        }

        async ProcessTask ReviewNotEligible(MotionDqReviewDecision decision)
        {
            var recorded = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.RecordReviewDecision.Reference,
                caseId,
                decision,
                MotionDqProcess.Identities.RecordNotEligible,
                "completed",
                "outcome");

            async ProcessTask DecisionAccepted()
            {
                await process.Succeed(
                    MotionDqOnboardingOutcome.NotEligible,
                    MotionDqProcess.Identities.NotEligible);
            }

            async ProcessTask DecisionRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var required = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.RecordNotEligible);
            await process.Match(
                recorded,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.ReviewDecisionRecorded, DecisionAccepted,
                    MotionDqProcess.Accepted(required), "accepted", required)],
                DecisionRejected,
                required,
                MotionDqProcess.Rejected(required),
                "rejected",
                required);
        }

        async ProcessTask ReviewHire(MotionDqReviewDecision decision)
        {
            var recorded = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.RecordReviewDecision.Reference,
                caseId,
                decision,
                MotionDqProcess.Identities.RecordHire,
                "completed",
                "outcome");

            async ProcessTask DecisionAccepted()
            {
            }

            async ProcessTask DecisionRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var required = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.RecordHire);
            await process.Match(
                recorded,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.ReviewDecisionRecorded, DecisionAccepted,
                    MotionDqProcess.Accepted(required), "accepted", required)],
                DecisionRejected,
                required,
                MotionDqProcess.Rejected(required),
                "rejected",
                required);
        }

        async ProcessTask ReviewHold(MotionDqReviewDecision decision)
        {
            var recorded = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.RecordReviewDecision.Reference,
                caseId,
                decision,
                MotionDqProcess.Identities.RecordHold,
                "completed",
                "outcome");

            async ProcessTask DecisionAccepted()
            {
                await process.ContinueAt(MotionDqProcess.Identities.AwaitReview);
            }

            async ProcessTask DecisionRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var required = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.RecordHold);
            await process.Match(
                recorded,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.ReviewDecisionRecorded, DecisionAccepted,
                    MotionDqProcess.Accepted(required), "accepted", required)],
                DecisionRejected,
                required,
                MotionDqProcess.Rejected(required),
                "rejected",
                required);
        }

        async ProcessTask ReviewTimedOut()
        {
            var cancelled = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.CancelCase.Reference,
                caseId,
                input.ReviewTimeoutCancellation,
                MotionDqProcess.Identities.RecordReviewTimeout,
                "completed",
                "outcome");

            async ProcessTask TimeoutAccepted()
            {
                await process.Succeed(
                    MotionDqOnboardingOutcome.ReviewTimedOut,
                    MotionDqProcess.Identities.ReviewTimedOut);
            }

            async ProcessTask TimeoutRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var required = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.RecordReviewTimeout);
            await process.Match(
                cancelled,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.Cancelled, TimeoutAccepted,
                    MotionDqProcess.Accepted(required), "accepted", required)],
                TimeoutRejected,
                required,
                MotionDqProcess.Rejected(required),
                "rejected",
                required);
        }

        await process.AwaitMatch(
            clauses:
            [
                process.Signal<MotionDqCancellation>(
                    Interactions.CaseCancellationSignal,
                    ReviewCancelled,
                    100,
                    cancellation => cancellation.CaseId == caseId,
                    MotionDqProcess.Identities.ReviewCancelledClause,
                    "cancelled",
                    MotionDqProcess.Identities.AwaitReview,
                    "cancelled",
                    MotionDqProcess.Identities.AwaitReview),
                process.Signal<MotionDqReviewDecision>(
                    Interactions.ReviewDecisionSignal,
                    ReviewNotEligible,
                    90,
                    decision => decision.Kind == MotionDqReviewDecisionKind.NotEligible
                        && decision.CaseId == caseId
                        && decision.ApplicationId == applicationId,
                    MotionDqProcess.Identities.ReviewNotEligibleClause,
                    "noteligible",
                    MotionDqProcess.Identities.AwaitReview,
                    "noteligible",
                    MotionDqProcess.Identities.AwaitReview),
                process.Signal<MotionDqReviewDecision>(
                    Interactions.ReviewDecisionSignal,
                    ReviewHire,
                    80,
                    decision => decision.Kind == MotionDqReviewDecisionKind.Hire
                        && decision.CaseId == caseId
                        && decision.ApplicationId == applicationId,
                    MotionDqProcess.Identities.ReviewHireClause,
                    "hire",
                    MotionDqProcess.Identities.AwaitReview,
                    "hire",
                    MotionDqProcess.Identities.AwaitReview),
                process.Signal<MotionDqReviewDecision>(
                    Interactions.ReviewDecisionSignal,
                    ReviewHold,
                    70,
                    decision => decision.Kind == MotionDqReviewDecisionKind.Hold
                        && decision.CaseId == caseId
                        && decision.ApplicationId == applicationId,
                    MotionDqProcess.Identities.ReviewHoldClause,
                    "hold",
                    MotionDqProcess.Identities.AwaitReview,
                    "hold",
                    MotionDqProcess.Identities.AwaitReview),
                process.Deadline(
                    input.ReviewDueAtUtc,
                    ReviewTimedOut,
                    0,
                    MotionDqProcess.Identities.ReviewTimedOutClause,
                    "timedout",
                    MotionDqProcess.Identities.AwaitReview)
            ],
            arbitration: ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
            lateInput: ProcessAwaitInputDisposition.Observe,
            staleInput: ProcessAwaitInputDisposition.Reject,
            duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
            missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
            retentionHorizon: TimeSpan.FromDays(30),
            id: MotionDqProcess.Identities.AwaitReview);

        async ProcessTask InsuranceRequestValid()
        {
        }

        async ProcessTask InsuranceRequestInvalid()
        {
            await process.Terminate(
                MotionDqOnboardingOutcome.CoordinationRejected,
                MotionDqProcess.Identities.CoordinationRejected);
        }

        await process.Choice(
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            [
                process.When(
                    input.InsuranceTerms.CaseId == caseId
                    && input.InsuranceTerms.TermsRevision != "",
                    InsuranceRequestValid,
                    MotionDqProcess.Accepted(MotionDqProcess.Identities.ValidateInsuranceRequest),
                    "accepted",
                    MotionDqProcess.Identities.ValidateInsuranceRequest)
            ],
            InsuranceRequestInvalid,
            MotionDqProcess.Identities.ValidateInsuranceRequest,
            MotionDqProcess.Rejected(MotionDqProcess.Identities.ValidateInsuranceRequest),
            "rejected",
            MotionDqProcess.Identities.ValidateInsuranceRequest);

        async ProcessTask InsuranceAccepted(MotionDqInsuranceTermsResult result)
        {
            async ProcessTask AcceptedResultValid()
            {
            }

            async ProcessTask AcceptedResultInvalid()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            await process.Choice(
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [
                    process.When(
                        result.CaseId == caseId
                        && result.CaseId == input.InsuranceTerms.CaseId
                        && result.TermsRevision == input.InsuranceTerms.TermsRevision
                        && result.Evaluation.Requirement.CaseId == caseId
                        && result.Evaluation.Requirement.RequirementId == MotionDqVocabulary.Requirements.InsuranceTerms
                        && result.Evaluation.Disposition == MotionDqGateDisposition.Satisfied
                        && result.Evaluation.EvaluationId != ""
                        && result.Evaluation.EvidenceId != "",
                        AcceptedResultValid,
                        MotionDqProcess.Accepted(MotionDqProcess.Identities.ValidateAcceptedInsuranceTerms),
                        "accepted",
                        MotionDqProcess.Identities.ValidateAcceptedInsuranceTerms)
                ],
                AcceptedResultInvalid,
                MotionDqProcess.Identities.ValidateAcceptedInsuranceTerms,
                MotionDqProcess.Rejected(MotionDqProcess.Identities.ValidateAcceptedInsuranceTerms),
                "rejected",
                MotionDqProcess.Identities.ValidateAcceptedInsuranceTerms);

            var applied = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.ApplyRequirementEvaluation.Reference,
                result.Evaluation.Requirement,
                result.Evaluation,
                MotionDqProcess.Identities.ApplyAcceptedInsuranceTerms,
                "completed",
                "outcome");

            async ProcessTask AppliedAccepted()
            {
            }

            async ProcessTask AppliedRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var applyRequired = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.ApplyAcceptedInsuranceTerms);
            await process.Match(
                applied,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.RequirementEvaluationAccepted, AppliedAccepted,
                    MotionDqProcess.Accepted(applyRequired), "accepted", applyRequired)],
                AppliedRejected,
                applyRequired,
                MotionDqProcess.Rejected(applyRequired),
                "rejected",
                applyRequired);

            var termsAdvanced = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.AdvanceCaseMilestone.Reference,
                caseId,
                input.InsuranceTermsAdmission,
                MotionDqProcess.Identities.AdvanceInsuranceTermsMilestone,
                "completed",
                "outcome");

            async ProcessTask TermsAdvanced()
            {
            }

            async ProcessTask TermsAdvanceRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var termsRequired = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.AdvanceInsuranceTermsMilestone);
            await process.Match(
                termsAdvanced,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.MilestoneAdvanced, TermsAdvanced,
                    MotionDqProcess.Accepted(termsRequired), "accepted", termsRequired)],
                TermsAdvanceRejected,
                termsRequired,
                MotionDqProcess.Rejected(termsRequired),
                "rejected",
                termsRequired);

            var postTerms = input.PostTerms;
            async ProcessTask PostTermsValid()
            {
            }

            async ProcessTask PostTermsInvalid()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            await process.Choice(
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [
                    process.When(
                        postTerms.DrugTest.Requirement.CaseId == caseId
                        && postTerms.DrugTest.Requirement.RequirementId == MotionDqVocabulary.Requirements.DrugTest
                        && postTerms.DrugTest.EvidenceNeedId == MotionDqVocabulary.EvidenceNeeds.DrugTest
                        && (postTerms.Clearinghouse.Requirement.CaseId == caseId
                        && postTerms.Clearinghouse.Requirement.RequirementId == MotionDqVocabulary.Requirements.Clearinghouse
                        && postTerms.Clearinghouse.EvidenceNeedId == MotionDqVocabulary.EvidenceNeeds.Clearinghouse)
                        && (postTerms.Vehicle.Requirement.CaseId == caseId
                        && postTerms.Vehicle.Requirement.RequirementId == MotionDqVocabulary.Requirements.Vehicle
                        && postTerms.Vehicle.EvidenceNeedId == MotionDqVocabulary.EvidenceNeeds.Vehicle)
                        && (postTerms.Business.Requirement.CaseId == caseId
                        && postTerms.Business.Requirement.RequirementId == MotionDqVocabulary.Requirements.Business
                        && postTerms.Business.EvidenceNeedId == MotionDqVocabulary.EvidenceNeeds.Business)
                        && (postTerms.Equipment.Requirement.CaseId == caseId
                        && postTerms.Equipment.Requirement.RequirementId == MotionDqVocabulary.Requirements.Equipment
                        && postTerms.Equipment.EvidenceNeedId == MotionDqVocabulary.EvidenceNeeds.Equipment)
                        && (postTerms.Permit.Requirement.CaseId == caseId
                        && postTerms.Permit.Requirement.RequirementId == MotionDqVocabulary.Requirements.Permit
                        && postTerms.Permit.EvidenceNeedId == MotionDqVocabulary.EvidenceNeeds.Permit)
                        && (postTerms.RandomPool.Requirement.CaseId == caseId
                        && postTerms.RandomPool.Requirement.RequirementId == MotionDqVocabulary.Requirements.RandomPool
                        && postTerms.RandomPool.EvidenceNeedId == MotionDqVocabulary.EvidenceNeeds.RandomPool),
                        PostTermsValid,
                        MotionDqProcess.Accepted(MotionDqProcess.Identities.ValidatePostTerms),
                        "accepted",
                        MotionDqProcess.Identities.ValidatePostTerms)
                ],
                PostTermsInvalid,
                MotionDqProcess.Identities.ValidatePostTerms,
                MotionDqProcess.Rejected(MotionDqProcess.Identities.ValidatePostTerms),
                "rejected",
                MotionDqProcess.Identities.ValidatePostTerms);

            async ProcessTask DrugTestFulfillment()
            {
                var request = postTerms.DrugTest;

                async ProcessTask VendorFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "apply/vendor-fulfilled"),
                        "joined");
                    await process.ContinueAt(MotionDqProcess.Identities.PostTermsJoin);
                }

                async ProcessTask VendorFailed()
                {
                }

                async ProcessTask VendorTimedOut()
                {
                }

                async ProcessTask VendorCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            VendorFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            VendorFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            VendorTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            VendorCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "vendor"));

                async ProcessTask ManualFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "apply/manual-fulfilled"),
                        "joined");
                }

                async ProcessTask ManualFailed()
                {
                }

                async ProcessTask ManualTimedOut()
                {
                }

                async ProcessTask ManualCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            ManualFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            ManualFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            ManualTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            ManualCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "manual"));
            }

            async ProcessTask ClearinghouseFulfillment()
            {
                var request = postTerms.Clearinghouse;

                async ProcessTask VendorFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "apply/vendor-fulfilled"),
                        "joined");
                    await process.ContinueAt(MotionDqProcess.Identities.PostTermsJoin);
                }

                async ProcessTask VendorFailed()
                {
                }

                async ProcessTask VendorTimedOut()
                {
                }

                async ProcessTask VendorCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            VendorFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            VendorFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            VendorTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            VendorCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "vendor"));

                async ProcessTask ManualFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "apply/manual-fulfilled"),
                        "joined");
                }

                async ProcessTask ManualFailed()
                {
                }

                async ProcessTask ManualTimedOut()
                {
                }

                async ProcessTask ManualCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            ManualFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            ManualFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            ManualTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            ManualCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "manual"));
            }

            async ProcessTask VehicleFulfillment()
            {
                var request = postTerms.Vehicle;

                async ProcessTask VendorFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "apply/vendor-fulfilled"),
                        "joined");
                    await process.ContinueAt(MotionDqProcess.Identities.PostTermsJoin);
                }

                async ProcessTask VendorFailed()
                {
                }

                async ProcessTask VendorTimedOut()
                {
                }

                async ProcessTask VendorCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            VendorFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            VendorFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            VendorTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            VendorCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "vendor"));

                async ProcessTask ManualFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "apply/manual-fulfilled"),
                        "joined");
                }

                async ProcessTask ManualFailed()
                {
                }

                async ProcessTask ManualTimedOut()
                {
                }

                async ProcessTask ManualCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            ManualFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            ManualFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            ManualTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            ManualCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "manual"));
            }

            async ProcessTask BusinessFulfillment()
            {
                var request = postTerms.Business;

                async ProcessTask VendorFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "apply/vendor-fulfilled"),
                        "joined");
                    await process.ContinueAt(MotionDqProcess.Identities.PostTermsJoin);
                }

                async ProcessTask VendorFailed()
                {
                }

                async ProcessTask VendorTimedOut()
                {
                }

                async ProcessTask VendorCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            VendorFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            VendorFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            VendorTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            VendorCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "vendor"));

                async ProcessTask ManualFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "apply/manual-fulfilled"),
                        "joined");
                }

                async ProcessTask ManualFailed()
                {
                }

                async ProcessTask ManualTimedOut()
                {
                }

                async ProcessTask ManualCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            ManualFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            ManualFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            ManualTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            ManualCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "manual"));
            }

            async ProcessTask EquipmentFulfillment()
            {
                var request = postTerms.Equipment;

                async ProcessTask VendorFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "apply/vendor-fulfilled"),
                        "joined");
                    await process.ContinueAt(MotionDqProcess.Identities.PostTermsJoin);
                }

                async ProcessTask VendorFailed()
                {
                }

                async ProcessTask VendorTimedOut()
                {
                }

                async ProcessTask VendorCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            VendorFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            VendorFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            VendorTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            VendorCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "vendor"));

                async ProcessTask ManualFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "apply/manual-fulfilled"),
                        "joined");
                }

                async ProcessTask ManualFailed()
                {
                }

                async ProcessTask ManualTimedOut()
                {
                }

                async ProcessTask ManualCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            ManualFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            ManualFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            ManualTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            ManualCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "manual"));
            }

            async ProcessTask PermitFulfillment()
            {
                var request = postTerms.Permit;

                async ProcessTask VendorFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "apply/vendor-fulfilled"),
                        "joined");
                    await process.ContinueAt(MotionDqProcess.Identities.PostTermsJoin);
                }

                async ProcessTask VendorFailed()
                {
                }

                async ProcessTask VendorTimedOut()
                {
                }

                async ProcessTask VendorCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            VendorFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            VendorFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            VendorTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            VendorCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "vendor"));

                async ProcessTask ManualFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "apply/manual-fulfilled"),
                        "joined");
                }

                async ProcessTask ManualFailed()
                {
                }

                async ProcessTask ManualTimedOut()
                {
                }

                async ProcessTask ManualCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            ManualFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            ManualFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            ManualTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            ManualCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "manual"));
            }

            async ProcessTask RandomPoolFulfillment()
            {
                var request = postTerms.RandomPool;

                async ProcessTask VendorFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "apply/vendor-fulfilled"),
                        "joined");
                    await process.ContinueAt(MotionDqProcess.Identities.PostTermsJoin);
                }

                async ProcessTask VendorFailed()
                {
                }

                async ProcessTask VendorTimedOut()
                {
                }

                async ProcessTask VendorCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            VendorFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            VendorFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            VendorTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            VendorCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "vendor"));

                async ProcessTask ManualFulfilled(MotionDqRequirementEvaluationReceipt receipt)
                {
                    await process.Transition(
                        Transitions.ApplyRequirementEvaluation.Reference,
                        request.Requirement,
                        receipt,
                        MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "apply/manual-fulfilled"),
                        "joined");
                }

                async ProcessTask ManualFailed()
                {
                }

                async ProcessTask ManualTimedOut()
                {
                }

                async ProcessTask ManualCancelled()
                {
                }

                await process.Effect(
                    Interactions.FulfillRequirementRequest,
                    request,
                    [
                        process.Outcome<MotionDqRequirementEvaluationReceipt>(
                            MotionDqInteractionContracts.RequirementFulfilledOutcome,
                            ManualFulfilled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual/fulfilled"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual"),
                            "fulfilled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                            ManualFailed,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual/provider-failed"),
                            "failed",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                            ManualTimedOut,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual/provider-timed-out"),
                            "timedout",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual")),
                        process.Outcome(
                            MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                            ManualCancelled,
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual/cancelled"),
                            "cancelled",
                            MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual"))
                    ],
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "manual"));
            }

            await process.ForkJoin(
                MotionDqProcess.Identities.PostTermsFork,
                MotionDqProcess.Identities.PostTermsJoin,
                "complete",
                process.Branch(
                    DrugTestFulfillment(),
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.DrugTest, "branch"),
                    role: MotionDqProcess.LastSegment(MotionDqVocabulary.Requirements.DrugTest),
                    edgeOwner: MotionDqProcess.Identities.PostTermsFork),
                process.Branch(
                    ClearinghouseFulfillment(),
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Clearinghouse, "branch"),
                    role: MotionDqProcess.LastSegment(MotionDqVocabulary.Requirements.Clearinghouse),
                    edgeOwner: MotionDqProcess.Identities.PostTermsFork),
                process.Branch(
                    VehicleFulfillment(),
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Vehicle, "branch"),
                    role: MotionDqProcess.LastSegment(MotionDqVocabulary.Requirements.Vehicle),
                    edgeOwner: MotionDqProcess.Identities.PostTermsFork),
                process.Branch(
                    BusinessFulfillment(),
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Business, "branch"),
                    role: MotionDqProcess.LastSegment(MotionDqVocabulary.Requirements.Business),
                    edgeOwner: MotionDqProcess.Identities.PostTermsFork),
                process.Branch(
                    EquipmentFulfillment(),
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Equipment, "branch"),
                    role: MotionDqProcess.LastSegment(MotionDqVocabulary.Requirements.Equipment),
                    edgeOwner: MotionDqProcess.Identities.PostTermsFork),
                process.Branch(
                    PermitFulfillment(),
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.Permit, "branch"),
                    role: MotionDqProcess.LastSegment(MotionDqVocabulary.Requirements.Permit),
                    edgeOwner: MotionDqProcess.Identities.PostTermsFork),
                process.Branch(
                    RandomPoolFulfillment(),
                    MotionDqProcess.PostTerms(MotionDqVocabulary.Requirements.RandomPool, "branch"),
                    role: MotionDqProcess.LastSegment(MotionDqVocabulary.Requirements.RandomPool),
                    edgeOwner: MotionDqProcess.Identities.PostTermsFork));

            var postTermsAdvanced = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.AdvanceCaseMilestone.Reference,
                caseId,
                input.PostTermsAdmission,
                MotionDqProcess.Identities.AdvancePostTermsMilestone,
                "completed",
                "outcome");

            async ProcessTask PostTermsAdvanced()
            {
            }

            async ProcessTask PostTermsAdvanceRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var postTermsRequired = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.AdvancePostTermsMilestone);
            await process.Match(
                postTermsAdvanced,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.MilestoneAdvanced, PostTermsAdvanced,
                    MotionDqProcess.Accepted(postTermsRequired), "accepted", postTermsRequired)],
                PostTermsAdvanceRejected,
                postTermsRequired,
                MotionDqProcess.Rejected(postTermsRequired),
                "rejected",
                postTermsRequired);

            var activations = input.Activations;
            var carrierProof = activations.Driver.Admission.ParentCarrierProof;

            async ProcessTask ActivationsValid()
            {
            }

            async ProcessTask ActivationsInvalid()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            await process.Choice(
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [
                    process.When(
                        activations.Applicant.Subject.ApplicationId == applicationId
                        && activations.Applicant.Subject.Kind == MotionDqSubjectKind.Applicant
                        && activations.Applicant.Subject.SubjectId != ""
                        && activations.Applicant.Admission.Kind == MotionDqSubjectKind.Applicant
                        && activations.Applicant.Admission.GateId == MotionDqVocabulary.Gates.ApplicantActivation
                        && activations.Applicant.Admission.GateDisposition == MotionDqGateDisposition.Satisfied
                        && activations.Applicant.Admission.DecisionId != ""
                        && activations.Applicant.Subject.ParentApplicationId == null
                        && activations.Applicant.Admission.ParentCarrierProof == null
                        && (activations.CarrierOwnerOperator.Subject.ApplicationId == applicationId
                        && activations.CarrierOwnerOperator.Subject.Kind == MotionDqSubjectKind.CarrierOwnerOperator
                        && activations.CarrierOwnerOperator.Subject.SubjectId != ""
                        && activations.CarrierOwnerOperator.Admission.Kind == MotionDqSubjectKind.CarrierOwnerOperator
                        && activations.CarrierOwnerOperator.Admission.GateId == MotionDqVocabulary.Gates.CarrierActivation
                        && activations.CarrierOwnerOperator.Admission.GateDisposition == MotionDqGateDisposition.Satisfied
                        && activations.CarrierOwnerOperator.Admission.DecisionId != ""
                        && activations.CarrierOwnerOperator.Subject.ParentApplicationId == null
                        && activations.CarrierOwnerOperator.Admission.ParentCarrierProof == null)
                        && (activations.Driver.Subject.ApplicationId == applicationId
                        && activations.Driver.Subject.Kind == MotionDqSubjectKind.Driver
                        && activations.Driver.Subject.SubjectId != ""
                        && activations.Driver.Admission.Kind == MotionDqSubjectKind.Driver
                        && activations.Driver.Admission.GateId == MotionDqVocabulary.Gates.DriverActivation
                        && activations.Driver.Admission.GateDisposition == MotionDqGateDisposition.Satisfied
                        && activations.Driver.Admission.DecisionId != "")
                        && (activations.Truck.Subject.ApplicationId == applicationId
                        && activations.Truck.Subject.Kind == MotionDqSubjectKind.Truck
                        && activations.Truck.Subject.SubjectId != ""
                        && activations.Truck.Admission.Kind == MotionDqSubjectKind.Truck
                        && activations.Truck.Admission.GateId == MotionDqVocabulary.Gates.TruckActivation
                        && activations.Truck.Admission.GateDisposition == MotionDqGateDisposition.Satisfied
                        && activations.Truck.Admission.DecisionId != ""
                        && activations.Truck.Subject.ParentApplicationId == null
                        && activations.Truck.Admission.ParentCarrierProof == null)
                        && (activations.Trailer.Subject.ApplicationId == applicationId
                        && activations.Trailer.Subject.Kind == MotionDqSubjectKind.Trailer
                        && activations.Trailer.Subject.SubjectId != ""
                        && activations.Trailer.Admission.Kind == MotionDqSubjectKind.Trailer
                        && activations.Trailer.Admission.GateId == MotionDqVocabulary.Gates.TrailerActivation
                        && activations.Trailer.Admission.GateDisposition == MotionDqGateDisposition.Satisfied
                        && activations.Trailer.Admission.DecisionId != ""
                        && activations.Trailer.Subject.ParentApplicationId == null
                        && activations.Trailer.Admission.ParentCarrierProof == null)
                        && carrierProof != null
                        && activations.Driver.Subject.ParentApplicationId == applicationId
                        && carrierProof!.CarrierSubject == activations.CarrierOwnerOperator.Subject
                        && carrierProof.ActivationDecisionId == activations.CarrierOwnerOperator.Admission.DecisionId
                        && carrierProof.EvidenceId != "",
                        ActivationsValid,
                        MotionDqProcess.Accepted(MotionDqProcess.Identities.ValidateActivations),
                        "accepted",
                        MotionDqProcess.Identities.ValidateActivations)
                ],
                ActivationsInvalid,
                MotionDqProcess.Identities.ValidateActivations,
                MotionDqProcess.Rejected(MotionDqProcess.Identities.ValidateActivations),
                "rejected",
                MotionDqProcess.Identities.ValidateActivations);

            var carrierActivated = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.ActivateSubject.Reference,
                activations.CarrierOwnerOperator.Subject,
                activations.CarrierOwnerOperator.Admission,
                MotionDqProcess.Identities.ActivateCarrier,
                "completed",
                "outcome");

            async ProcessTask CarrierAccepted()
            {
            }

            async ProcessTask CarrierRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var carrierRequired = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.ActivateCarrier);
            await process.Match(
                carrierActivated,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.SubjectActivated, CarrierAccepted,
                    MotionDqProcess.Accepted(carrierRequired), "accepted", carrierRequired)],
                CarrierRejected,
                carrierRequired,
                MotionDqProcess.Rejected(carrierRequired),
                "rejected",
                carrierRequired);

            async ProcessTask ActivateApplicant()
            {
                await process.Transition(
                    Transitions.ActivateSubject.Reference,
                    activations.Applicant.Subject,
                    activations.Applicant.Admission,
                    MotionDqProcess.Activation("applicant"),
                    "joined");
            }

            async ProcessTask ActivateDriver()
            {
                await process.Transition(
                    Transitions.ActivateSubject.Reference,
                    activations.Driver.Subject,
                    activations.Driver.Admission,
                    MotionDqProcess.Activation("driver"),
                    "joined");
            }

            async ProcessTask ActivateTruck()
            {
                await process.Transition(
                    Transitions.ActivateSubject.Reference,
                    activations.Truck.Subject,
                    activations.Truck.Admission,
                    MotionDqProcess.Activation("truck"),
                    "joined");
            }

            async ProcessTask ActivateTrailer()
            {
                await process.Transition(
                    Transitions.ActivateSubject.Reference,
                    activations.Trailer.Subject,
                    activations.Trailer.Admission,
                    MotionDqProcess.Activation("trailer"),
                    "joined");
            }

            await process.ForkJoin(
                MotionDqProcess.Identities.ActivationFork,
                MotionDqProcess.Identities.ActivationJoin,
                "complete",
                process.Branch(
                    ActivateApplicant(),
                    MotionDqProcess.ActivationBranch("applicant"),
                    role: "applicant",
                    edgeOwner: MotionDqProcess.Identities.ActivationFork),
                process.Branch(
                    ActivateDriver(),
                    MotionDqProcess.ActivationBranch("driver"),
                    role: "driver",
                    edgeOwner: MotionDqProcess.Identities.ActivationFork),
                process.Branch(
                    ActivateTruck(),
                    MotionDqProcess.ActivationBranch("truck"),
                    role: "truck",
                    edgeOwner: MotionDqProcess.Identities.ActivationFork),
                process.Branch(
                    ActivateTrailer(),
                    MotionDqProcess.ActivationBranch("trailer"),
                    role: "trailer",
                    edgeOwner: MotionDqProcess.Identities.ActivationFork));

            var activationAdvanced = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.AdvanceCaseMilestone.Reference,
                caseId,
                input.ActivationAdmission,
                MotionDqProcess.Identities.AdvanceActivationMilestone,
                "completed",
                "outcome");

            async ProcessTask ActivationAdvanced()
            {
                await process.Succeed(
                    MotionDqOnboardingOutcome.Completed,
                    MotionDqProcess.Identities.Completed);
            }

            async ProcessTask ActivationAdvanceRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var activationRequired = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.AdvanceActivationMilestone);
            await process.Match(
                activationAdvanced,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.MilestoneAdvanced, ActivationAdvanced,
                    MotionDqProcess.Accepted(activationRequired), "accepted", activationRequired)],
                ActivationAdvanceRejected,
                activationRequired,
                MotionDqProcess.Rejected(activationRequired),
                "rejected",
                activationRequired);
        }

        async ProcessTask InsuranceDeclined(MotionDqInsuranceTermsResult result)
        {
            async ProcessTask DeclinedResultValid()
            {
            }

            async ProcessTask DeclinedResultInvalid()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            await process.Choice(
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [
                    process.When(
                        result.CaseId == caseId
                        && result.CaseId == input.InsuranceTerms.CaseId
                        && result.TermsRevision == input.InsuranceTerms.TermsRevision
                        && result.Evaluation.Requirement.CaseId == caseId
                        && result.Evaluation.Requirement.RequirementId == MotionDqVocabulary.Requirements.InsuranceTerms
                        && result.Evaluation.Disposition == MotionDqGateDisposition.Unsatisfied
                        && result.Evaluation.EvaluationId != ""
                        && result.Evaluation.EvidenceId != "",
                        DeclinedResultValid,
                        MotionDqProcess.Accepted(MotionDqProcess.Identities.ValidateDeclinedInsuranceTerms),
                        "accepted",
                        MotionDqProcess.Identities.ValidateDeclinedInsuranceTerms)
                ],
                DeclinedResultInvalid,
                MotionDqProcess.Identities.ValidateDeclinedInsuranceTerms,
                MotionDqProcess.Rejected(MotionDqProcess.Identities.ValidateDeclinedInsuranceTerms),
                "rejected",
                MotionDqProcess.Identities.ValidateDeclinedInsuranceTerms);

            var applied = await process.Transition<MotionDqTransitionOutcome>(
                Transitions.ApplyRequirementEvaluation.Reference,
                result.Evaluation.Requirement,
                result.Evaluation,
                MotionDqProcess.Identities.ApplyDeclinedInsuranceTerms,
                "completed",
                "outcome");

            async ProcessTask DeclineApplied()
            {
                await process.Succeed(
                    MotionDqOnboardingOutcome.InsuranceTermsDeclined,
                    MotionDqProcess.Identities.InsuranceTermsDeclined);
            }

            async ProcessTask DeclineApplyRejected()
            {
                await process.Terminate(
                    MotionDqOnboardingOutcome.CoordinationRejected,
                    MotionDqProcess.Identities.CoordinationRejected);
            }

            var required = MotionDqProcess.RequiredOutcome(MotionDqProcess.Identities.ApplyDeclinedInsuranceTerms);
            await process.Match(
                applied,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [process.Case(MotionDqTransitionOutcome.RequirementEvaluationAccepted, DeclineApplied,
                    MotionDqProcess.Accepted(required), "accepted", required)],
                DeclineApplyRejected,
                required,
                MotionDqProcess.Rejected(required),
                "rejected",
                required);
        }

        async ProcessTask InsuranceFailed()
        {
            await process.Terminate(
                MotionDqOnboardingOutcome.InsuranceTermsFailed,
                MotionDqProcess.Identities.InsuranceTermsFailed);
        }

        async ProcessTask InsuranceTimedOut()
        {
            await process.Succeed(
                MotionDqOnboardingOutcome.InsuranceTermsTimedOut,
                MotionDqProcess.Identities.InsuranceTermsTimedOut);
        }

        async ProcessTask InsuranceCancelled()
        {
            await process.Succeed(
                MotionDqOnboardingOutcome.InsuranceTermsCancelled,
                MotionDqProcess.Identities.InsuranceTermsCancelled);
        }

        await process.Effect(
            Interactions.InsuranceTermsRequest,
            input.InsuranceTerms,
            [
                process.Outcome<MotionDqInsuranceTermsResult>(
                    MotionDqInteractionContracts.InsuranceTermsAcceptedOutcome,
                    InsuranceAccepted,
                    MotionDqProcess.Identities.InsuranceTermsAcceptedOutcome,
                    "accepted",
                    MotionDqProcess.Identities.InsuranceTerms,
                    "accepted",
                    MotionDqProcess.Identities.InsuranceTerms),
                process.Outcome<MotionDqInsuranceTermsResult>(
                    MotionDqInteractionContracts.InsuranceTermsDeclinedOutcome,
                    InsuranceDeclined,
                    MotionDqProcess.Identities.InsuranceTermsDeclinedOutcome,
                    "declined",
                    MotionDqProcess.Identities.InsuranceTerms,
                    "declined",
                    MotionDqProcess.Identities.InsuranceTerms),
                process.Outcome(MotionDqInteractionContracts.InsuranceTermsFailedOutcome, InsuranceFailed,
                    MotionDqProcess.Identities.InsuranceTermsFailedOutcome, "failed", MotionDqProcess.Identities.InsuranceTerms),
                process.Outcome(MotionDqInteractionContracts.InsuranceTermsTimedOutOutcome, InsuranceTimedOut,
                    MotionDqProcess.Identities.InsuranceTermsTimedOutOutcome, "timedout", MotionDqProcess.Identities.InsuranceTerms),
                process.Outcome(MotionDqInteractionContracts.InsuranceTermsCancelledOutcome, InsuranceCancelled,
                    MotionDqProcess.Identities.InsuranceTermsCancelledOutcome, "cancelled", MotionDqProcess.Identities.InsuranceTerms)
            ],
            MotionDqProcess.Identities.InsuranceTerms);
        return process.Unreachable<MotionDqOnboardingOutcome>();
    }
}
