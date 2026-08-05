using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using AuthoredProcess = Cohesive.Processes.Authoring.Process<
    Cohesive.ExecutionKernel.TestFixtures.MotionDq.MotionDqOnboardingInput,
    Cohesive.ExecutionKernel.TestFixtures.MotionDq.MotionDqOnboardingOutcome>;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

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
        var authored = Author();
        RequireValid(authored.Validation, "context-free Process authoring");
        var compilation = authored.Compile(linkingContext);
        RequireValid(compilation.Validation, "linked Process compilation");
        if (compilation.Plan is null)
        {
            throw new InvalidOperationException("Linked Motion DQ Process compilation produced no reference plan.");
        }

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

    static AuthoredProcess Author() =>
        MotionDqProcessDefinition.Define(
            new(
                new("process/motion-dq/onboarding"),
                new("revision/1"),
                Identities.SubmitPrequalification,
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance(),
                displayName: "Motion DQ onboarding",
                description: "Coordinates typed review, terms, fulfillment, and subject activation semantics."));

    static ExecutionProvenance Provenance() => new(
        new(ProcessAuthoring.Producer, "1"),
        new("ari-181/motion-dq/onboarding-process"),
        DocumentOrigin.User);

    static void RequireValid(DocumentValidationResult validation, string stage)
    {
        if (validation.IsValid)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Motion DQ {stage} failed: {string.Join("; ", validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"))}");
    }

    internal static ExecutionNodeId RequiredOutcome(ExecutionNodeId invocation) =>
        new($"{invocation.Value}/require-outcome");

    internal static ExecutionNodeId Accepted(ExecutionNodeId decision) =>
        new($"{decision.Value}/accepted");

    internal static ExecutionNodeId Rejected(ExecutionNodeId decision) =>
        new($"{decision.Value}/rejected");

    internal static ExecutionNodeId PostTerms(string requirement, string suffix) =>
        new($"motion-dq/post-terms/{requirement}/{suffix}");

    internal static string LastSegment(string value)
    {
        var separator = value.LastIndexOf('/');
        return separator < 0 ? value : value[(separator + 1)..];
    }

    internal static ExecutionNodeId Activation(string role) => new($"motion-dq/activation/{role}");

    internal static ExecutionNodeId ActivationBranch(string role) =>
        new($"motion-dq/activation/{role}/branch");

    internal static class Identities
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
        public static readonly ExecutionNodeId ReviewTaskCreatedOutcome = new("motion-dq/review-task/created");
        public static readonly ExecutionNodeId ReviewTaskFailedOutcome = new("motion-dq/review-task/failed");
        public static readonly ExecutionNodeId ReviewCancelledClause = new("motion-dq/review/cancelled");
        public static readonly ExecutionNodeId ReviewNotEligibleClause = new("motion-dq/review/not-eligible");
        public static readonly ExecutionNodeId ReviewHireClause = new("motion-dq/review/hire");
        public static readonly ExecutionNodeId ReviewHoldClause = new("motion-dq/review/hold");
        public static readonly ExecutionNodeId ReviewTimedOutClause = new("motion-dq/review/timed-out");
        public static readonly ExecutionNodeId InsuranceTermsDeclined = new("motion-dq/terminal/insurance-terms-declined");
        public static readonly ExecutionNodeId InsuranceTermsFailed = new("motion-dq/terminal/insurance-terms-failed");
        public static readonly ExecutionNodeId InsuranceTermsTimedOut = new("motion-dq/terminal/insurance-terms-timed-out");
        public static readonly ExecutionNodeId InsuranceTermsCancelled = new("motion-dq/terminal/insurance-terms-cancelled");
        public static readonly ExecutionNodeId InsuranceTermsAcceptedOutcome = new("motion-dq/insurance-terms/accepted");
        public static readonly ExecutionNodeId InsuranceTermsDeclinedOutcome = new("motion-dq/insurance-terms/declined");
        public static readonly ExecutionNodeId InsuranceTermsFailedOutcome = new("motion-dq/insurance-terms/failed");
        public static readonly ExecutionNodeId InsuranceTermsTimedOutOutcome = new("motion-dq/insurance-terms/timed-out");
        public static readonly ExecutionNodeId InsuranceTermsCancelledOutcome = new("motion-dq/insurance-terms/cancelled");
        public static readonly ExecutionNodeId CoordinationRejected = new("motion-dq/terminal/coordination-rejected");
    }
}
