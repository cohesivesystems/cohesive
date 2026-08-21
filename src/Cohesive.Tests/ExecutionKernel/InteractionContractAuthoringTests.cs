using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class InteractionContractAuthoringTests
{
    [Fact]
    public void CreateDomainEvent_DerivesPortablePayloadAndRetainsExplicitAuthority()
    {
        var provenance = new ExecutionProvenance(
            new("ari.training.events", "1"),
            new("ari/ari-355/training-example-generated"),
            DocumentOrigin.User);

        var authored = InteractionContractAuthoring.CreateDomainEvent<TrainingExampleGenerated>(
            new("ari/event/training-example-generated"),
            new("1"),
            new("ari/training-example-generated/v1"),
            provenance,
            displayName: "Training example generated");

        Assert.True(authored.IsValid, FormatDiagnostics(authored.Validation));
        Assert.Equal(InteractionContractDocuments.Kind, authored.Document.Kind);
        Assert.Equal(new ExecutionDefinitionId("ari/event/training-example-generated"), authored.Document.Metadata.DefinitionId);
        Assert.Equal(new ExecutionRevisionId("1"), authored.Document.Metadata.RevisionId);
        Assert.Equal(provenance, authored.Document.Metadata.Provenance);
        Assert.Equal("Training example generated", authored.Document.Metadata.DisplayName);
        Assert.Equal(authored.Validation.Diagnostics, authored.Document.Metadata.Diagnostics);

        var definition = authored.Definition;
        Assert.Equal(new InteractionValueSchemaRevision("ari/training-example-generated/v1"), definition.Payload.Revision);
        Assert.Equal(
            new DefaultClrTypeRefMapper().Map(typeof(TrainingExampleGenerated), null),
            definition.Payload.Contract.Type);
        var payload = Assert.IsType<ObjectTypeRef>(definition.Payload.Contract.Type);
        Assert.Equal(
            ["DatasetName", "GeneratedAtUtc", "TrainingExampleId"],
            payload.Fields.Select(static field => field.Name));
        Assert.All(payload.Fields, static field =>
        {
            Assert.Equal(FieldPresence.Required, field.Presence);
            Assert.Equal(FieldNullability.NonNullable, field.Nullability);
        });

        Assert.Equal(authored.Document.Metadata.DefinitionId, authored.Reference.Definition.DefinitionId);
        Assert.Equal(authored.Document.Metadata.RevisionId, authored.Reference.Definition.RevisionId);
        Assert.Equal(authored.Document.Metadata.Fingerprint, authored.Reference.Definition.Fingerprint);
    }

    [Fact]
    public void CreateDomainEvent_ProducesDeterministicDocuments()
    {
        var first = Create<TrainingExampleGenerated>();
        var second = Create<TrainingExampleGenerated>();

        Assert.Equal(first.Document, second.Document);
        Assert.Equal(first.Reference, second.Reference);
    }

    [Fact]
    public void CreateDomainEvent_RetainsUnsupportedClrShapeDiagnostics()
    {
        var authored = Create<RecursiveEvent>();

        Assert.False(authored.IsValid);
        var diagnostic = Assert.Single(authored.Validation.Diagnostics);
        Assert.Equal(InteractionContractDiagnosticCodes.ValueSchemaInvalid, diagnostic.Code);
        Assert.Equal(diagnostic, Assert.Single(authored.Document.Metadata.Diagnostics));
        var payload = Assert.IsType<ObjectTypeRef>(authored.Definition.Payload.Contract.Type);
        Assert.IsType<OpaqueRuntimeTypeRef>(Assert.Single(payload.Fields).Type);
    }

    [Fact]
    public void CreateRequestProtocol_AuthorsTypedOutcomesAndExactReplyBindings()
    {
        var protocol = CreateRequestProtocol();

        Assert.True(protocol.IsValid, FormatDiagnostics(protocol.Validation));
        Assert.Equal(4, protocol.Documents.Length);
        Assert.Equal(3, protocol.Replies.Length);
        Assert.Equal(protocol.RequestDocument, protocol.Documents[0]);
        Assert.True(protocol.Declares(protocol.Outcomes.Accepted));
        Assert.True(protocol.Declares(protocol.Outcomes.Failed));
        Assert.IsType<RequestResultDefinition>(protocol.Outcomes.Accepted.Definition);
        Assert.IsType<RequestFailureDefinition>(protocol.Outcomes.Failed.Definition);
        Assert.IsType<RequestTimeoutDefinition>(protocol.Outcomes.TimedOut.Definition);
        Assert.Equal(
            new DefaultClrTypeRefMapper().Map(typeof(SubmissionAccepted), null),
            protocol.Outcomes.Accepted.Schema.Contract.Type);
        Assert.Equal(
            protocol.ReplyFor(protocol.Outcomes.Failed),
            protocol.Replies.Single(reply => reply.Outcome == protocol.Outcomes.Failed.Id).Reply);

        var binding = protocol.BindDurably(
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            terminalFailureOutcome: protocol.Outcomes.Failed,
            timeoutAfter: TimeSpan.FromMinutes(30));

        Assert.Equal(protocol.Request, binding.Request);
        Assert.Equal(protocol.Replies, binding.Replies);
        Assert.Equal(protocol.Outcomes.Failed.Id, binding.TerminalFailureOutcome);
    }

    [Fact]
    public void CreateRequestProtocol_ProducesTheCanonicalDocumentModelWithoutAParallelAuthority()
    {
        var protocol = CreateRequestProtocol();
        var expected = CreateRequestDocumentsDirectly();

        Assert.Equal(
            expected.Select(Reference),
            protocol.Documents.Select(Reference));
        Assert.Equal(
            expected.Select(static document => document.Metadata.Provenance),
            protocol.Documents.Select(static document => document.Metadata.Provenance));
        Assert.Equal(
            Assert.IsType<RequestContractDefinition>(
                expected[0].GetDefinition<InteractionContractDefinition>()),
            protocol.Definition);
        Assert.Equal(
            expected.Skip(1).Select(static document => new ReplyContractReference(Reference(document))),
            protocol.Replies.Select(static reply => reply.Reply));
    }

    [Fact]
    public void CreateRequestProtocol_ProjectsClosedClrCasesWithoutCanonicalDrift()
    {
        var representationNeutral = CreateRequestProtocol();
        var projected = CreateProjectedRequestProtocol();

        Assert.Equal(
            representationNeutral.Documents.Select(Reference),
            projected.Documents.Select(Reference));
        Assert.Equal(representationNeutral.Definition, projected.Definition);
        Assert.Equal(representationNeutral.Request, projected.Request);
        Assert.Equal(
            representationNeutral.Replies.Select(static reply => (reply.Outcome, reply.Reply)),
            projected.Replies.Select(static reply => (reply.Outcome, reply.Reply)));
        Assert.Equal(3, projected.Cases.Length);
        Assert.Same(projected.Outcomes.Accepted, projected.CaseFor<SubmissionAcceptedCase>());
        Assert.Same(projected.Outcomes.Failed, projected.CaseFor<SubmissionFailedCase>());
        Assert.Equal(typeof(SubmissionFailure), projected.Outcomes.TimedOut.PayloadType);
    }

    [Fact]
    public void CreateRequestProtocol_RequiresEveryProjectedCaseToBePubliclyExposedOnce()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            InteractionContractAuthoring.CreateRequestProtocol<SubmitTraining, SubmissionOutcome, IncompleteSubmissionCaseSet>(
                new("tests/request/training-submission"),
                new("revision/1"),
                new("tests/request/training-submission/v1"),
                outcomes =>
                {
                    var accepted = outcomes.Result<SubmissionAcceptedCase, SubmissionAccepted>(
                        new("accepted"),
                        new("tests/result/accepted/v1"));
                    _ = outcomes.Failure<SubmissionFailedCase, SubmissionFailure>(
                        new("failed"),
                        new("tests/result/failure/v1"));
                    return new(accepted);
                },
                ResponsePolicy(timeout: RequestOptionalTerminalSemantics.Unsupported),
                RequestProvenance()));

        Assert.Contains("expose every authored RequestProtocolCase", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRequestProtocol_IsDeterministic()
    {
        var first = CreateRequestProtocol();
        var second = CreateRequestProtocol();

        Assert.Equal(
            first.Documents.Select(Reference),
            second.Documents.Select(Reference));
        Assert.Equal(first.Request, second.Request);
        Assert.Equal(
            first.Replies.Select(static reply => (reply.Outcome, reply.Reply)),
            second.Replies.Select(static reply => (reply.Outcome, reply.Reply)));
    }

    [Fact]
    public void CreateRequestProtocol_RejectsDuplicateOutcomeIdentity()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            InteractionContractAuthoring.CreateRequestProtocol<SubmitTraining, SubmissionOutcomes>(
                new("tests/request/training-submission"),
                new("revision/1"),
                new("tests/request/training-submission/v1"),
                outcomes => new(
                    outcomes.Result<SubmissionAccepted>(new("duplicate"), new("tests/result/accepted/v1")),
                    outcomes.Failure<SubmissionFailure>(new("duplicate"), new("tests/result/failure/v1")),
                    outcomes.Timeout<SubmissionFailure>(new("timed-out"), new("tests/result/timeout/v1"))),
                ResponsePolicy(),
                RequestProvenance()));

        Assert.Contains("declared more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRequestProtocol_RejectsOutcomePolicyMismatch()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            InteractionContractAuthoring.CreateRequestProtocol<SubmitTraining, SubmissionOutcomes>(
                new("tests/request/training-submission"),
                new("revision/1"),
                new("tests/request/training-submission/v1"),
                outcomes => new(
                    outcomes.Result<SubmissionAccepted>(new("accepted"), new("tests/result/accepted/v1")),
                    outcomes.Failure<SubmissionFailure>(new("failed"), new("tests/result/failure/v1")),
                    outcomes.Timeout<SubmissionFailure>(new("timed-out"), new("tests/result/timeout/v1"))),
                ResponsePolicy(timeout: RequestOptionalTerminalSemantics.Unsupported),
                RequestProvenance()));

        Assert.Contains("timeout semantics require exactly 0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestProtocol_RejectsTypedOutcomesFromAnotherProtocol()
    {
        var first = CreateRequestProtocol();
        var second = CreateRequestProtocol();

        Assert.False(first.Declares(second.Outcomes.Failed));
        Assert.Throws<ArgumentException>(() => first.ReplyFor(second.Outcomes.Failed));
        Assert.Throws<ArgumentException>(() => first.BindDurably(
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            terminalFailureOutcome: second.Outcomes.Failed,
            timeoutAfter: TimeSpan.FromMinutes(30)));
        Assert.Throws<ArgumentException>(() => first.BindDurably(
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(5),
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            terminalFailureOutcome: first.Outcomes.Accepted,
            timeoutAfter: TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void CreateRequestProtocol_RetainsUnsupportedClrShapeDiagnostics()
    {
        var protocol = InteractionContractAuthoring.CreateRequestProtocol<RecursiveEvent, SubmissionOutcomes>(
            new("tests/request/recursive"),
            new("revision/1"),
            new("tests/request/recursive/v1"),
            outcomes => new(
                outcomes.Result<SubmissionAccepted>(new("accepted"), new("tests/result/accepted/v1")),
                outcomes.Failure<SubmissionFailure>(new("failed"), new("tests/result/failure/v1")),
                outcomes.Timeout<SubmissionFailure>(new("timed-out"), new("tests/result/timeout/v1"))),
            ResponsePolicy(),
            RequestProvenance());

        Assert.False(protocol.IsValid);
        Assert.Contains(
            protocol.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == InteractionContractCatalogDiagnosticCodes.DocumentInvalid);
        Assert.Contains(
            protocol.RequestDocument.Metadata.Diagnostics,
            static diagnostic => diagnostic.Code == InteractionContractDiagnosticCodes.ValueSchemaInvalid);
        Assert.False(protocol.TryGetCatalog(out _));
        Assert.Throws<InvalidOperationException>(() => protocol.Catalog);
    }

    static AuthoredDomainEventContract<TPayload> Create<TPayload>() =>
        InteractionContractAuthoring.CreateDomainEvent<TPayload>(
            new("tests/event/domain-event"),
            new("revision/1"),
            new("tests/event/payload/v1"),
            new(
                new("interaction-authoring-tests", "1"),
                new("tests/execution-kernel/interaction-authoring"),
                DocumentOrigin.Generated));

    static RequestProtocol<SubmitTraining, SubmissionOutcomes> CreateRequestProtocol() =>
        InteractionContractAuthoring.CreateRequestProtocol<SubmitTraining, SubmissionOutcomes>(
            new("tests/request/training-submission"),
            new("revision/1"),
            new("tests/request/training-submission/v1"),
            outcomes => new(
                Accepted: outcomes.Result<SubmissionAccepted>(
                    new("accepted"),
                    new("tests/result/accepted/v1")),
                Failed: outcomes.Failure<SubmissionFailure>(
                    new("failed"),
                    new("tests/result/failure/v1")),
                TimedOut: outcomes.Timeout<SubmissionFailure>(
                    new("timed-out"),
                    new("tests/result/timeout/v1"))),
            ResponsePolicy(),
            RequestProvenance(),
            replyProvenance: ReplyProvenance);

    static RequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCaseSet> CreateProjectedRequestProtocol() =>
        InteractionContractAuthoring.CreateRequestProtocol<SubmitTraining, SubmissionOutcome, SubmissionCaseSet>(
            new("tests/request/training-submission"),
            new("revision/1"),
            new("tests/request/training-submission/v1"),
            outcomes => new(
                Accepted: outcomes.Result<SubmissionAcceptedCase, SubmissionAccepted>(
                    new("accepted"),
                    new("tests/result/accepted/v1")),
                Failed: outcomes.Failure<SubmissionFailedCase, SubmissionFailure>(
                    new("failed"),
                    new("tests/result/failure/v1")),
                TimedOut: outcomes.Timeout<SubmissionTimedOutCase, SubmissionFailure>(
                    new("timed-out"),
                    new("tests/result/timeout/v1"))),
            ResponsePolicy(),
            RequestProvenance(),
            replyProvenance: ReplyProvenance);

    static ImmutableArray<ExecutionDefinitionDocument> CreateRequestDocumentsDirectly()
    {
        var mapper = new DefaultClrTypeRefMapper();
        var requestDefinition = new RequestContractDefinition(
            new(
                new(mapper.Map(typeof(SubmitTraining), null)),
                new("tests/request/training-submission/v1")),
            new(
                terminalOutcomes:
                [
                    new RequestResultDefinition(
                        new("accepted"),
                        new(
                            new(mapper.Map(typeof(SubmissionAccepted), null)),
                            new("tests/result/accepted/v1"))),
                    new RequestFailureDefinition(
                        new("failed"),
                        new(
                            new(mapper.Map(typeof(SubmissionFailure), null)),
                            new("tests/result/failure/v1"))),
                    new RequestTimeoutDefinition(
                        new("timed-out"),
                        new(
                            new(mapper.Map(typeof(SubmissionFailure), null)),
                            new("tests/result/timeout/v1")))
                ],
                timeout: RequestOptionalTerminalSemantics.TerminalOutcome,
                cancellation: RequestOptionalTerminalSemantics.Unsupported,
                lateResult: RequestResultDisposition.Observe,
                staleResult: RequestResultDisposition.Reject,
                duplicateResult: RequestResultDisposition.ReusePriorDisposition,
                retry: RequestRetrySemantics.StableIdentity,
                ambiguousOutcome: RequestResolutionSemantics.TerminalFailure,
                unresolvedOutcome: RequestResolutionSemantics.TerminalFailure,
                retentionHorizon: TimeSpan.FromDays(30)));
        var requestDocument = InteractionContractDocuments.Create(
            new("tests/request/training-submission"),
            new("revision/1"),
            requestDefinition,
            RequestProvenance());
        RequestContractReference request = new(Reference(requestDocument));
        return
        [
            requestDocument,
            ReplyDocument(request, "accepted"),
            ReplyDocument(request, "failed"),
            ReplyDocument(request, "timed-out")
        ];
    }

    static ExecutionDefinitionDocument ReplyDocument(RequestContractReference request, string outcome) =>
        InteractionContractDocuments.Create(
            new($"tests/request/training-submission/reply/{outcome}"),
            new("revision/1"),
            new ReplyContractDefinition(request, new(outcome)),
            ReplyProvenance(new(outcome)));

    static RequestProtocolResponsePolicy ResponsePolicy(
        RequestOptionalTerminalSemantics timeout = RequestOptionalTerminalSemantics.TerminalOutcome) => new(
        timeout,
        RequestOptionalTerminalSemantics.Unsupported,
        RequestResultDisposition.Observe,
        RequestResultDisposition.Reject,
        RequestResultDisposition.ReusePriorDisposition,
        RequestRetrySemantics.StableIdentity,
        RequestResolutionSemantics.TerminalFailure,
        RequestResolutionSemantics.TerminalFailure,
        TimeSpan.FromDays(30));

    static ExecutionProvenance RequestProvenance() => new(
        new("interaction-authoring-tests", "1"),
        new("tests/request/training-submission/request"),
        DocumentOrigin.Generated);

    static ExecutionProvenance ReplyProvenance(RequestTerminalOutcomeId outcome) => new(
        new("interaction-authoring-tests", "1"),
        new($"tests/request/training-submission/reply/{outcome.Value}"),
        DocumentOrigin.Generated);

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Severity} {diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));

    sealed record TrainingExampleGenerated(
        string TrainingExampleId,
        string DatasetName,
        DateTimeOffset GeneratedAtUtc);

    sealed record SubmitTraining(string DatasetId);

    sealed record SubmissionAccepted(string SubmissionId);

    sealed record SubmissionFailure(string Reason);

    sealed record SubmissionOutcomes(
        RequestProtocolOutcome<SubmissionAccepted> Accepted,
        RequestProtocolOutcome<SubmissionFailure> Failed,
        RequestProtocolOutcome<SubmissionFailure> TimedOut);

    abstract record SubmissionOutcome;

    sealed record SubmissionAcceptedCase(SubmissionAccepted Payload) : SubmissionOutcome;

    sealed record SubmissionFailedCase(SubmissionFailure Payload) : SubmissionOutcome;

    sealed record SubmissionTimedOutCase(SubmissionFailure Payload) : SubmissionOutcome;

    sealed record SubmissionCaseSet(
        RequestProtocolCase<SubmissionAcceptedCase, SubmissionAccepted> Accepted,
        RequestProtocolCase<SubmissionFailedCase, SubmissionFailure> Failed,
        RequestProtocolCase<SubmissionTimedOutCase, SubmissionFailure> TimedOut);

    sealed record IncompleteSubmissionCaseSet(
        RequestProtocolCase<SubmissionAcceptedCase, SubmissionAccepted> Accepted);

    sealed class RecursiveEvent
    {
        public RecursiveEvent? Parent { get; init; }
    }
}
