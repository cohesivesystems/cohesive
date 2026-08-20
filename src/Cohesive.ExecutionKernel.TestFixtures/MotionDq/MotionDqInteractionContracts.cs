using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;

namespace Cohesive.ExecutionKernel.TestFixtures.MotionDq;

/// <summary>Typed request for one exact revision of the insurance terms presented to an onboarding case.</summary>
/// <param name="CaseId">Stable onboarding-case identity.</param>
/// <param name="TermsRevision">Exact insurance-terms revision that must be accepted.</param>
public sealed record MotionDqInsuranceTermsRequest(string CaseId, string TermsRevision);

/// <summary>Typed terminal decision for an insurance-terms Request.</summary>
/// <param name="CaseId">Stable onboarding-case identity.</param>
/// <param name="TermsRevision">Exact insurance-terms revision decided by the applicant.</param>
/// <param name="DecidedAtUtc">Authoritative decision time.</param>
/// <param name="Evaluation">Endogenous evaluation that settles the independently owned terms requirement.</param>
public sealed record MotionDqInsuranceTermsResult(
    string CaseId,
    string TermsRevision,
    DateTimeOffset DecidedAtUtc,
    MotionDqRequirementEvaluationReceipt Evaluation);

/// <summary>
/// Durable evidence that one physical fulfillment attempt ended without producing an authoritative requirement
/// evaluation.
/// </summary>
/// <param name="ProviderAttemptId">Stable identity of the physical provider attempt.</param>
/// <param name="Requirement">Exact case-scoped requirement addressed by the attempt.</param>
/// <param name="EvidenceNeedId">Exact evidence need the provider attempted to fulfill.</param>
/// <param name="ReasonCode">Inspectable provider-neutral reason for failure, timeout, or cancellation.</param>
/// <param name="ObservedAtUtc">Authoritative time at which the terminal attempt evidence was observed.</param>
/// <remarks>
/// The enclosing Reply outcome classifies this evidence as failure, timeout, or cancellation. This payload cannot
/// settle requirement state: only <see cref="MotionDqRequirementEvaluationReceipt"/> is accepted by the requirement
/// Transition.
/// </remarks>
public sealed record MotionDqRequirementFulfillmentFailure(
    string ProviderAttemptId,
    MotionDqCaseRequirementReference Requirement,
    string EvidenceNeedId,
    string ReasonCode,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Canonical interaction-contract authority for the Motion DQ onboarding fixture.
/// </summary>
/// <remarks>
/// Vendor and manual fulfillment are physical interpretations of <see cref="FulfillRequirementRequest"/> at
/// different Process-node occurrences. They intentionally share one exact Request contract and one durable binding.
/// </remarks>
public sealed class MotionDqInteractionContracts
{
    static readonly ExecutionRevisionId Revision = new("revision/1");
    static readonly IClrTypeRefMapper TypeMapper = new DefaultClrTypeRefMapper();

    /// <summary>Review task was created and the case may begin waiting for a caseworker decision.</summary>
    public static RequestTerminalOutcomeId ReviewTaskCreatedOutcome { get; } = new("created");

    /// <summary>Review task creation failed terminally.</summary>
    public static RequestTerminalOutcomeId ReviewTaskFailedOutcome { get; } = new("failed");

    /// <summary>The applicant accepted the exact insurance-terms revision.</summary>
    public static RequestTerminalOutcomeId InsuranceTermsAcceptedOutcome { get; } = new("accepted");

    /// <summary>The applicant declined the exact insurance-terms revision.</summary>
    public static RequestTerminalOutcomeId InsuranceTermsDeclinedOutcome { get; } = new("declined");

    /// <summary>The insurance-terms provider failed terminally.</summary>
    public static RequestTerminalOutcomeId InsuranceTermsFailedOutcome { get; } = new("failed");

    /// <summary>The insurance-terms Request reached its declared semantic timeout.</summary>
    public static RequestTerminalOutcomeId InsuranceTermsTimedOutOutcome { get; } = new("timed-out");

    /// <summary>The insurance-terms Request was cancelled.</summary>
    public static RequestTerminalOutcomeId InsuranceTermsCancelledOutcome { get; } = new("cancelled");

    /// <summary>A provider produced a requirement-evaluation receipt.</summary>
    public static RequestTerminalOutcomeId RequirementFulfilledOutcome { get; } = new("fulfilled");

    /// <summary>The selected provider failed and another provider may be attempted.</summary>
    public static RequestTerminalOutcomeId RequirementProviderFailedOutcome { get; } = new("provider-failed");

    /// <summary>The selected provider reached its declared semantic timeout.</summary>
    public static RequestTerminalOutcomeId RequirementProviderTimedOutOutcome { get; } = new("provider-timed-out");

    /// <summary>The requirement-fulfillment Request was cancelled without an authoritative evaluation.</summary>
    public static RequestTerminalOutcomeId RequirementFulfillmentCancelledOutcome { get; } = new("cancelled");

    MotionDqInteractionContracts(
        DomainEventContractReference prequalificationSubmittedEvent,
        DomainEventContractReference prequalificationAuditEvent,
        SignalContractReference reviewDecisionSignal,
        SignalContractReference caseCancellationSignal,
        RequestContractReference reviewTaskRequest,
        DurableRequestBinding reviewTaskBinding,
        RequestContractReference insuranceTermsRequest,
        DurableRequestBinding insuranceTermsBinding,
        RequestContractReference fulfillRequirementRequest,
        DurableRequestBinding fulfillRequirementBinding,
        ImmutableArray<ExecutionDefinitionDocument> documents,
        InteractionContractCatalog catalog)
    {
        PrequalificationSubmittedEvent = prequalificationSubmittedEvent;
        PrequalificationAuditEvent = prequalificationAuditEvent;
        ReviewDecisionSignal = reviewDecisionSignal;
        CaseCancellationSignal = caseCancellationSignal;
        ReviewTaskRequest = reviewTaskRequest;
        ReviewTaskBinding = reviewTaskBinding;
        InsuranceTermsRequest = insuranceTermsRequest;
        InsuranceTermsBinding = insuranceTermsBinding;
        FulfillRequirementRequest = fulfillRequirementRequest;
        FulfillRequirementBinding = fulfillRequirementBinding;
        Documents = documents;
        Catalog = catalog;
    }

    /// <summary>Canonical version-one Motion DQ interaction-contract set.</summary>
    public static MotionDqInteractionContracts Version1 { get; } = CreateVersion1();

    /// <summary>Canonical event announcing an accepted prequalification submission.</summary>
    public DomainEventContractReference PrequalificationSubmittedEvent { get; }

    /// <summary>Canonical audit event retaining the accepted prequalification evidence.</summary>
    public DomainEventContractReference PrequalificationAuditEvent { get; }

    /// <summary>Exact Signal contract carrying a typed caseworker review decision.</summary>
    public SignalContractReference ReviewDecisionSignal { get; }

    /// <summary>Exact Signal contract carrying a typed onboarding-case cancellation.</summary>
    public SignalContractReference CaseCancellationSignal { get; }

    /// <summary>Exact Request contract used to create the caseworker review task.</summary>
    public RequestContractReference ReviewTaskRequest { get; }

    /// <summary>Durable interpretation policy for <see cref="ReviewTaskRequest"/>.</summary>
    public DurableRequestBinding ReviewTaskBinding { get; }

    /// <summary>Exact Request contract gating post-terms requirements on accepted insurance terms.</summary>
    public RequestContractReference InsuranceTermsRequest { get; }

    /// <summary>Durable interpretation policy for <see cref="InsuranceTermsRequest"/>.</summary>
    public DurableRequestBinding InsuranceTermsBinding { get; }

    /// <summary>
    /// Exact provider-neutral Request contract reused by vendor and manual fulfillment node occurrences.
    /// </summary>
    public RequestContractReference FulfillRequirementRequest { get; }

    /// <summary>Shared durable interpretation policy for every fulfillment occurrence.</summary>
    public DurableRequestBinding FulfillRequirementBinding { get; }

    /// <summary>Complete canonical Signal, Request, and Reply documents in deterministic construction order.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Documents { get; }

    /// <summary>Validated exact-reference catalog assembled from <see cref="Documents"/>.</summary>
    public InteractionContractCatalog Catalog { get; }

    static MotionDqInteractionContracts CreateVersion1()
    {
        var prequalificationSubmittedDocument = InteractionContractDocuments.Create(
            new("interaction/motion-dq/prequalification-submitted"),
            Revision,
            new DomainEventContractDefinition(
                Schema<MotionDqPrequalificationSubmission>("motion-dq/prequalification-submitted/v1")),
            Provenance("prequalification-submitted"));
        var prequalificationAuditDocument = InteractionContractDocuments.Create(
            new("interaction/motion-dq/prequalification-audit"),
            Revision,
            new DomainEventContractDefinition(
                Schema<MotionDqPrequalificationSubmission>("motion-dq/prequalification-audit/v1")),
            Provenance("prequalification-audit"));
        var reviewDecisionDocument = InteractionContractDocuments.Create(
            new("interaction/motion-dq/review-decision"),
            Revision,
            new SignalContractDefinition(Schema<MotionDqReviewDecision>("motion-dq/review-decision/v1")),
            Provenance("review-decision"));
        var cancellationDocument = InteractionContractDocuments.Create(
            new("interaction/motion-dq/case-cancellation"),
            Revision,
            new SignalContractDefinition(Schema<MotionDqCancellation>("motion-dq/case-cancellation/v1")),
            Provenance("case-cancellation"));

        var reviewTask = InteractionContractAuthoring.CreateRequestProtocol<
            MotionDqReviewTaskRequest,
            ReviewTaskOutcomes>(
            definitionId: new("interaction/motion-dq/create-review-task"),
            revisionId: Revision,
            payloadRevision: new("motion-dq/review-task/request/v1"),
            createOutcomes: outcomes => new(
                Created: outcomes.Result<MotionDqReviewTaskReference>(
                    ReviewTaskCreatedOutcome,
                    new("motion-dq/review-task/reference/v1")),
                Failed: outcomes.Failure<string>(
                    ReviewTaskFailedOutcome,
                    new("motion-dq/review-task/failure/v1"))),
            responsePolicy: ResponsePolicy(
                RequestOptionalTerminalSemantics.Unsupported,
                RequestOptionalTerminalSemantics.Unsupported),
            provenance: Provenance("interaction/motion-dq/create-review-task/request"),
            replyProvenance: ReplyProvenance("interaction/motion-dq/create-review-task"));
        var reviewTaskBinding = reviewTask.BindDurably(
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            terminalFailureOutcome: reviewTask.Outcomes.Failed);

        var insuranceTerms = InteractionContractAuthoring.CreateRequestProtocol<
            MotionDqInsuranceTermsRequest,
            InsuranceTermsOutcomes>(
            definitionId: new("interaction/motion-dq/insurance-terms"),
            revisionId: Revision,
            payloadRevision: new("motion-dq/insurance-terms/request/v1"),
            createOutcomes: outcomes => new(
                Accepted: outcomes.Result<MotionDqInsuranceTermsResult>(
                    InsuranceTermsAcceptedOutcome,
                    new("motion-dq/insurance-terms/result/v1")),
                Declined: outcomes.Result<MotionDqInsuranceTermsResult>(
                    InsuranceTermsDeclinedOutcome,
                    new("motion-dq/insurance-terms/result/v1")),
                Failed: outcomes.Failure<string>(
                    InsuranceTermsFailedOutcome,
                    new("motion-dq/insurance-terms/failure/v1")),
                TimedOut: outcomes.Timeout<string>(
                    InsuranceTermsTimedOutOutcome,
                    new("motion-dq/insurance-terms/timeout/v1")),
                Cancelled: outcomes.Cancellation<string>(
                    InsuranceTermsCancelledOutcome,
                    new("motion-dq/insurance-terms/cancellation/v1"))),
            responsePolicy: ResponsePolicy(
                RequestOptionalTerminalSemantics.TerminalOutcome,
                RequestOptionalTerminalSemantics.TerminalOutcome),
            provenance: Provenance("interaction/motion-dq/insurance-terms/request"),
            replyProvenance: ReplyProvenance("interaction/motion-dq/insurance-terms"));
        var insuranceTermsBinding = insuranceTerms.BindDurably(
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            timeoutAfter: TimeSpan.FromDays(7),
            terminalFailureOutcome: insuranceTerms.Outcomes.Failed);

        var fulfillRequirement = InteractionContractAuthoring.CreateRequestProtocol<
            MotionDqRequirementFulfillmentRequest,
            FulfillRequirementOutcomes>(
            definitionId: new("interaction/motion-dq/fulfill-requirement"),
            revisionId: Revision,
            payloadRevision: new("motion-dq/requirement/fulfillment-request/v1"),
            createOutcomes: outcomes => new(
                Fulfilled: outcomes.Result<MotionDqRequirementEvaluationReceipt>(
                    RequirementFulfilledOutcome,
                    new("motion-dq/requirement/evaluation-receipt/v1")),
                ProviderFailed: outcomes.Failure<MotionDqRequirementFulfillmentFailure>(
                    RequirementProviderFailedOutcome,
                    new("motion-dq/requirement/fulfillment-failure/v1")),
                ProviderTimedOut: outcomes.Timeout<MotionDqRequirementFulfillmentFailure>(
                    RequirementProviderTimedOutOutcome,
                    new("motion-dq/requirement/fulfillment-failure/v1")),
                Cancelled: outcomes.Cancellation<MotionDqRequirementFulfillmentFailure>(
                    RequirementFulfillmentCancelledOutcome,
                    new("motion-dq/requirement/fulfillment-failure/v1"))),
            responsePolicy: ResponsePolicy(
                RequestOptionalTerminalSemantics.TerminalOutcome,
                RequestOptionalTerminalSemantics.TerminalOutcome),
            provenance: Provenance("interaction/motion-dq/fulfill-requirement/request"),
            replyProvenance: ReplyProvenance("interaction/motion-dq/fulfill-requirement"));
        var fulfillRequirementBinding = fulfillRequirement.BindDurably(
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            timeoutAfter: TimeSpan.FromDays(1),
            terminalFailureOutcome: fulfillRequirement.Outcomes.ProviderFailed);

        ImmutableArray<ExecutionDefinitionDocument> documents =
        [
            prequalificationSubmittedDocument,
            prequalificationAuditDocument,
            reviewDecisionDocument,
            cancellationDocument,
            .. reviewTask.Documents,
            .. insuranceTerms.Documents,
            .. fulfillRequirement.Documents
        ];
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        if (!validation.IsValid || catalog is null)
        {
            throw new InvalidOperationException(
                $"Motion DQ interaction contracts are invalid: {Format(validation)}");
        }

        return new(
            prequalificationSubmittedEvent: new(Reference(prequalificationSubmittedDocument)),
            prequalificationAuditEvent: new(Reference(prequalificationAuditDocument)),
            reviewDecisionSignal: new(Reference(reviewDecisionDocument)),
            caseCancellationSignal: new(Reference(cancellationDocument)),
            reviewTaskRequest: reviewTask.Request,
            reviewTaskBinding: reviewTaskBinding,
            insuranceTermsRequest: insuranceTerms.Request,
            insuranceTermsBinding: insuranceTermsBinding,
            fulfillRequirementRequest: fulfillRequirement.Request,
            fulfillRequirementBinding: fulfillRequirementBinding,
            documents: documents,
            catalog: catalog);
    }

    static RequestProtocolResponsePolicy ResponsePolicy(
        RequestOptionalTerminalSemantics timeout,
        RequestOptionalTerminalSemantics cancellation) => new(
        timeout: timeout,
        cancellation: cancellation,
        lateResult: RequestResultDisposition.Observe,
        staleResult: RequestResultDisposition.Reject,
        duplicateResult: RequestResultDisposition.ReusePriorDisposition,
        retry: RequestRetrySemantics.StableIdentity,
        ambiguousOutcome: RequestResolutionSemantics.TerminalFailure,
        unresolvedOutcome: RequestResolutionSemantics.TerminalFailure,
        retentionHorizon: TimeSpan.FromDays(30));

    static Func<RequestTerminalOutcomeId, ExecutionProvenance> ReplyProvenance(string definitionId) =>
        outcome => Provenance($"{definitionId}/reply/{outcome.Value}");

    static InteractionValueSchema Schema<TValue>(string revision) => new(
        new ValueContract(TypeMapper.Map(typeof(TValue), null)),
        new(revision));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance(string source) => new(
        new("cohesive-motion-dq-fixture", "1"),
        new($"ari-181/{source}"),
        DocumentOrigin.User);

    static string Format(DocumentValidationResult validation) => string.Join(
        "; ",
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed record ReviewTaskOutcomes(
        RequestProtocolOutcome<MotionDqReviewTaskReference> Created,
        RequestProtocolOutcome<string> Failed);

    sealed record InsuranceTermsOutcomes(
        RequestProtocolOutcome<MotionDqInsuranceTermsResult> Accepted,
        RequestProtocolOutcome<MotionDqInsuranceTermsResult> Declined,
        RequestProtocolOutcome<string> Failed,
        RequestProtocolOutcome<string> TimedOut,
        RequestProtocolOutcome<string> Cancelled);

    sealed record FulfillRequirementOutcomes(
        RequestProtocolOutcome<MotionDqRequirementEvaluationReceipt> Fulfilled,
        RequestProtocolOutcome<MotionDqRequirementFulfillmentFailure> ProviderFailed,
        RequestProtocolOutcome<MotionDqRequirementFulfillmentFailure> ProviderTimedOut,
        RequestProtocolOutcome<MotionDqRequirementFulfillmentFailure> Cancelled);
}
