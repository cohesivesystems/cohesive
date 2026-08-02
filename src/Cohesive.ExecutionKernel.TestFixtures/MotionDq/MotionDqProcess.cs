using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using AuthoredProcess = Cohesive.Processes.Authoring.Process<
    Cohesive.ExecutionKernel.TestFixtures.MotionDq.MotionDqOnboardingInput,
    Cohesive.ExecutionKernel.TestFixtures.MotionDq.MotionDqOnboardingOutcome>;

namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Finite terminal outcomes of the Motion DQ onboarding Process fixture.</summary>
public enum MotionDqOnboardingOutcome
{
    /// <summary>Every post-terms branch converged and every subject activation occurrence completed.</summary>
    Completed,

    /// <summary>The caseworker concluded that the application is not eligible.</summary>
    NotEligible,

    /// <summary>An explicitly delivered cancellation terminated onboarding.</summary>
    Cancelled,

    /// <summary>The caseworker-review timer won before an eligible decision or cancellation.</summary>
    ReviewTimedOut,

    /// <summary>The external review-task Request failed terminally.</summary>
    ReviewTaskFailed,

    /// <summary>The applicant declined the exact insurance-terms revision.</summary>
    InsuranceTermsDeclined,

    /// <summary>The insurance-terms provider failed terminally.</summary>
    InsuranceTermsFailed,

    /// <summary>The insurance-terms Request reached its declared semantic timeout.</summary>
    InsuranceTermsTimedOut,

    /// <summary>The insurance-terms Request was cancelled.</summary>
    InsuranceTermsCancelled,

    /// <summary>A typed command, interaction result, or Transition outcome violated the declared coordination contract.</summary>
    CoordinationRejected
}

/// <summary>One exact independently owned subject activation occurrence.</summary>
/// <param name="Subject">Stable subject authority addressed by the Transition invocation.</param>
/// <param name="Admission">Typed gate decision supplied to the generic activation Transition.</param>
public sealed record MotionDqSubjectActivationInvocation(
    MotionDqSubjectReference Subject,
    MotionDqSubjectActivationAdmission Admission);

/// <summary>Bounded post-terms fulfillment requests projected from the version-one profile.</summary>
/// <param name="DrugTest">Provider-neutral drug-test request.</param>
/// <param name="Clearinghouse">Provider-neutral Clearinghouse request.</param>
/// <param name="Vehicle">Provider-neutral vehicle-qualification request.</param>
/// <param name="Business">Provider-neutral business-qualification request.</param>
/// <param name="Equipment">Provider-neutral equipment-qualification request.</param>
/// <param name="Permit">Provider-neutral permit request.</param>
/// <param name="RandomPool">Provider-neutral random-pool enrollment request.</param>
public sealed record MotionDqPostTermsFulfillment(
    MotionDqRequirementFulfillmentRequest DrugTest,
    MotionDqRequirementFulfillmentRequest Clearinghouse,
    MotionDqRequirementFulfillmentRequest Vehicle,
    MotionDqRequirementFulfillmentRequest Business,
    MotionDqRequirementFulfillmentRequest Equipment,
    MotionDqRequirementFulfillmentRequest Permit,
    MotionDqRequirementFulfillmentRequest RandomPool);

/// <summary>Independent activation occurrences for the five authorities gated by this Process.</summary>
/// <param name="Applicant">Applicant activation independent of the driver, truck, and trailer occurrences.</param>
/// <param name="CarrierOwnerOperator">Carrier or owner-operator activation, which precedes the dependent driver.</param>
/// <param name="Driver">Dependent-driver activation.</param>
/// <param name="Truck">Truck activation independent of the driver and trailer occurrences.</param>
/// <param name="Trailer">Trailer activation independent of the driver and truck occurrences.</param>
public sealed record MotionDqSubjectActivations(
    MotionDqSubjectActivationInvocation Applicant,
    MotionDqSubjectActivationInvocation CarrierOwnerOperator,
    MotionDqSubjectActivationInvocation Driver,
    MotionDqSubjectActivationInvocation Truck,
    MotionDqSubjectActivationInvocation Trailer);

/// <summary>Typed invocation input for the canonical Motion DQ onboarding Process.</summary>
/// <param name="Prequalification">Prequalification submitted against the pinned profile.</param>
/// <param name="FullApplication">Full application submitted after prequalification.</param>
/// <param name="ReviewTask">Request to create the external caseworker-review task.</param>
/// <param name="ReviewDueAtUtc">Absolute timer deadline participating in caseworker AwaitMatch arbitration.</param>
/// <param name="ReviewTimeoutCancellation">Typed cancellation recorded when the review timer wins.</param>
/// <param name="InsuranceTerms">Exact insurance-terms Request admitted only after Hire.</param>
/// <param name="InsuranceTermsAdmission">Exact gate decision advancing Insurance Terms to Post Terms.</param>
/// <param name="PostTerms">Seven provider-neutral requests started only after insurance acceptance.</param>
/// <param name="PostTermsAdmission">Exact gate decision advancing Post Terms to Activation.</param>
/// <param name="Activations">Five independent subject activation occurrences.</param>
/// <param name="ActivationAdmission">Exact gate decision advancing Activation to Completed.</param>
public sealed record MotionDqOnboardingInput(
    MotionDqPrequalificationSubmission Prequalification,
    MotionDqFullApplicationSubmission FullApplication,
    MotionDqReviewTaskRequest ReviewTask,
    DateTimeOffset ReviewDueAtUtc,
    MotionDqCancellation ReviewTimeoutCancellation,
    MotionDqInsuranceTermsRequest InsuranceTerms,
    MotionDqCaseMilestoneAdmission InsuranceTermsAdmission,
    MotionDqPostTermsFulfillment PostTerms,
    MotionDqCaseMilestoneAdmission PostTermsAdmission,
    MotionDqSubjectActivations Activations,
    MotionDqCaseMilestoneAdmission ActivationAdmission);

/// <summary>
/// Canonical authored and linked Motion DQ onboarding Process artifacts used by reference and durable conformance.
/// </summary>
/// <remarks>
/// A provider non-success is typed as attempt evidence and never enters the requirement Transition. Activation and
/// milestone truth is not inferred from fork-local bindings: each authority receives a separately issued exact gate
/// admission, and a rejecting Transition host fails that token before
/// <see cref="MotionDqOnboardingOutcome.Completed"/>. Cross-branch derivation of those admissions remains outside
/// the current Process IR because it has no branch-result merge expression. A manual terminal non-success therefore
/// converges the coordination branch without claiming requirement satisfaction; the subsequent explicit post-terms
/// admission is the only authority permitted to advance the case.
/// </remarks>
public sealed class MotionDqProcess
{
    MotionDqProcess(
        AuthoredProcess authored,
        ProcessDefinitionValidationContext linkingContext,
        ProcessCompilationResult compilation,
        MotionDqTransitionDefinitions transitions,
        MotionDqInteractionContracts interactions,
        ImmutableArray<ExecutionDefinitionDocument> documents,
        ImmutableArray<DurableRequestBinding> requestBindings)
    {
        Authored = authored;
        LinkingContext = linkingContext;
        Compilation = compilation;
        Plan = compilation.Plan
            ?? throw new ArgumentException("A Motion DQ Process artifact requires a compiled plan.", nameof(compilation));
        Transitions = transitions;
        Interactions = interactions;
        Documents = documents;
        RequestBindings = requestBindings;
    }

    /// <summary>Canonical version-one Motion DQ onboarding Process and all exact dependencies.</summary>
    public static MotionDqProcess Version1 { get; } = AuthorVersion1();

    /// <summary>Authors and links a fresh deterministic version-one fixture.</summary>
    /// <returns>
    /// A new artifact graph whose canonical fingerprints equal <see cref="Version1"/> while sharing no runtime state.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A Transition link, interaction catalog, authored Process, or target-independent compilation is invalid.
    /// </exception>
    public static MotionDqProcess AuthorVersion1() => CreateVersion1();

    /// <summary>Typed handle whose canonical document is the Process semantic authority.</summary>
    public AuthoredProcess Authored { get; }

    /// <summary>Canonical persisted Process execution-definition document.</summary>
    public ExecutionDefinitionDocument Document => Authored.Document;

    /// <summary>Typed canonical Process IR projected from <see cref="Document"/>.</summary>
    public CanonicalProcessDefinition Definition => Plan.Definition;

    /// <summary>Exact fingerprinted reference to <see cref="Document"/>.</summary>
    public ExecutionDefinitionReference Reference => Authored.Reference;

    /// <summary>Derived Transition links and exact interaction catalog used for Process validation.</summary>
    public ProcessDefinitionValidationContext LinkingContext { get; }

    /// <summary>Complete target-independent compilation result.</summary>
    public ProcessCompilationResult Compilation { get; }

    /// <summary>Executable canonical reference plan shared by reference and durable interpretations.</summary>
    public CompiledProcessPlan Plan { get; }

    /// <summary>Canonical Transition dependencies invoked by the Process.</summary>
    public MotionDqTransitionDefinitions Transitions { get; }

    /// <summary>Canonical interaction-contract catalog and exact Request bindings.</summary>
    public MotionDqInteractionContracts Interactions { get; }

    /// <summary>Validated interaction catalog linked by <see cref="LinkingContext"/>.</summary>
    public InteractionContractCatalog InteractionCatalog => Interactions.Catalog;

    /// <summary>
    /// Complete canonical dependency documents followed by the Process document; generated runtime state is absent.
    /// </summary>
    public ImmutableArray<ExecutionDefinitionDocument> Documents { get; }

    /// <summary>
    /// Review-task, insurance-terms, and provider-neutral fulfillment bindings. The fulfillment binding occurs once
    /// even though its exact Request is authored at vendor and manual nodes.
    /// </summary>
    public ImmutableArray<DurableRequestBinding> RequestBindings { get; }

    static MotionDqProcess CreateVersion1()
    {
        var transitions = MotionDqTransitions.Author();
        var interactions = MotionDqInteractionContracts.Version1;
        var links = ImmutableArray.CreateBuilder<ProcessDefinitionLink>(transitions.Documents.Length);
        foreach (var document in transitions.Documents)
        {
            var validation = ProcessDefinitionLink.TryCreateTransition(document, out var link);
            RequireValid(validation, $"Transition link '{document.Metadata.DefinitionId.Value}'");
            links.Add(link ?? throw new InvalidOperationException(
                $"Transition '{document.Metadata.DefinitionId.Value}' produced no exact Process link."));
        }

        var linkingContext = new ProcessDefinitionValidationContext(
            links.MoveToImmutable(),
            interactions.Catalog);
        var authored = Author(transitions, interactions);
        RequireValid(authored.Validation, "context-free Process authoring");
        var compilation = authored.Compile(linkingContext);
        RequireValid(compilation.Validation, "linked Process compilation");
        if (compilation.Plan is null)
            throw new InvalidOperationException("Linked Motion DQ Process compilation produced no reference plan.");

        ImmutableArray<ExecutionDefinitionDocument> documents =
        [
            .. transitions.Documents,
            .. interactions.Documents,
            authored.Document
        ];
        ImmutableArray<DurableRequestBinding> requestBindings =
        [
            interactions.ReviewTaskBinding,
            interactions.InsuranceTermsBinding,
            interactions.FulfillRequirementBinding
        ];
        return new(
            authored,
            linkingContext,
            compilation,
            transitions,
            interactions,
            documents,
            requestBindings);
    }

    static AuthoredProcess Author(
        MotionDqTransitionDefinitions transitions,
        MotionDqInteractionContracts interactions) =>
        ProcessAuthoring.Create<MotionDqOnboardingInput, MotionDqOnboardingOutcome>(
            new(
                new("process/motion-dq/onboarding"),
                new("revision/1"),
                Identities.SubmitPrequalification,
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance(),
                displayName: "Motion DQ onboarding",
                description: "Coordinates typed review, terms, fulfillment, and subject activation semantics."),
            process => AuthorGraph(process, transitions, interactions));

    static void AuthorGraph(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        MotionDqTransitionDefinitions transitions,
        MotionDqInteractionContracts interactions)
    {
        var prequalification = process.Input.Field(static input => input.Prequalification);
        var caseId = prequalification.Field(static submission => submission.CaseId);
        var fullApplication = process.Input.Field(static input => input.FullApplication);
        var reviewTask = process.Input.Field(static input => input.ReviewTask);
        var reviewDueAt = process.Input.Field(static input => input.ReviewDueAtUtc);
        AuthorRequiredTransition(
            process,
            Identities.SubmitPrequalification,
            transitions.SubmitPrequalification.Reference,
            caseId,
            prequalification,
            MotionDqTransitionOutcome.PrequalificationSubmitted,
            Identities.SubmitFullApplication);
        AuthorRequiredTransition(
            process,
            Identities.SubmitFullApplication,
            transitions.SubmitFullApplication.Reference,
            caseId,
            fullApplication,
            MotionDqTransitionOutcome.FullApplicationSubmitted,
            Identities.ValidateReviewTask);

        var reviewTaskValid = process.And(
            process.Equal(reviewTask.Field(static request => request.CaseId), caseId),
            process.Equal(
                reviewTask.Field(static request => request.ApplicationId),
                fullApplication.Field(static application => application.ApplicationId)));
        AuthorGuard(
            process,
            Identities.ValidateReviewTask,
            reviewTaskValid,
            Identities.CreateReviewTask);

        var reviewTaskReference = process.Output<MotionDqReviewTaskReference>(
            Identities.CreateReviewTask,
            "created");
        process.Request(
            Identities.CreateReviewTask,
            interactions.ReviewTaskRequest,
            reviewTask,
            [
                process.RequestOutcome(
                    new("motion-dq/review-task/created"),
                    MotionDqInteractionContracts.ReviewTaskCreatedOutcome,
                    process.Continuation(
                        process.Edge(
                            Identities.CreateReviewTask,
                            "created",
                            Identities.AwaitReview),
                        reviewTaskReference)),
                process.RequestOutcome(
                    new("motion-dq/review-task/failed"),
                    MotionDqInteractionContracts.ReviewTaskFailedOutcome,
                    process.Continuation(process.Edge(
                        Identities.CreateReviewTask,
                        "failed",
                        Identities.ReviewTaskFailed)))
            ]);

        AuthorReviewAwait(
            process,
            transitions,
            interactions,
            caseId,
            fullApplication.Field(static application => application.ApplicationId),
            reviewDueAt);

        var insuranceTerms = process.Input.Field(static input => input.InsuranceTerms);
        var insuranceRequestValid = process.And(
            process.Equal(insuranceTerms.Field(static request => request.CaseId), caseId),
            process.NotEqual(
                insuranceTerms.Field(static request => request.TermsRevision),
                process.Constant(string.Empty)));
        AuthorGuard(
            process,
            Identities.ValidateInsuranceRequest,
            insuranceRequestValid,
            Identities.InsuranceTerms);

        var insuranceAccepted = process.Output<MotionDqInsuranceTermsResult>(Identities.InsuranceTerms, "accepted");
        var insuranceDeclined = process.Output<MotionDqInsuranceTermsResult>(Identities.InsuranceTerms, "declined");
        process.Request(
            Identities.InsuranceTerms,
            interactions.InsuranceTermsRequest,
            insuranceTerms,
            [
                process.RequestOutcome(
                    new("motion-dq/insurance-terms/accepted"),
                    MotionDqInteractionContracts.InsuranceTermsAcceptedOutcome,
                    process.Continuation(
                        process.Edge(
                            Identities.InsuranceTerms,
                            "accepted",
                            Identities.ValidateAcceptedInsuranceTerms),
                        insuranceAccepted)),
                process.RequestOutcome(
                    new("motion-dq/insurance-terms/declined"),
                    MotionDqInteractionContracts.InsuranceTermsDeclinedOutcome,
                    process.Continuation(
                        process.Edge(
                            Identities.InsuranceTerms,
                            "declined",
                            Identities.ValidateDeclinedInsuranceTerms),
                        insuranceDeclined)),
                process.RequestOutcome(
                    new("motion-dq/insurance-terms/failed"),
                    MotionDqInteractionContracts.InsuranceTermsFailedOutcome,
                    process.Continuation(process.Edge(
                        Identities.InsuranceTerms,
                        "failed",
                        Identities.InsuranceTermsFailed))),
                process.RequestOutcome(
                    new("motion-dq/insurance-terms/timed-out"),
                    MotionDqInteractionContracts.InsuranceTermsTimedOutOutcome,
                    process.Continuation(process.Edge(
                        Identities.InsuranceTerms,
                        "timedout",
                        Identities.InsuranceTermsTimedOut))),
                process.RequestOutcome(
                    new("motion-dq/insurance-terms/cancelled"),
                    MotionDqInteractionContracts.InsuranceTermsCancelledOutcome,
                    process.Continuation(process.Edge(
                        Identities.InsuranceTerms,
                        "cancelled",
                        Identities.InsuranceTermsCancelled)))
            ]);

        AuthorInsuranceResultGuard(
            process,
            Identities.ValidateAcceptedInsuranceTerms,
            insuranceAccepted.Value,
            insuranceTerms,
            caseId,
            MotionDqGateDisposition.Satisfied,
            Identities.ApplyAcceptedInsuranceTerms);
        var acceptedEvaluation = insuranceAccepted.Field(static result => result.Evaluation);
        AuthorRequiredTransition(
            process,
            Identities.ApplyAcceptedInsuranceTerms,
            transitions.ApplyRequirementEvaluation.Reference,
            acceptedEvaluation.Field(static evaluation => evaluation.Requirement),
            acceptedEvaluation,
            MotionDqTransitionOutcome.RequirementEvaluationAccepted,
            Identities.AdvanceInsuranceTermsMilestone);
        AuthorRequiredTransition(
            process,
            Identities.AdvanceInsuranceTermsMilestone,
            transitions.AdvanceCaseMilestone.Reference,
            caseId,
            process.Input.Field(static input => input.InsuranceTermsAdmission),
            MotionDqTransitionOutcome.MilestoneAdvanced,
            Identities.ValidatePostTerms);

        AuthorInsuranceResultGuard(
            process,
            Identities.ValidateDeclinedInsuranceTerms,
            insuranceDeclined.Value,
            insuranceTerms,
            caseId,
            MotionDqGateDisposition.Unsatisfied,
            Identities.ApplyDeclinedInsuranceTerms);
        var declinedEvaluation = insuranceDeclined.Field(static result => result.Evaluation);
        AuthorRequiredTransition(
            process,
            Identities.ApplyDeclinedInsuranceTerms,
            transitions.ApplyRequirementEvaluation.Reference,
            declinedEvaluation.Field(static evaluation => evaluation.Requirement),
            declinedEvaluation,
            MotionDqTransitionOutcome.RequirementEvaluationAccepted,
            Identities.InsuranceTermsDeclined);

        var postTerms = process.Input.Field(static input => input.PostTerms);
        var drugTest = postTerms.Field(static requests => requests.DrugTest);
        var clearinghouse = postTerms.Field(static requests => requests.Clearinghouse);
        var vehicle = postTerms.Field(static requests => requests.Vehicle);
        var business = postTerms.Field(static requests => requests.Business);
        var equipment = postTerms.Field(static requests => requests.Equipment);
        var permit = postTerms.Field(static requests => requests.Permit);
        var randomPool = postTerms.Field(static requests => requests.RandomPool);
        var postTermsValid = RequirementRequestMatches(
            process,
            drugTest,
            caseId,
            MotionDqProfileCatalog.DrugTestRequirement,
            MotionDqVocabulary.EvidenceNeeds.DrugTest);
        postTermsValid = process.And(
            postTermsValid,
            RequirementRequestMatches(
                process,
                clearinghouse,
                caseId,
                MotionDqProfileCatalog.ClearinghouseRequirement,
                MotionDqVocabulary.EvidenceNeeds.Clearinghouse));
        postTermsValid = process.And(
            postTermsValid,
            RequirementRequestMatches(
                process,
                vehicle,
                caseId,
                MotionDqProfileCatalog.VehicleRequirement,
                MotionDqVocabulary.EvidenceNeeds.Vehicle));
        postTermsValid = process.And(
            postTermsValid,
            RequirementRequestMatches(
                process,
                business,
                caseId,
                MotionDqProfileCatalog.BusinessRequirement,
                MotionDqVocabulary.EvidenceNeeds.Business));
        postTermsValid = process.And(
            postTermsValid,
            RequirementRequestMatches(
                process,
                equipment,
                caseId,
                MotionDqProfileCatalog.EquipmentRequirement,
                MotionDqVocabulary.EvidenceNeeds.Equipment));
        postTermsValid = process.And(
            postTermsValid,
            RequirementRequestMatches(
                process,
                permit,
                caseId,
                MotionDqProfileCatalog.PermitRequirement,
                MotionDqVocabulary.EvidenceNeeds.Permit));
        postTermsValid = process.And(
            postTermsValid,
            RequirementRequestMatches(
                process,
                randomPool,
                caseId,
                MotionDqProfileCatalog.RandomPoolRequirement,
                MotionDqVocabulary.EvidenceNeeds.RandomPool));
        AuthorGuard(process, Identities.ValidatePostTerms, postTermsValid, Identities.PostTermsFork);

        var postTermsBranches = ImmutableArray.CreateBuilder<ProcessForkBranch>(7);
        postTermsBranches.Add(AuthorRequirementBranch(
            process,
            interactions,
            transitions,
            MotionDqProfileCatalog.DrugTestRequirement,
            drugTest));
        postTermsBranches.Add(AuthorRequirementBranch(
            process,
            interactions,
            transitions,
            MotionDqProfileCatalog.ClearinghouseRequirement,
            clearinghouse));
        postTermsBranches.Add(AuthorRequirementBranch(
            process,
            interactions,
            transitions,
            MotionDqProfileCatalog.VehicleRequirement,
            vehicle));
        postTermsBranches.Add(AuthorRequirementBranch(
            process,
            interactions,
            transitions,
            MotionDqProfileCatalog.BusinessRequirement,
            business));
        postTermsBranches.Add(AuthorRequirementBranch(
            process,
            interactions,
            transitions,
            MotionDqProfileCatalog.EquipmentRequirement,
            equipment));
        postTermsBranches.Add(AuthorRequirementBranch(
            process,
            interactions,
            transitions,
            MotionDqProfileCatalog.PermitRequirement,
            permit));
        postTermsBranches.Add(AuthorRequirementBranch(
            process,
            interactions,
            transitions,
            MotionDqProfileCatalog.RandomPoolRequirement,
            randomPool));
        process.Fork(
            Identities.PostTermsFork,
            postTermsBranches.MoveToImmutable(),
            Identities.PostTermsJoin);
        process.Join(
            Identities.PostTermsJoin,
            Identities.PostTermsFork,
            AllJoinPolicy(),
            process.Edge(Identities.PostTermsJoin, "complete", Identities.AdvancePostTermsMilestone));

        AuthorRequiredTransition(
            process,
            Identities.AdvancePostTermsMilestone,
            transitions.AdvanceCaseMilestone.Reference,
            caseId,
            process.Input.Field(static input => input.PostTermsAdmission),
            MotionDqTransitionOutcome.MilestoneAdvanced,
            Identities.ValidateActivations);

        AuthorActivations(
            process,
            transitions,
            fullApplication.Field(static application => application.ApplicationId),
            caseId);
        AuthorTerminals(process);
    }

    static void AuthorRequiredTransition<TSubject, TInput>(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ExecutionNodeId invocationId,
        ExecutionDefinitionReference transition,
        ProcessValue<TSubject> subject,
        ProcessValue<TInput> input,
        MotionDqTransitionOutcome requiredOutcome,
        ExecutionNodeId next)
    {
        var matchId = new ExecutionNodeId($"{invocationId.Value}/require-outcome");
        var output = process.Output<MotionDqTransitionOutcome>(invocationId, "outcome");
        process.InvokeTransition(
            invocationId,
            transition,
            subject,
            input,
            process.Continuation(
                process.Edge(invocationId, "completed", matchId),
                output));
        process.Match(
            matchId,
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            output.Value,
            [
                process.MatchCase(
                    new($"{matchId.Value}/accepted"),
                    output.Value,
                    requiredOutcome,
                    process.Edge(matchId, "accepted", next))
            ],
            process.Fallback(
                new($"{matchId.Value}/rejected"),
                process.Edge(matchId, "rejected", Identities.CoordinationRejected)));
    }

    static void AuthorGuard(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ExecutionNodeId id,
        ProcessValue<bool> predicate,
        ExecutionNodeId next) =>
        process.Choice(
            id,
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            [
                process.ChoiceCase(
                    new($"{id.Value}/accepted"),
                    predicate,
                    process.Edge(id, "accepted", next))
            ],
            process.Fallback(
                new($"{id.Value}/rejected"),
                process.Edge(id, "rejected", Identities.CoordinationRejected)));

    static void AuthorInsuranceResultGuard(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ExecutionNodeId id,
        ProcessValue<MotionDqInsuranceTermsResult> result,
        ProcessValue<MotionDqInsuranceTermsRequest> request,
        ProcessValue<string> caseId,
        MotionDqGateDisposition expectedDisposition,
        ExecutionNodeId next)
    {
        var evaluation = result.Field(static value => value.Evaluation);
        var valid = process.Equal(result.Field(static value => value.CaseId), caseId);
        valid = process.And(
            valid,
            process.Equal(
                result.Field(static value => value.CaseId),
                request.Field(static value => value.CaseId)));
        valid = process.And(
            valid,
            process.Equal(
                result.Field(static value => value.TermsRevision),
                request.Field(static value => value.TermsRevision)));
        valid = process.And(
            valid,
            process.Equal(
                evaluation.Field(static value => value.Requirement.CaseId),
                caseId));
        valid = process.And(
            valid,
            process.Equal(
                evaluation.Field(static value => value.Requirement.RequirementId),
                process.Constant(MotionDqProfileCatalog.InsuranceTermsRequirement.Id)));
        valid = process.And(
            valid,
            process.Equal(
                evaluation.Field(static value => value.Disposition),
                process.Constant(expectedDisposition)));
        valid = process.And(
            valid,
            process.NotEqual(
                evaluation.Field(static value => value.EvaluationId),
                process.Constant(string.Empty)));
        valid = process.And(
            valid,
            process.NotEqual(
                evaluation.Field(static value => value.EvidenceId),
                process.Constant(string.Empty)));
        AuthorGuard(process, id, valid, next);
    }

    static ProcessValue<bool> RequirementRequestMatches(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ProcessValue<MotionDqRequirementFulfillmentRequest> request,
        ProcessValue<string> caseId,
        MotionDqRequirementReference requirement,
        string evidenceNeedId)
    {
        var valid = process.Equal(
            request.Field(static value => value.Requirement.CaseId),
            caseId);
        valid = process.And(
            valid,
            process.Equal(
                request.Field(static value => value.Requirement.RequirementId),
                process.Constant(requirement.Id)));
        return process.And(
            valid,
            process.Equal(
                request.Field(static value => value.EvidenceNeedId),
                process.Constant(evidenceNeedId)));
    }

    static ProcessValue<bool> ReviewDecisionMatches(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ProcessValue<MotionDqReviewDecision> decision,
        ProcessValue<string> caseId,
        ProcessValue<string> applicationId,
        MotionDqReviewDecisionKind expectedKind)
    {
        var matches = process.Equal(
            decision.Field(static value => value.Kind),
            process.Constant(expectedKind));
        matches = process.And(
            matches,
            process.Equal(
                decision.Field(static value => value.CaseId),
                caseId));
        return process.And(
            matches,
            process.Equal(
                decision.Field(static value => value.ApplicationId),
                applicationId));
    }

    static ProcessValue<bool> IndependentActivationMatches(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ProcessValue<MotionDqSubjectActivationInvocation> activation,
        ProcessValue<string> applicationId,
        MotionDqSubjectKind expectedKind,
        string expectedGateId)
    {
        var valid = ActivationBaseMatches(
            process,
            activation,
            applicationId,
            expectedKind,
            expectedGateId);
        valid = process.And(
            valid,
            IsNull(
                process,
                activation.Field(static value => value.Subject.ParentApplicationId)));
        return process.And(
            valid,
            IsNull(
                process,
                activation.Field(static value => value.Admission.ParentCarrierProof)));
    }

    static ProcessValue<bool> ActivationBaseMatches(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ProcessValue<MotionDqSubjectActivationInvocation> activation,
        ProcessValue<string> applicationId,
        MotionDqSubjectKind expectedKind,
        string expectedGateId)
    {
        var subject = activation.Field(static value => value.Subject);
        var admission = activation.Field(static value => value.Admission);
        var valid = process.Equal(
            subject.Field(static value => value.ApplicationId),
            applicationId);
        valid = process.And(
            valid,
            process.Equal(
                subject.Field(static value => value.Kind),
                process.Constant(expectedKind)));
        valid = process.And(
            valid,
            process.NotEqual(
                subject.Field(static value => value.SubjectId),
                process.Constant(string.Empty)));
        valid = process.And(
            valid,
            process.Equal(
                admission.Field(static value => value.Kind),
                process.Constant(expectedKind)));
        valid = process.And(
            valid,
            process.Equal(
                admission.Field(static value => value.GateId),
                process.Constant(expectedGateId)));
        valid = process.And(
            valid,
            process.Equal(
                admission.Field(static value => value.GateDisposition),
                process.Constant(MotionDqGateDisposition.Satisfied)));
        return process.And(
            valid,
            process.NotEqual(
                admission.Field(static value => value.DecisionId),
                process.Constant(string.Empty)));
    }

    static ProcessValue<bool> IsNull<TValue>(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ProcessValue<TValue> value) =>
        process.CanonicalValue<bool>(
            Expr.Eq(value.Expression, Expr.Const(ObservationValue.Null)),
            new(new ScalarTypeRef(ScalarTypeKind.Bool)));

    static ProcessValue<bool> IsNotNull<TValue>(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ProcessValue<TValue> value) =>
        process.CanonicalValue<bool>(
            Expr.Ne(value.Expression, Expr.Const(ObservationValue.Null)),
            new(new ScalarTypeRef(ScalarTypeKind.Bool)));

    static ProcessValue<bool> EqualRegardlessOfNullability(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        ProcessValue<string?> left,
        ProcessValue<string> right) =>
        process.CanonicalValue<bool>(
            Expr.Eq(left.Expression, right.Expression),
            new(new ScalarTypeRef(ScalarTypeKind.Bool)));

    static void AuthorReviewAwait(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        MotionDqTransitionDefinitions transitions,
        MotionDqInteractionContracts interactions,
        ProcessValue<string> caseId,
        ProcessValue<string> applicationId,
        ProcessValue<DateTimeOffset> reviewDueAt)
    {
        var hire = process.Output<MotionDqReviewDecision>(Identities.AwaitReview, "hire");
        var hold = process.Output<MotionDqReviewDecision>(Identities.AwaitReview, "hold");
        var notEligible = process.Output<MotionDqReviewDecision>(Identities.AwaitReview, "noteligible");
        var cancellation = process.Output<MotionDqCancellation>(Identities.AwaitReview, "cancelled");
        var hireGuard = ReviewDecisionMatches(
            process,
            hire.Value,
            caseId,
            applicationId,
            MotionDqReviewDecisionKind.Hire);
        var holdGuard = ReviewDecisionMatches(
            process,
            hold.Value,
            caseId,
            applicationId,
            MotionDqReviewDecisionKind.Hold);
        var notEligibleGuard = ReviewDecisionMatches(
            process,
            notEligible.Value,
            caseId,
            applicationId,
            MotionDqReviewDecisionKind.NotEligible);
        var cancellationGuard = process.Equal(
            cancellation.Field(static value => value.CaseId),
            caseId);

        ProcessAwaitClause[] clauses =
        [
            process.AwaitInteractionClause(
                new("motion-dq/review/cancelled"),
                interactions.CaseCancellationSignal,
                cancellation,
                requestObligation: null,
                cancellationGuard,
                priority: 100,
                process.Continuation(process.Edge(
                    Identities.AwaitReview,
                    "cancelled",
                    Identities.RecordCancellation))),
            process.AwaitInteractionClause(
                new("motion-dq/review/not-eligible"),
                interactions.ReviewDecisionSignal,
                notEligible,
                requestObligation: null,
                notEligibleGuard,
                priority: 90,
                process.Continuation(process.Edge(
                    Identities.AwaitReview,
                    "noteligible",
                    Identities.RecordNotEligible))),
            process.AwaitInteractionClause(
                new("motion-dq/review/hire"),
                interactions.ReviewDecisionSignal,
                hire,
                requestObligation: null,
                hireGuard,
                priority: 80,
                process.Continuation(process.Edge(
                    Identities.AwaitReview,
                    "hire",
                    Identities.RecordHire))),
            process.AwaitInteractionClause(
                new("motion-dq/review/hold"),
                interactions.ReviewDecisionSignal,
                hold,
                requestObligation: null,
                holdGuard,
                priority: 70,
                process.Continuation(process.Edge(
                    Identities.AwaitReview,
                    "hold",
                    Identities.RecordHold))),
            process.AwaitTimerClause(
                new("motion-dq/review/timed-out"),
                reviewDueAt,
                priority: 0,
                process.Continuation(process.Edge(
                    Identities.AwaitReview,
                    "timedout",
                    Identities.RecordReviewTimeout)))
        ];
        process.AwaitMatch(
            Identities.AwaitReview,
            ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
            [.. clauses],
            lateInput: ProcessAwaitInputDisposition.Observe,
            staleInput: ProcessAwaitInputDisposition.Reject,
            duplicateInput: ProcessAwaitInputDisposition.ReusePriorDisposition,
            missingTarget: ProcessAwaitMissingTargetDisposition.DeadLetter,
            retentionHorizon: TimeSpan.FromDays(30));

        AuthorRequiredTransition(
            process,
            Identities.RecordHire,
            transitions.RecordReviewDecision.Reference,
            caseId,
            hire.Value,
            MotionDqTransitionOutcome.ReviewDecisionRecorded,
            Identities.ValidateInsuranceRequest);
        AuthorRequiredTransition(
            process,
            Identities.RecordHold,
            transitions.RecordReviewDecision.Reference,
            caseId,
            hold.Value,
            MotionDqTransitionOutcome.ReviewDecisionRecorded,
            Identities.AwaitReview);
        AuthorRequiredTransition(
            process,
            Identities.RecordNotEligible,
            transitions.RecordReviewDecision.Reference,
            caseId,
            notEligible.Value,
            MotionDqTransitionOutcome.ReviewDecisionRecorded,
            Identities.NotEligible);
        AuthorRequiredTransition(
            process,
            Identities.RecordCancellation,
            transitions.CancelCase.Reference,
            caseId,
            cancellation.Value,
            MotionDqTransitionOutcome.Cancelled,
            Identities.Cancelled);
        AuthorRequiredTransition(
            process,
            Identities.RecordReviewTimeout,
            transitions.CancelCase.Reference,
            caseId,
            process.Input.Field(static input => input.ReviewTimeoutCancellation),
            MotionDqTransitionOutcome.Cancelled,
            Identities.ReviewTimedOut);
    }

    static ProcessForkBranch AuthorRequirementBranch(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        MotionDqInteractionContracts interactions,
        MotionDqTransitionDefinitions transitions,
        MotionDqRequirementReference requirement,
        ProcessValue<MotionDqRequirementFulfillmentRequest> request)
    {
        var role = LastPathSegment(requirement.Id);
        var prefix = $"motion-dq/post-terms/{requirement.Id}";
        var vendor = new ExecutionNodeId($"{prefix}/vendor");
        var manual = new ExecutionNodeId($"{prefix}/manual");
        var vendorFulfilledApply = new ExecutionNodeId($"{prefix}/apply/vendor-fulfilled");
        var manualFulfilledApply = new ExecutionNodeId($"{prefix}/apply/manual-fulfilled");

        var vendorFulfilled = process.Output<MotionDqRequirementEvaluationReceipt>(vendor, "fulfilled");
        var manualFulfilled = process.Output<MotionDqRequirementEvaluationReceipt>(manual, "fulfilled");

        process.Request(
            vendor,
            interactions.FulfillRequirementRequest,
            request,
            [
                process.RequestOutcome(
                    new($"{prefix}/vendor/fulfilled"),
                    MotionDqInteractionContracts.RequirementFulfilledOutcome,
                    process.Continuation(
                        process.Edge(vendor, "fulfilled", vendorFulfilledApply),
                        vendorFulfilled)),
                process.RequestOutcome(
                    new($"{prefix}/vendor/provider-failed"),
                    MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                    process.Continuation(process.Edge(vendor, "failed", manual))),
                process.RequestOutcome(
                    new($"{prefix}/vendor/provider-timed-out"),
                    MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                    process.Continuation(process.Edge(vendor, "timedout", manual))),
                process.RequestOutcome(
                    new($"{prefix}/vendor/cancelled"),
                    MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                    process.Continuation(process.Edge(vendor, "cancelled", manual)))
            ]);
        process.Request(
            manual,
            interactions.FulfillRequirementRequest,
            request,
            [
                process.RequestOutcome(
                    new($"{prefix}/manual/fulfilled"),
                    MotionDqInteractionContracts.RequirementFulfilledOutcome,
                    process.Continuation(
                        process.Edge(manual, "fulfilled", manualFulfilledApply),
                        manualFulfilled)),
                process.RequestOutcome(
                    new($"{prefix}/manual/provider-failed"),
                    MotionDqInteractionContracts.RequirementProviderFailedOutcome,
                    process.Continuation(process.Edge(manual, "failed", Identities.PostTermsJoin))),
                process.RequestOutcome(
                    new($"{prefix}/manual/provider-timed-out"),
                    MotionDqInteractionContracts.RequirementProviderTimedOutOutcome,
                    process.Continuation(process.Edge(manual, "timedout", Identities.PostTermsJoin))),
                process.RequestOutcome(
                    new($"{prefix}/manual/cancelled"),
                    MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome,
                    process.Continuation(process.Edge(manual, "cancelled", Identities.PostTermsJoin)))
            ]);

        var requirementSubject = request.Field(static payload => payload.Requirement);
        AuthorRequirementEvaluation(
            process,
            transitions,
            vendorFulfilledApply,
            requirementSubject,
            vendorFulfilled.Value);
        AuthorRequirementEvaluation(
            process,
            transitions,
            manualFulfilledApply,
            requirementSubject,
            manualFulfilled.Value);

        return process.ForkBranch(
            new($"{prefix}/branch"),
            process.Edge(Identities.PostTermsFork, role, vendor));
    }

    static void AuthorRequirementEvaluation(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        MotionDqTransitionDefinitions transitions,
        ExecutionNodeId node,
        ProcessValue<MotionDqCaseRequirementReference> requirement,
        ProcessValue<MotionDqRequirementEvaluationReceipt> receipt) =>
        process.InvokeTransition(
            node,
            transitions.ApplyRequirementEvaluation.Reference,
            requirement,
            receipt,
            process.Continuation(process.Edge(node, "joined", Identities.PostTermsJoin)));

    static void AuthorActivations(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        MotionDqTransitionDefinitions transitions,
        ProcessValue<string> applicationId,
        ProcessValue<string> caseId)
    {
        var activations = process.Input.Field(static input => input.Activations);
        var applicant = activations.Field(static value => value.Applicant);
        var carrier = activations.Field(static value => value.CarrierOwnerOperator);
        var driver = activations.Field(static value => value.Driver);
        var truck = activations.Field(static value => value.Truck);
        var trailer = activations.Field(static value => value.Trailer);

        var activationsValid = IndependentActivationMatches(
            process,
            applicant,
            applicationId,
            MotionDqSubjectKind.Applicant,
            MotionDqVocabulary.Gates.ApplicantActivation);
        activationsValid = process.And(
            activationsValid,
            IndependentActivationMatches(
                process,
                carrier,
                applicationId,
                MotionDqSubjectKind.CarrierOwnerOperator,
                MotionDqVocabulary.Gates.CarrierActivation));
        activationsValid = process.And(
            activationsValid,
            ActivationBaseMatches(
                process,
                driver,
                applicationId,
                MotionDqSubjectKind.Driver,
                MotionDqVocabulary.Gates.DriverActivation));
        activationsValid = process.And(
            activationsValid,
            IndependentActivationMatches(
                process,
                truck,
                applicationId,
                MotionDqSubjectKind.Truck,
                MotionDqVocabulary.Gates.TruckActivation));
        activationsValid = process.And(
            activationsValid,
            IndependentActivationMatches(
                process,
                trailer,
                applicationId,
                MotionDqSubjectKind.Trailer,
                MotionDqVocabulary.Gates.TrailerActivation));

        var carrierSubject = carrier.Field(static value => value.Subject);
        var carrierAdmission = carrier.Field(static value => value.Admission);
        var driverSubject = driver.Field(static value => value.Subject);
        var driverAdmission = driver.Field(static value => value.Admission);
        var parentCarrierProof = driverAdmission.Field(static admission => admission.ParentCarrierProof);
        var provenCarrierSubject = parentCarrierProof.Field(static proof => proof!.CarrierSubject);
        activationsValid = process.And(
            activationsValid,
            IsNotNull(process, parentCarrierProof));
        activationsValid = process.And(
            activationsValid,
            EqualRegardlessOfNullability(
                process,
                driverSubject.Field(static subject => subject.ParentApplicationId),
                applicationId));
        activationsValid = process.And(
            activationsValid,
            process.Equal(provenCarrierSubject, carrierSubject));
        activationsValid = process.And(
            activationsValid,
            process.Equal(
                parentCarrierProof.Field(static proof => proof!.ActivationDecisionId),
                carrierAdmission.Field(static admission => admission.DecisionId)));
        activationsValid = process.And(
            activationsValid,
            process.NotEqual(
                parentCarrierProof.Field(static proof => proof!.EvidenceId),
                process.Constant(string.Empty)));
        AuthorGuard(process, Identities.ValidateActivations, activationsValid, Identities.ActivateCarrier);

        AuthorRequiredTransition(
            process,
            Identities.ActivateCarrier,
            transitions.ActivateSubject.Reference,
            carrierSubject,
            carrierAdmission,
            MotionDqTransitionOutcome.SubjectActivated,
            Identities.ActivationFork);

        var applicantBranch = AuthorActivationBranch(
            process,
            transitions,
            role: "applicant",
            applicant);
        var driverBranch = AuthorActivationBranch(
            process,
            transitions,
            role: "driver",
            driver);
        var truckBranch = AuthorActivationBranch(
            process,
            transitions,
            role: "truck",
            truck);
        var trailerBranch = AuthorActivationBranch(
            process,
            transitions,
            role: "trailer",
            trailer);
        process.Fork(
            Identities.ActivationFork,
            [applicantBranch, driverBranch, truckBranch, trailerBranch],
            Identities.ActivationJoin);
        process.Join(
            Identities.ActivationJoin,
            Identities.ActivationFork,
            AllJoinPolicy(),
            process.Edge(Identities.ActivationJoin, "complete", Identities.AdvanceActivationMilestone));
        AuthorRequiredTransition(
            process,
            Identities.AdvanceActivationMilestone,
            transitions.AdvanceCaseMilestone.Reference,
            caseId,
            process.Input.Field(static input => input.ActivationAdmission),
            MotionDqTransitionOutcome.MilestoneAdvanced,
            Identities.Completed);
    }

    static ProcessForkBranch AuthorActivationBranch(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process,
        MotionDqTransitionDefinitions transitions,
        string role,
        ProcessValue<MotionDqSubjectActivationInvocation> activation)
    {
        var node = new ExecutionNodeId($"motion-dq/activation/{role}");
        process.InvokeTransition(
            node,
            transitions.ActivateSubject.Reference,
            activation.Field(static value => value.Subject),
            activation.Field(static value => value.Admission),
            process.Continuation(process.Edge(node, "joined", Identities.ActivationJoin)));
        return process.ForkBranch(
            new($"motion-dq/activation/{role}/branch"),
            process.Edge(Identities.ActivationFork, role, node));
    }

    static void AuthorTerminals(
        ProcessBuilder<MotionDqOnboardingInput, MotionDqOnboardingOutcome> process)
    {
        process.Return(Identities.Completed, process.Constant(MotionDqOnboardingOutcome.Completed));
        process.Return(Identities.NotEligible, process.Constant(MotionDqOnboardingOutcome.NotEligible));
        process.Return(Identities.Cancelled, process.Constant(MotionDqOnboardingOutcome.Cancelled));
        process.Return(Identities.ReviewTimedOut, process.Constant(MotionDqOnboardingOutcome.ReviewTimedOut));
        process.Fail(Identities.ReviewTaskFailed, process.Constant(MotionDqOnboardingOutcome.ReviewTaskFailed));
        process.Return(
            Identities.InsuranceTermsDeclined,
            process.Constant(MotionDqOnboardingOutcome.InsuranceTermsDeclined));
        process.Fail(
            Identities.InsuranceTermsFailed,
            process.Constant(MotionDqOnboardingOutcome.InsuranceTermsFailed));
        process.Return(
            Identities.InsuranceTermsTimedOut,
            process.Constant(MotionDqOnboardingOutcome.InsuranceTermsTimedOut));
        process.Return(
            Identities.InsuranceTermsCancelled,
            process.Constant(MotionDqOnboardingOutcome.InsuranceTermsCancelled));
        process.Fail(
            Identities.CoordinationRejected,
            process.Constant(MotionDqOnboardingOutcome.CoordinationRejected));
    }

    static ProcessJoinPolicy AllJoinPolicy() => new(
        ProcessJoinMode.All,
        requiredCount: 0,
        ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCancellationPolicy.AwaitRemaining,
        ProcessJoinCompletionOrder.Unobservable,
        ProcessJoinTieBreak.BranchIdentity);

    static string LastPathSegment(string value)
    {
        var separator = value.LastIndexOf('/');
        return separator < 0 ? value : value[(separator + 1)..];
    }

    static ExecutionProvenance Provenance() => new(
        new(ProcessAuthoring.Producer, "1"),
        new("ari-181/motion-dq/onboarding-process"),
        DocumentOrigin.User);

    static void RequireValid(DocumentValidationResult validation, string stage)
    {
        if (validation.IsValid)
            return;

        throw new InvalidOperationException(
            $"Motion DQ {stage} failed: {string.Join("; ", validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"))}");
    }

    static class Identities
    {
        public static readonly ExecutionNodeId SubmitPrequalification = new("motion-dq/case/submit-prequalification");
        public static readonly ExecutionNodeId SubmitFullApplication = new("motion-dq/case/submit-full-application");
        public static readonly ExecutionNodeId ValidateReviewTask = new("motion-dq/review/validate-task-request");
        public static readonly ExecutionNodeId CreateReviewTask = new("motion-dq/review/create-task");
        public static readonly ExecutionNodeId AwaitReview = new("motion-dq/review/await-match");
        public static readonly ExecutionNodeId RecordHire = new("motion-dq/review/record-hire");
        public static readonly ExecutionNodeId RecordHold = new("motion-dq/review/record-hold");
        public static readonly ExecutionNodeId RecordNotEligible = new("motion-dq/review/record-not-eligible");
        public static readonly ExecutionNodeId RecordCancellation = new("motion-dq/review/record-cancellation");
        public static readonly ExecutionNodeId RecordReviewTimeout = new("motion-dq/review/record-timeout");
        public static readonly ExecutionNodeId ValidateInsuranceRequest = new("motion-dq/insurance-terms/validate-request");
        public static readonly ExecutionNodeId InsuranceTerms = new("motion-dq/insurance-terms/request");
        public static readonly ExecutionNodeId ValidateAcceptedInsuranceTerms =
            new("motion-dq/insurance-terms/validate-accepted");
        public static readonly ExecutionNodeId ValidateDeclinedInsuranceTerms =
            new("motion-dq/insurance-terms/validate-declined");
        public static readonly ExecutionNodeId ApplyAcceptedInsuranceTerms = new("motion-dq/insurance-terms/apply-accepted");
        public static readonly ExecutionNodeId ApplyDeclinedInsuranceTerms = new("motion-dq/insurance-terms/apply-declined");
        public static readonly ExecutionNodeId AdvanceInsuranceTermsMilestone =
            new("motion-dq/case/advance-insurance-terms");
        public static readonly ExecutionNodeId ValidatePostTerms = new("motion-dq/post-terms/validate-requests");
        public static readonly ExecutionNodeId PostTermsFork = new("motion-dq/post-terms/fork");
        public static readonly ExecutionNodeId PostTermsJoin = new("motion-dq/post-terms/join");
        public static readonly ExecutionNodeId AdvancePostTermsMilestone =
            new("motion-dq/case/advance-post-terms");
        public static readonly ExecutionNodeId ValidateActivations = new("motion-dq/activation/validate-input");
        public static readonly ExecutionNodeId ActivateCarrier = new("motion-dq/activation/carrier-owner-operator");
        public static readonly ExecutionNodeId ActivationFork = new("motion-dq/activation/independent/fork");
        public static readonly ExecutionNodeId ActivationJoin = new("motion-dq/activation/independent/join");
        public static readonly ExecutionNodeId AdvanceActivationMilestone =
            new("motion-dq/case/advance-activation");
        public static readonly ExecutionNodeId Completed = new("motion-dq/terminal/completed");
        public static readonly ExecutionNodeId NotEligible = new("motion-dq/terminal/not-eligible");
        public static readonly ExecutionNodeId Cancelled = new("motion-dq/terminal/cancelled");
        public static readonly ExecutionNodeId ReviewTimedOut = new("motion-dq/terminal/review-timed-out");
        public static readonly ExecutionNodeId ReviewTaskFailed = new("motion-dq/terminal/review-task-failed");
        public static readonly ExecutionNodeId InsuranceTermsDeclined = new("motion-dq/terminal/insurance-terms-declined");
        public static readonly ExecutionNodeId InsuranceTermsFailed = new("motion-dq/terminal/insurance-terms-failed");
        public static readonly ExecutionNodeId InsuranceTermsTimedOut = new("motion-dq/terminal/insurance-terms-timed-out");
        public static readonly ExecutionNodeId InsuranceTermsCancelled = new("motion-dq/terminal/insurance-terms-cancelled");
        public static readonly ExecutionNodeId CoordinationRejected = new("motion-dq/terminal/coordination-rejected");
    }
}
