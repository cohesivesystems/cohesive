using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

internal sealed class DurableOperationTestFixture
{
    internal static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    internal static readonly ValueContract StringContract =
        new(new ScalarTypeRef(ScalarTypeKind.String));

    DurableOperationTestFixture(
        InteractionContractCatalog catalog,
        ExecutionDefinitionDocument requestDocument,
        ExecutionDefinitionDocument resultReplyDocument,
        ExecutionDefinitionDocument failureReplyDocument,
        ExecutionDefinitionDocument? timeoutReplyDocument,
        ExecutionDefinitionDocument? cancellationReplyDocument,
        RequestResponseObligation response,
        DurableRequestBinding binding)
    {
        Catalog = catalog;
        RequestDocument = requestDocument;
        ResultReplyDocument = resultReplyDocument;
        FailureReplyDocument = failureReplyDocument;
        TimeoutReplyDocument = timeoutReplyDocument;
        CancellationReplyDocument = cancellationReplyDocument;
        Response = response;
        Binding = binding;
        Executor = new(catalog);
    }

    internal InteractionContractCatalog Catalog { get; }

    internal DurableOperationReferenceExecutor Executor { get; }

    internal ExecutionDefinitionDocument RequestDocument { get; }

    internal ExecutionDefinitionDocument ResultReplyDocument { get; }

    internal ExecutionDefinitionDocument FailureReplyDocument { get; }

    internal ExecutionDefinitionDocument? TimeoutReplyDocument { get; }

    internal ExecutionDefinitionDocument? CancellationReplyDocument { get; }

    internal RequestResponseObligation Response { get; }

    internal DurableRequestBinding Binding { get; }

    internal RequestContractReference RequestContract => new(Reference(RequestDocument));

    internal ReplyContractReference ResultReplyContract => new(Reference(ResultReplyDocument));

    internal ReplyContractReference FailureReplyContract => new(Reference(FailureReplyDocument));

    internal ReplyContractReference TimeoutReplyContract =>
        new(Reference(Assert.IsType<ExecutionDefinitionDocument>(TimeoutReplyDocument)));

    internal ReplyContractReference CancellationReplyContract =>
        new(Reference(Assert.IsType<ExecutionDefinitionDocument>(CancellationReplyDocument)));

    internal static DurableOperationTestFixture Create(
        RequestRetrySemantics retry = RequestRetrySemantics.StableIdentity,
        RequestResolutionSemantics ambiguousOutcome = RequestResolutionSemantics.Reconcile,
        RequestResolutionSemantics unresolvedOutcome = RequestResolutionSemantics.Escalate,
        RequestResultDisposition lateResult = RequestResultDisposition.Observe,
        RequestResultDisposition staleResult = RequestResultDisposition.Reject,
        RequestResultDisposition duplicateResult = RequestResultDisposition.ReusePriorDisposition,
        int? maxAttempts = null,
        DurableOperationIdempotencyEvidence idempotencyEvidence =
            DurableOperationIdempotencyEvidence.TargetDeduplication,
        TimeSpan? timeoutAfter = null,
        bool supportsCancellation = false)
    {
        List<RequestTerminalOutcomeDefinition> outcomes =
        [
            new RequestResultDefinition(new("result"), StringSchema("result/v1")),
            new RequestFailureDefinition(new("failure"), StringSchema("failure/v1"))
        ];
        if (timeoutAfter is not null)
            outcomes.Add(new RequestTimeoutDefinition(new("timeout"), StringSchema("timeout/v1")));
        if (supportsCancellation)
        {
            outcomes.Add(
                new RequestCancellationDefinition(
                    new("cancellation"),
                    StringSchema("cancellation/v1")));
        }

        var response = new RequestResponseObligation(
            [.. outcomes],
            timeoutAfter is null
                ? RequestOptionalTerminalSemantics.Unsupported
                : RequestOptionalTerminalSemantics.TerminalOutcome,
            supportsCancellation
                ? RequestOptionalTerminalSemantics.TerminalOutcome
                : RequestOptionalTerminalSemantics.Unsupported,
            lateResult,
            staleResult,
            duplicateResult,
            retry,
            ambiguousOutcome,
            unresolvedOutcome,
            TimeSpan.FromDays(30));
        var requestDocument = InteractionContractDocuments.Create(
            new("interaction/request/durable-operation"),
            new("revision/1"),
            new RequestContractDefinition(StringSchema("request/v1"), response),
            Provenance());
        var requestReference = new RequestContractReference(Reference(requestDocument));
        var resultReplyDocument = InteractionContractDocuments.Create(
            new("interaction/reply/durable-operation-result"),
            new("revision/1"),
            new ReplyContractDefinition(requestReference, new("result")),
            Provenance());
        var failureReplyDocument = InteractionContractDocuments.Create(
            new("interaction/reply/durable-operation-failure"),
            new("revision/1"),
            new ReplyContractDefinition(requestReference, new("failure")),
            Provenance());
        var timeoutReplyDocument = timeoutAfter is null
            ? null
            : InteractionContractDocuments.Create(
                new("interaction/reply/durable-operation-timeout"),
                new("revision/1"),
                new ReplyContractDefinition(requestReference, new("timeout")),
                Provenance());
        var cancellationReplyDocument = !supportsCancellation
            ? null
            : InteractionContractDocuments.Create(
                new("interaction/reply/durable-operation-cancellation"),
                new("revision/1"),
                new ReplyContractDefinition(requestReference, new("cancellation")),
                Provenance());
        List<ExecutionDefinitionDocument> documents =
            [requestDocument, resultReplyDocument, failureReplyDocument];
        List<DurableReplyBinding> replies =
        [
            new(new("result"), new(Reference(resultReplyDocument))),
            new(new("failure"), new(Reference(failureReplyDocument)))
        ];
        if (timeoutReplyDocument is not null)
        {
            documents.Add(timeoutReplyDocument);
            replies.Add(new(new("timeout"), new(Reference(timeoutReplyDocument))));
        }
        if (cancellationReplyDocument is not null)
        {
            documents.Add(cancellationReplyDocument);
            replies.Add(new(new("cancellation"), new(Reference(cancellationReplyDocument))));
        }
        var catalogValidation = InteractionContractCatalog.TryCreate(
            [.. documents],
            out var catalog);
        Assert.True(catalogValidation.IsValid, FormatDiagnostics(catalogValidation));

        var needsReconciliation = retry == RequestRetrySemantics.ReconcileBeforeRetry
                                  || ambiguousOutcome == RequestResolutionSemantics.Reconcile
                                  || unresolvedOutcome == RequestResolutionSemantics.Reconcile;
        var needsEscalation = ambiguousOutcome == RequestResolutionSemantics.Escalate
                              || unresolvedOutcome == RequestResolutionSemantics.Escalate;
        var needsTerminalFailure = ambiguousOutcome == RequestResolutionSemantics.TerminalFailure
                                   || unresolvedOutcome == RequestResolutionSemantics.TerminalFailure;
        var binding = new DurableRequestBinding(
            requestReference,
            [.. replies],
            maxAttempts ?? (retry == RequestRetrySemantics.Never ? 1 : 3),
            TimeSpan.FromMinutes(5),
            timeoutAfter,
            idempotencyEvidence,
            terminalFailureOutcome: needsTerminalFailure ? new("failure") : null,
            reconciliationTarget: needsReconciliation
                ? ResolutionTarget("process/reconcile", "node/reconcile")
                : null,
            escalationTarget: needsEscalation
                ? ResolutionTarget("process/escalate", "node/escalate")
                : null);

        return new(
            Assert.IsType<InteractionContractCatalog>(catalog),
            requestDocument,
            resultReplyDocument,
            failureReplyDocument,
            timeoutReplyDocument,
            cancellationReplyDocument,
            response,
            binding);
    }

    internal DurableOperationState CreateState(
        string requestId = "emission/request/1",
        InteractionTarget? target = null,
        DurableRequestBinding? binding = null)
    {
        var validation = Executor.TryCreate(
            Request(requestId, target),
            binding ?? Binding,
            CreatedAtUtc,
            out var state);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        return Assert.IsType<DurableOperationState>(state);
    }

    internal RequestEnvelope Request(
        string requestId = "emission/request/1",
        InteractionTarget? target = null) =>
        new(
            InteractionEnvelope.CurrentSchemaVersion,
            Context(requestId),
            RequestContract,
            StringValue($"payload/{requestId}"),
            target ?? ProcessTarget());

    internal DurableOperationOutcomeObservation Success(string value = "accepted") =>
        new(new RequestResultOutcome(new("result"), StringValue(value)));

    internal DurableOperationFailureObservation Failure(
        DurableOperationFailurePhase phase,
        DurableOperationEffectEvidence effectEvidence,
        DurableOperationFailureDisposition disposition = DurableOperationFailureDisposition.Retryable,
        string code = "adapter.failure") =>
        new(new(phase, effectEvidence, disposition, code));

    internal RequestTimeoutOutcome Timeout(string value = "timed-out") =>
        new(new("timeout"), StringValue(value));

    internal RequestCancellationOutcome Cancellation(string value = "cancelled") =>
        new(new("cancellation"), StringValue(value));

    internal static ProcessTokenInteractionTarget ProcessTarget(
        string attempt = "process-attempt/1",
        string token = "token/review") =>
        new(
            new(new("process/onboarding-1"), new(attempt)),
            new(token));

    internal static TransitionInteractionTarget TransitionTarget(
        string fingerprintDigit = "c",
        string continuation = "continuation/apply-result",
        string entityId = "dq-case/1") =>
        new(
            DefinitionReference("transition/continue-review", fingerprintDigit[0]),
            new(continuation),
            new(new("DqCase"), new(entityId)));

    internal static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    internal static OperationContext ContextAt(DateTimeOffset utcNow) =>
        OperationContext.Create(timeProvider: new FixedTimeProvider(utcNow));

    internal static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(
            document.Metadata.DefinitionId,
            document.Metadata.RevisionId,
            document.Metadata.Fingerprint);

    internal static ExecutionDefinitionReference DefinitionReference(string id, char fingerprintDigit) =>
        new(
            new(id),
            new("revision/1"),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string(fingerprintDigit, 64)));

    internal static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static InteractionValueSchema StringSchema(string revision) =>
        new(StringContract, new(revision));

    static InteractionEnvelopeContext Context(string requestId) =>
        new(
            new(requestId),
            new ProcessInteractionOrigin(
                DefinitionReference("process/onboarding", 'b'),
                new("node/collect-evidence"),
                new(new("process/onboarding-1"), new("process-attempt/1")),
                new("activation/4"),
                new("token/review")),
            new("correlation/review-1"),
            causationId: null,
            new("authority/motion", "tenant/acme"),
            new($"idempotency/{requestId}"),
            new("entity", StringValue("dq-case/1")),
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
            Provenance());

    static DurableOperationResolutionTarget ResolutionTarget(string definition, string node) =>
        new(DefinitionReference(definition, 'd'), new(node));

    static ExecutionProvenance Provenance() =>
        new(
            new("durable-operation-tests", "1"),
            new("tests/execution-kernel/durable-operation"),
            DocumentOrigin.Generated);

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
