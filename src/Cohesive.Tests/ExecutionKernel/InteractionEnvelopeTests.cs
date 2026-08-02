using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class InteractionEnvelopeTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract Int64Contract = new(new ScalarTypeRef(ScalarTypeKind.Int64));

    [Fact]
    public void AllEnvelopeKinds_RoundTripTheStrictWireAndValidateAgainstExactContracts()
    {
        var fixture = ContractFixture.Create();
        var catalogValidation = InteractionContractCatalog.TryCreate(fixture.Documents, out var catalog);
        Assert.True(catalogValidation.IsValid, FormatDiagnostics(catalogValidation));
        Assert.NotNull(catalog);

        EmissionId requestId = new("emission/request/review-1");
        InteractionEnvelope[] envelopes =
        [
            new DomainEventEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context("emission/event/reviewed", TransitionOrigin(), causationId: requestId),
                new(Reference(fixture.DomainEvent)),
                StringValue("reviewed")),
            new RequestEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context(requestId.Value, ProcessOrigin()),
                new(Reference(fixture.Request)),
                StringValue("collect evidence"),
                ProcessTarget()),
            new SignalEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context("emission/signal/review-ready", ProcessOrigin(), causationId: requestId),
                new(Reference(fixture.Signal)),
                StringValue("ready"),
                TransitionTarget()),
            new ReplyEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context("emission/reply/review-1", ProcessOrigin(), causationId: requestId),
                new(Reference(fixture.Reply)),
                requestId,
                new RequestResultOutcome(new("result"), StringValue("accepted")))
        ];

        foreach (var envelope in envelopes)
        {
            var json = InteractionEnvelopeJsonSerializer.Serialize(envelope);
            var validation = InteractionEnvelopeJsonSerializer.TryDeserialize(
                json,
                catalog,
                out var restored);

            Assert.True(validation.IsValid, FormatDiagnostics(validation));
            Assert.NotNull(restored);
            Assert.Equal(envelope.GetType(), restored.GetType());
            Assert.Equal(envelope, restored);
            Assert.Equal(envelope.Context.EmissionId, restored.Context.EmissionId);
            Assert.Equal(envelope.Context.CorrelationId, restored.Context.CorrelationId);
            Assert.Equal(envelope.Context.CausationId, restored.Context.CausationId);
            Assert.Equal(envelope.Context.AuthorityScope, restored.Context.AuthorityScope);
            Assert.Equal(envelope.Context.IdempotencyKey, restored.Context.IdempotencyKey);
            Assert.Equal(envelope.Context.Ordering, restored.Context.Ordering);
            Assert.Equal(envelope.Context.Delivery, restored.Context.Delivery);
            Assert.Equal(envelope.Context.Provenance, restored.Context.Provenance);
            Assert.Equal(
                InteractionEnvelopeJsonSerializer.GetCanonicalBytes(envelope),
                InteractionEnvelopeJsonSerializer.GetCanonicalBytes(restored));
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(
                ExpectedDiscriminator(envelope),
                parsed.RootElement.GetProperty(InteractionWireNames.InteractionDiscriminator).GetString());
        }

        Assert.IsType<ProcessTokenInteractionTarget>(Assert.IsType<RequestEnvelope>(envelopes[1]).ResponseTarget);
        Assert.IsType<TransitionInteractionTarget>(Assert.IsType<SignalEnvelope>(envelopes[2]).Target);
        Assert.IsType<TransitionInteractionOrigin>(envelopes[0].Context.Origin);
        Assert.IsType<ProcessInteractionOrigin>(envelopes[1].Context.Origin);
    }

    [Fact]
    public void ProcessTarget_ExactWaitOccurrenceRoundTripsWithoutCollapsingToTokenScope()
    {
        var fixture = ContractFixture.Create();
        var catalog = Catalog(fixture);
        ProcessWaitRegistrationId waitRegistration = new("process-wait:v1:sha256:exact");
        var envelope = new RequestEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            Context("emission/request/exact-wait", ProcessOrigin()),
            new(Reference(fixture.Request)),
            StringValue("collect evidence"),
            new ProcessTokenInteractionTarget(
                Continuation(),
                new("token/review"),
                waitRegistration));

        var json = InteractionEnvelopeJsonSerializer.Serialize(envelope);
        var restored = Assert.IsType<RequestEnvelope>(
            InteractionEnvelopeJsonSerializer.Deserialize(json, catalog));
        var target = Assert.IsType<ProcessTokenInteractionTarget>(restored.ResponseTarget);

        Assert.Equal(waitRegistration, target.WaitRegistrationId);
        Assert.Equal(envelope, restored);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(
            waitRegistration.Value,
            parsed.RootElement
                .GetProperty("responseTarget")
                .GetProperty("waitRegistrationId")
                .GetString());
    }

    [Fact]
    public void ChildRequestTarget_RoundTripsExactIdentityWhileOrdinaryRequestsOmitTheExtension()
    {
        var fixture = ContractFixture.Create();
        var catalog = Catalog(fixture);
        var childTarget = new ProcessChildRequestTarget(
            DefinitionReference("process/index-worker", 'd'),
            new(new("process/index-worker/instance-1"), new("process-attempt/1")),
            ChildOutcomeMapping(),
            ownerToken: new("token/review"),
            occurrence: 0);
        var childRequest = new RequestEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            Context("emission/request/child", ProcessOrigin()),
            new(Reference(fixture.Request)),
            StringValue("partition/a"),
            ProcessTarget(),
            childTarget);
        var ordinaryRequest = new RequestEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            Context("emission/request/ordinary", ProcessOrigin()),
            new(Reference(fixture.Request)),
            StringValue("ordinary"),
            ProcessTarget());

        var childJson = InteractionEnvelopeJsonSerializer.Serialize(childRequest);
        var restored = Assert.IsType<RequestEnvelope>(
            InteractionEnvelopeJsonSerializer.Deserialize(childJson, catalog));
        var ordinaryJson = JsonNode.Parse(InteractionEnvelopeJsonSerializer.Serialize(ordinaryRequest))!.AsObject();

        Assert.Equal(new ExecutionIrSchemaVersion("cohesive-interaction-envelope/v3"), childRequest.SchemaVersion);
        Assert.Equal(childTarget, restored.ChildTarget);
        Assert.Equal(childRequest, restored);
        Assert.Equal(
            InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(childRequest),
            InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(restored));
        Assert.Contains("\"childTarget\"", childJson, StringComparison.Ordinal);
        Assert.False(ordinaryJson.ContainsKey("childTarget"));
    }

    [Fact]
    public void ChildRequestTarget_RequiresIntrinsicProcessAddressing()
    {
        var fixture = ContractFixture.Create();
        var childTarget = new ProcessChildRequestTarget(
            DefinitionReference("process/index-worker", 'e'),
            new(new("process/index-worker/instance-2"), new("process-attempt/1")),
            ChildOutcomeMapping(),
            ownerToken: new("token/review"),
            occurrence: 0);

        Assert.Throws<ArgumentException>(() => new RequestEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            Context("emission/request/transition-origin", TransitionOrigin()),
            new(Reference(fixture.Request)),
            StringValue("partition/a"),
            ProcessTarget(),
            childTarget));
        Assert.Throws<ArgumentException>(() => new RequestEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            Context("emission/request/transition-target", ProcessOrigin()),
            new(Reference(fixture.Request)),
            StringValue("partition/a"),
            TransitionTarget(),
            childTarget));
    }

    [Fact]
    public void StrictEnvelopeReader_RejectsPreChildTargetV1Schema()
    {
        var fixture = ContractFixture.Create();
        var catalog = Catalog(fixture);
        var legacy = new RequestEnvelope(
            new("cohesive-interaction-envelope/v1"),
            Context("emission/request/v1", ProcessOrigin()),
            new(Reference(fixture.Request)),
            StringValue("legacy"),
            ProcessTarget());

        var validation = InteractionEnvelopeJsonSerializer.TryDeserialize(
            InteractionEnvelopeJsonSerializer.Serialize(legacy),
            catalog,
            out var restored);

        Assert.Null(restored);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == InteractionEnvelopeDiagnosticCodes.SchemaVersionUnsupported);
    }

    [Theory]
    [InlineData("persistenceEvent")]
    [InlineData("unknown")]
    public void StrictEnvelopeReader_RejectsPersistenceAndUnknownInteractionDiscriminators(string discriminator)
    {
        var fixture = ContractFixture.Create();
        var catalog = Catalog(fixture);
        var envelope = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            Context("emission/event/reviewed", TransitionOrigin()),
            new(Reference(fixture.DomainEvent)),
            StringValue("reviewed"));
        var root = JsonNode.Parse(InteractionEnvelopeJsonSerializer.Serialize(envelope))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse the interaction-envelope test JSON.");
        root[InteractionWireNames.InteractionDiscriminator] = discriminator;

        var validation = InteractionEnvelopeJsonSerializer.TryDeserialize(
            root.ToJsonString(InteractionEnvelopeJsonSerializer.CreateOptions()),
            catalog,
            out var restored);

        Assert.Null(restored);
        Assert.Equal(
            InteractionEnvelopeJsonDiagnosticCodes.DeserializationInvalid,
            Assert.Single(validation.Diagnostics).Code);
    }

    [Fact]
    public void StrictEnvelopeReader_RejectsAnUnsupportedSchemaBeforeTypedInterpretation()
    {
        var fixture = ContractFixture.Create();
        var catalog = Catalog(fixture);
        var envelope = new DomainEventEnvelope(
            new("cohesive-interaction-envelope/v4"),
            Context("emission/event/future-schema", TransitionOrigin()),
            new(Reference(fixture.DomainEvent)),
            StringValue("reviewed"));
        var futureWire = JsonNode.Parse(InteractionEnvelopeJsonSerializer.Serialize(envelope))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse the future interaction-envelope test JSON.");
        futureWire["futureSemantics"] = new JsonObject { ["mode"] = "v4-only" };

        var validation = InteractionEnvelopeJsonSerializer.TryDeserialize(
            futureWire.ToJsonString(InteractionEnvelopeJsonSerializer.CreateOptions()),
            new([new("cohesive-interaction-envelope/v4")]),
            catalog,
            graph: null,
            out var restored);

        Assert.Null(restored);
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(InteractionEnvelopeDiagnosticCodes.SchemaVersionUnsupported, diagnostic.Code);
        Assert.Equal("/schemaVersion", diagnostic.Location);
    }

    [Fact]
    public void StrictEnvelopeReader_SeparatesLocalSchemaSupportFromInterpreterAdmission()
    {
        var fixture = ContractFixture.Create();
        var catalog = Catalog(fixture);
        var envelope = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            Context("emission/event/interpreter-schema", TransitionOrigin()),
            new(Reference(fixture.DomainEvent)),
            StringValue("reviewed"));

        var validation = InteractionEnvelopeJsonSerializer.TryDeserialize(
            InteractionEnvelopeJsonSerializer.Serialize(envelope),
            new([new("cohesive-interaction-envelope/other")]),
            catalog,
            graph: null,
            out var restored);

        Assert.Equal(envelope, restored);
        Assert.Equal(
            InteractionEnvelopeDiagnosticCodes.SchemaVersionInterpreterUnsupported,
            Assert.Single(validation.Diagnostics).Code);
    }

    [Fact]
    public void EnvelopeLinking_FailsClosedForKindFingerprintPayloadAndReplyOutcomeMismatches()
    {
        var fixture = ContractFixture.Create();
        var catalogValidation = InteractionContractCatalog.TryCreate(fixture.Documents, out var catalog);
        Assert.True(catalogValidation.IsValid, FormatDiagnostics(catalogValidation));
        Assert.NotNull(catalog);
        EmissionId requestId = new("emission/request/linked");

        InteractionEnvelope[] invalid =
        [
            new RequestEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context("emission/request/wrong-kind", ProcessOrigin()),
                new(Reference(fixture.DomainEvent)),
                StringValue("payload"),
                ProcessTarget()),
            new SignalEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context("emission/signal/wrong-fingerprint", ProcessOrigin()),
                new(DifferentFingerprint(Reference(fixture.Signal))),
                StringValue("payload"),
                ProcessTarget()),
            new DomainEventEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context("emission/event/wrong-payload", TransitionOrigin()),
                new(Reference(fixture.DomainEvent)),
                Int64Value(42)),
            new ReplyEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context("emission/reply/unknown-outcome", ProcessOrigin(), requestId),
                new(Reference(fixture.Reply)),
                requestId,
                new RequestResultOutcome(new("unknown"), StringValue("payload"))),
            new ReplyEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context("emission/reply/wrong-outcome-kind", ProcessOrigin(), requestId),
                new(Reference(fixture.Reply)),
                requestId,
                new RequestFailureOutcome(new("result"), StringValue("payload"))),
            new ReplyEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                Context("emission/reply/wrong-outcome-contract", ProcessOrigin(), requestId),
                new(Reference(fixture.Reply)),
                requestId,
                new RequestResultOutcome(new("result"), Int64Value(42))),
            new DomainEventEnvelope(
                new("cohesive-interaction-envelope/v4"),
                Context("emission/event/future-schema", TransitionOrigin()),
                new(Reference(fixture.DomainEvent)),
                StringValue("payload"))
        ];
        string[] expectedCodes =
        [
            InteractionContractCatalogDiagnosticCodes.ContractKindMismatch,
            InteractionContractCatalogDiagnosticCodes.FingerprintMismatch,
            InteractionEnvelopeDiagnosticCodes.PayloadContractMismatch,
            InteractionEnvelopeDiagnosticCodes.OutcomeUnknown,
            InteractionEnvelopeDiagnosticCodes.OutcomeKindMismatch,
            InteractionEnvelopeDiagnosticCodes.OutcomeContractMismatch,
            InteractionEnvelopeDiagnosticCodes.SchemaVersionUnsupported
        ];
        string[] expectedLocations =
        [
            "/contract",
            "/contract/definition/fingerprint",
            "/payload/contract",
            "/outcome/id",
            "/outcome",
            "/outcome/value/contract",
            "/schemaVersion"
        ];

        for (var index = 0; index < invalid.Length; index++)
        {
            var diagnostic = Assert.Single(InteractionEnvelopeValidator.Validate(invalid[index], catalog).Diagnostics);
            Assert.Equal(expectedCodes[index], diagnostic.Code);
            Assert.Equal(expectedLocations[index], diagnostic.Location);
        }
    }

    [Fact]
    public void ReplyContractCatalog_RejectsAnUnknownRequestOutcomeAndAFalseRequestLabel()
    {
        var fixture = ContractFixture.Create();
        var unknownOutcomeReply = InteractionContractDocuments.Create(
            new("interaction/reply/unknown-outcome"),
            new("revision/1"),
            new ReplyContractDefinition(
                new(Reference(fixture.Request)),
                new("not-declared")),
            Provenance());
        var falseRequestReply = InteractionContractDocuments.Create(
            new("interaction/reply/false-request"),
            new("revision/1"),
            new ReplyContractDefinition(
                new(Reference(fixture.DomainEvent)),
                new("result")),
            Provenance());

        var outcomeValidation = InteractionContractCatalog.TryCreate(
            [fixture.Request, unknownOutcomeReply],
            out var outcomeCatalog);
        var requestValidation = InteractionContractCatalog.TryCreate(
            [falseRequestReply, fixture.DomainEvent],
            out var requestCatalog);

        Assert.Null(outcomeCatalog);
        Assert.Null(requestCatalog);
        Assert.Equal(
            InteractionContractCatalogDiagnosticCodes.ReplyOutcomeUnknown,
            Assert.Single(outcomeValidation.Diagnostics).Code);
        Assert.Equal(
            InteractionContractCatalogDiagnosticCodes.ContractKindMismatch,
            Assert.Single(requestValidation.Diagnostics).Code);
        Assert.Equal(
            "/contracts/0/definition/request",
            Assert.Single(requestValidation.Diagnostics).Location);
    }

    [Fact]
    public void StrictEnvelopeReader_RejectsNonCanonicalOmissionOfOptionalMembers()
    {
        var fixture = ContractFixture.Create();
        var catalog = Catalog(fixture);
        var envelope = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new("emission/event/canonical"),
                TransitionOrigin(),
                new("correlation/canonical"),
                causationId: null,
                new("authority/motion"),
                new("idempotency/event/canonical"),
                ordering: null,
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
                Provenance()),
            new(Reference(fixture.DomainEvent)),
            StringValue("reviewed"));
        var root = JsonNode.Parse(InteractionEnvelopeJsonSerializer.Serialize(envelope))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse canonical interaction JSON.");
        var context = root["context"]?.AsObject()
            ?? throw new InvalidOperationException("Canonical interaction JSON has no context.");
        Assert.True(context.Remove("causationId"));

        var validation = InteractionEnvelopeJsonSerializer.TryDeserialize(
            root.ToJsonString(InteractionEnvelopeJsonSerializer.CreateOptions()),
            catalog,
            out var restored);

        Assert.Equal(envelope, restored);
        Assert.Equal(
            InteractionEnvelopeJsonDiagnosticCodes.WireNonCanonical,
            Assert.Single(validation.Diagnostics).Code);
    }

    [Fact]
    public void EnvelopeConstruction_RejectsUnmaterializedValuesAndDefaultCausation()
    {
        var fixture = ContractFixture.Create();
        var unknown = PortableValue.Unknown(StringContract);
        var failed = PortableValue.Failed(
            StringContract,
            new("tests.value.failed", DiagnosticSeverity.Error, "Value acquisition failed.", "/value"));

        Assert.Throws<ArgumentException>(() => new InteractionOrdering("entity", unknown));
        Assert.Throws<ArgumentException>(() => new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            Context("emission/event/unknown", TransitionOrigin()),
            new(Reference(fixture.DomainEvent)),
            unknown));
        Assert.Throws<ArgumentException>(() => new RequestResultOutcome(new("result"), failed));
        Assert.Throws<ArgumentException>(() => new InteractionEnvelopeContext(
            new("emission/event/default-cause"),
            TransitionOrigin(),
            new("correlation/default-cause"),
            new EmissionId(),
            new("authority/motion"),
            new("idempotency/default-cause"),
            ordering: null,
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
            Provenance()));
    }

    static InteractionEnvelopeContext Context(
        string emissionId,
        InteractionOrigin origin,
        EmissionId? causationId = null) =>
        new(
            new(emissionId),
            origin,
            new("correlation/review-1"),
            causationId,
            new("authority/motion", "tenant/acme"),
            new($"idempotency/{emissionId}"),
            new("entity", StringValue("dq-case/1")),
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
            Provenance());

    static TransitionInteractionOrigin TransitionOrigin() =>
        new(
            DefinitionReference("transition/review", 'a'),
            new("emit/reviewed"),
            Entity(),
            new("outcome/approved"));

    static ProcessInteractionOrigin ProcessOrigin() =>
        new(
            DefinitionReference("process/onboarding", 'b'),
            new("node/collect-evidence"),
            Continuation(),
            new("activation/4"),
            new("token/review"));

    static ProcessTokenInteractionTarget ProcessTarget() =>
        new(Continuation(), new("token/review"));

    static TransitionInteractionTarget TransitionTarget() =>
        new(
            DefinitionReference("transition/continue-review", 'c'),
            new("continuation/apply-result"),
            Entity());

    static ProcessContinuationIdentity Continuation() =>
        new(new("process/onboarding-1"), new("attempt/1"));

    static InteractionEntityReference Entity() =>
        new(new("DqCase"), new("dq-case/1"));

    static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static PortableValue Int64Value(long value) =>
        PortableValue.Concrete(Int64Contract, ObservationValue.FromInt64(value));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(
            document.Metadata.DefinitionId,
            document.Metadata.RevisionId,
            document.Metadata.Fingerprint);

    static ExecutionDefinitionReference DefinitionReference(string id, char fingerprintDigit) =>
        new(
            new(id),
            new("revision/1"),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string(fingerprintDigit, 64)));

    static ExecutionDefinitionReference DifferentFingerprint(ExecutionDefinitionReference reference) =>
        new(
            reference.DefinitionId,
            reference.RevisionId,
            new(
                reference.Fingerprint.Algorithm,
                reference.Fingerprint.Canonicalization,
                string.Equals(reference.Fingerprint.Value, new string('0', 64), StringComparison.Ordinal)
                    ? new string('1', 64)
                    : new string('0', 64)));

    static ExecutionProvenance Provenance() =>
        new(
            new("interaction-tests", "1"),
            new("tests/execution-kernel/interactions"),
            DocumentOrigin.Generated);

    static ProcessChildOutcomeMapping ChildOutcomeMapping() => new(
        new("result"),
        new("failure"),
        new("cancelled"),
        new("terminated"));

    static string ExpectedDiscriminator(InteractionEnvelope envelope) => envelope switch
    {
        DomainEventEnvelope => InteractionWireNames.DomainEvent,
        RequestEnvelope => InteractionWireNames.Request,
        SignalEnvelope => InteractionWireNames.Signal,
        ReplyEnvelope => InteractionWireNames.Reply,
        _ => throw new ArgumentOutOfRangeException(nameof(envelope))
    };

    static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static InteractionContractCatalog Catalog(ContractFixture fixture)
    {
        var validation = InteractionContractCatalog.TryCreate(fixture.Documents, out var catalog);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        return Assert.IsType<InteractionContractCatalog>(catalog);
    }

    sealed record ContractFixture(
        ExecutionDefinitionDocument DomainEvent,
        ExecutionDefinitionDocument Request,
        ExecutionDefinitionDocument Signal,
        ExecutionDefinitionDocument Reply)
    {
        public ExecutionDefinitionDocument[] Documents => [DomainEvent, Request, Signal, Reply];

        public static ContractFixture Create()
        {
            var domainEvent = InteractionContractDocuments.Create(
                new("interaction/event/reviewed"),
                new("revision/1"),
                new DomainEventContractDefinition(StringSchema("event/v1")),
                Provenance());
            var request = InteractionContractDocuments.Create(
                new("interaction/request/review"),
                new("revision/1"),
                new RequestContractDefinition(StringSchema("request/v1"), Response()),
                Provenance());
            var signal = InteractionContractDocuments.Create(
                new("interaction/signal/review-ready"),
                new("revision/1"),
                new SignalContractDefinition(StringSchema("signal/v1")),
                Provenance());
            var reply = InteractionContractDocuments.Create(
                new("interaction/reply/review"),
                new("revision/1"),
                new ReplyContractDefinition(new(Reference(request)), new("result")),
                Provenance());
            return new(domainEvent, request, signal, reply);
        }

        static InteractionValueSchema StringSchema(string revision) =>
            new(StringContract, new(revision));

        static RequestResponseObligation Response() =>
            new(
                [
                    new RequestResultDefinition(new("result"), StringSchema("result/v1")),
                    new RequestFailureDefinition(new("failure"), StringSchema("failure/v1")),
                    new RequestFailureDefinition(new("cancelled"), StringSchema("cancelled/v1")),
                    new RequestFailureDefinition(new("terminated"), StringSchema("terminated/v1"))
                ],
                RequestOptionalTerminalSemantics.Unsupported,
                RequestOptionalTerminalSemantics.Unsupported,
                RequestResultDisposition.Observe,
                RequestResultDisposition.Reject,
                RequestResultDisposition.ReusePriorDisposition,
                RequestRetrySemantics.StableIdentity,
                RequestResolutionSemantics.Reconcile,
                RequestResolutionSemantics.Escalate,
                TimeSpan.FromDays(30));
    }
}
