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
        RequestProtocol reviewTask,
        RequestProtocol insuranceTerms,
        RequestProtocol fulfillRequirement,
        ImmutableArray<ExecutionDefinitionDocument> documents,
        InteractionContractCatalog catalog)
    {
        PrequalificationSubmittedEvent = prequalificationSubmittedEvent;
        PrequalificationAuditEvent = prequalificationAuditEvent;
        ReviewDecisionSignal = reviewDecisionSignal;
        CaseCancellationSignal = caseCancellationSignal;
        ReviewTaskRequest = reviewTask.Contract;
        ReviewTaskBinding = reviewTask.Binding;
        InsuranceTermsRequest = insuranceTerms.Contract;
        InsuranceTermsBinding = insuranceTerms.Binding;
        FulfillRequirementRequest = fulfillRequirement.Contract;
        FulfillRequirementBinding = fulfillRequirement.Binding;
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

        var reviewTask = CreateRequestProtocol(
            definitionId: "interaction/motion-dq/create-review-task",
            payload: Schema<MotionDqReviewTaskRequest>("motion-dq/review-task/request/v1"),
            outcomes:
            [
                new(ReviewTaskCreatedOutcome, RequestOutcomeKind.Result,
                    Schema<MotionDqReviewTaskReference>("motion-dq/review-task/reference/v1")),
                new(ReviewTaskFailedOutcome, RequestOutcomeKind.Failure,
                    Schema<string>("motion-dq/review-task/failure/v1"))
            ],
            timeout: RequestOptionalTerminalSemantics.Unsupported,
            cancellation: RequestOptionalTerminalSemantics.Unsupported,
            timeoutAfter: null,
            maxAttempts: 3,
            terminalFailureOutcome: ReviewTaskFailedOutcome);

        var insuranceTerms = CreateRequestProtocol(
            definitionId: "interaction/motion-dq/insurance-terms",
            payload: Schema<MotionDqInsuranceTermsRequest>("motion-dq/insurance-terms/request/v1"),
            outcomes:
            [
                new(InsuranceTermsAcceptedOutcome, RequestOutcomeKind.Result,
                    Schema<MotionDqInsuranceTermsResult>("motion-dq/insurance-terms/result/v1")),
                new(InsuranceTermsDeclinedOutcome, RequestOutcomeKind.Result,
                    Schema<MotionDqInsuranceTermsResult>("motion-dq/insurance-terms/result/v1")),
                new(InsuranceTermsFailedOutcome, RequestOutcomeKind.Failure,
                    Schema<string>("motion-dq/insurance-terms/failure/v1")),
                new(InsuranceTermsTimedOutOutcome, RequestOutcomeKind.Timeout,
                    Schema<string>("motion-dq/insurance-terms/timeout/v1")),
                new(InsuranceTermsCancelledOutcome, RequestOutcomeKind.Cancellation,
                    Schema<string>("motion-dq/insurance-terms/cancellation/v1"))
            ],
            timeout: RequestOptionalTerminalSemantics.TerminalOutcome,
            cancellation: RequestOptionalTerminalSemantics.TerminalOutcome,
            timeoutAfter: TimeSpan.FromDays(7),
            maxAttempts: 3,
            terminalFailureOutcome: InsuranceTermsFailedOutcome);

        var fulfillmentReceipt = Schema<MotionDqRequirementEvaluationReceipt>(
            "motion-dq/requirement/evaluation-receipt/v1");
        var fulfillmentFailure = Schema<MotionDqRequirementFulfillmentFailure>(
            "motion-dq/requirement/fulfillment-failure/v1");
        var fulfillRequirement = CreateRequestProtocol(
            definitionId: "interaction/motion-dq/fulfill-requirement",
            payload: Schema<MotionDqRequirementFulfillmentRequest>("motion-dq/requirement/fulfillment-request/v1"),
            outcomes:
            [
                new(RequirementFulfilledOutcome, RequestOutcomeKind.Result, fulfillmentReceipt),
                new(RequirementProviderFailedOutcome, RequestOutcomeKind.Failure, fulfillmentFailure),
                new(RequirementProviderTimedOutOutcome, RequestOutcomeKind.Timeout, fulfillmentFailure),
                new(RequirementFulfillmentCancelledOutcome, RequestOutcomeKind.Cancellation, fulfillmentFailure)
            ],
            timeout: RequestOptionalTerminalSemantics.TerminalOutcome,
            cancellation: RequestOptionalTerminalSemantics.TerminalOutcome,
            timeoutAfter: TimeSpan.FromDays(1),
            maxAttempts: 3,
            terminalFailureOutcome: RequirementProviderFailedOutcome);

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
            reviewTask: reviewTask,
            insuranceTerms: insuranceTerms,
            fulfillRequirement: fulfillRequirement,
            documents: documents,
            catalog: catalog);
    }

    static RequestProtocol CreateRequestProtocol(
        string definitionId,
        InteractionValueSchema payload,
        ImmutableArray<RequestOutcome> outcomes,
        RequestOptionalTerminalSemantics timeout,
        RequestOptionalTerminalSemantics cancellation,
        TimeSpan? timeoutAfter,
        int maxAttempts,
        RequestTerminalOutcomeId terminalFailureOutcome)
    {
        var terminalOutcomes = ImmutableArray.CreateBuilder<RequestTerminalOutcomeDefinition>(outcomes.Length);
        foreach (var outcome in outcomes)
        {
            terminalOutcomes.Add(outcome.Kind switch
            {
                RequestOutcomeKind.Result => new RequestResultDefinition(outcome.Id, outcome.Schema),
                RequestOutcomeKind.Failure => new RequestFailureDefinition(outcome.Id, outcome.Schema),
                RequestOutcomeKind.Timeout => new RequestTimeoutDefinition(outcome.Id, outcome.Schema),
                RequestOutcomeKind.Cancellation => new RequestCancellationDefinition(outcome.Id, outcome.Schema),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(outcomes), outcome.Kind, "Unsupported Motion DQ Request outcome kind.")
            });
        }

        var requestDocument = InteractionContractDocuments.Create(
            new(definitionId),
            Revision,
            new RequestContractDefinition(
                payload,
                new(
                    terminalOutcomes: terminalOutcomes.MoveToImmutable(),
                    timeout: timeout,
                    cancellation: cancellation,
                    lateResult: RequestResultDisposition.Observe,
                    staleResult: RequestResultDisposition.Reject,
                    duplicateResult: RequestResultDisposition.ReusePriorDisposition,
                    retry: RequestRetrySemantics.StableIdentity,
                    ambiguousOutcome: RequestResolutionSemantics.TerminalFailure,
                    unresolvedOutcome: RequestResolutionSemantics.TerminalFailure,
                    retentionHorizon: TimeSpan.FromDays(30))),
            Provenance($"{definitionId}/request"));
        RequestContractReference request = new(Reference(requestDocument));

        var documents = ImmutableArray.CreateBuilder<ExecutionDefinitionDocument>(outcomes.Length + 1);
        var replies = ImmutableArray.CreateBuilder<DurableReplyBinding>(outcomes.Length);
        documents.Add(requestDocument);
        foreach (var outcome in outcomes)
        {
            var replyDocument = InteractionContractDocuments.Create(
                new($"{definitionId}/reply/{outcome.Id.Value}"),
                Revision,
                new ReplyContractDefinition(request, outcome.Id),
                Provenance($"{definitionId}/reply/{outcome.Id.Value}"));
            documents.Add(replyDocument);
            replies.Add(new(outcome.Id, new(Reference(replyDocument))));
        }

        var binding = new DurableRequestBinding(
            request: request,
            replies: replies.MoveToImmutable(),
            maxAttempts: maxAttempts,
            claimLease: TimeSpan.FromMinutes(5),
            timeoutAfter: timeoutAfter,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            terminalFailureOutcome: terminalFailureOutcome);
        return new(
            Contract: request,
            Binding: binding,
            Documents: documents.MoveToImmutable());
    }

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

    sealed record RequestProtocol(
        RequestContractReference Contract,
        DurableRequestBinding Binding,
        ImmutableArray<ExecutionDefinitionDocument> Documents);

    readonly record struct RequestOutcome(
        RequestTerminalOutcomeId Id,
        RequestOutcomeKind Kind,
        InteractionValueSchema Schema);

    enum RequestOutcomeKind
    {
        Result,
        Failure,
        Timeout,
        Cancellation
    }
}
