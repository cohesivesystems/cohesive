using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class TransitionEmissionEnvelopeLowererTests
{
    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract EmptyObjectContract = new(new ObjectTypeRef([]));
    static readonly InteractionEntityReference Entity = new(new("dq-case"), new("case/1"));
    static readonly InteractionDeliveryRequirements Delivery = new(
        InteractionDurabilityDemand.Durable,
        InteractionVisibilityDemand.AfterOriginCommit);

    [Fact]
    public void DirectAndProcessPolicies_LowerEquivalentSemanticsWithExactSourceProvenance()
    {
        var fixture = Fixture();
        var directValidation = TransitionEmissionEnvelopeLowerer.TryLower(
            fixture.Decision,
            fixture.Catalog,
            DirectPolicy(fixture.Decision),
            out var direct);
        var processValidation = TransitionEmissionEnvelopeLowerer.TryLower(
            fixture.Decision,
            fixture.Catalog,
            ProcessPolicy(fixture.Decision),
            out var process);

        Assert.True(directValidation.IsValid, Format(directValidation));
        Assert.True(processValidation.IsValid, Format(processValidation));
        Assert.Equal(2, direct.Length);
        Assert.Equal(direct.Length, process.Length);
        for (var index = 0; index < direct.Length; index++)
        {
            Assert.Equal(Contract(direct[index]), Contract(process[index]));
            Assert.Equal(Payload(direct[index]), Payload(process[index]));
            Assert.Equal(direct[index].Context.Provenance, process[index].Context.Provenance);
            var directOrigin = Assert.IsType<TransitionInteractionOrigin>(direct[index].Context.Origin);
            var processOrigin = Assert.IsType<ProcessInteractionOrigin>(process[index].Context.Origin);
            Assert.Equal(fixture.Decision.Evidence.Definition, directOrigin.Definition);
            Assert.Equal(fixture.Decision.Emissions[index].Node, directOrigin.Node);
            Assert.Equal(fixture.Decision.Evidence.Definition, processOrigin.Transition);
            Assert.Equal(fixture.Decision.Emissions[index].Node, processOrigin.TransitionNode);
            Assert.Equal(Entity, processOrigin.Entity);
            Assert.Equal(new ExecutionNodeId("outcome"), processOrigin.Outcome);
            Assert.NotEqual(
                InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(direct[index]),
                InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(process[index]));
        }

        var json = InteractionEnvelopeJsonSerializer.Serialize(process[0]);
        var roundTripValidation = InteractionEnvelopeJsonSerializer.TryDeserialize(
            json,
            fixture.Catalog,
            out var restored);
        var restoredOrigin = Assert.IsType<ProcessInteractionOrigin>(restored?.Context.Origin);

        Assert.True(roundTripValidation.IsValid, Format(roundTripValidation));
        Assert.Equal(fixture.Decision.Emissions[0].Node, restoredOrigin.TransitionNode);
        Assert.Equal(process[0], restored);
        Assert.Equal(
            InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(process[0]),
            InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(restored!));
    }

    [Fact]
    public void ExactReferenceFailures_AreStructuredAndFailClosed()
    {
        var eventDocument = EventDocument("interaction/event/reviewed", StringContract);
        var catalog = Catalog(eventDocument);
        (ExecutionDefinitionReference Reference, string Code, string Location)[] cases =
        [
            (new(
                    new("interaction/event/unknown"),
                    eventDocument.Metadata.RevisionId,
                    eventDocument.Metadata.Fingerprint),
                InteractionContractCatalogDiagnosticCodes.DefinitionUnknown,
                "/emissions/0/contract/definitionId"),
            (new(
                    eventDocument.Metadata.DefinitionId,
                    new("revision/unknown"),
                    eventDocument.Metadata.Fingerprint),
                InteractionContractCatalogDiagnosticCodes.RevisionUnknown,
                "/emissions/0/contract/revisionId"),
            (new(
                    eventDocument.Metadata.DefinitionId,
                    eventDocument.Metadata.RevisionId,
                    Fingerprint('f')),
                InteractionContractCatalogDiagnosticCodes.FingerprintMismatch,
                "/emissions/0/contract/fingerprint")
        ];

        foreach (var (reference, code, location) in cases)
        {
            var decision = Decision(reference);
            var validation = TransitionEmissionEnvelopeLowerer.TryLower(
                decision,
                catalog,
                DirectPolicy(decision),
                out var envelopes);

            Assert.Empty(envelopes);
            var diagnostic = Assert.Single(validation.Diagnostics);
            Assert.Equal(code, diagnostic.Code);
            Assert.Equal(location, diagnostic.Location);
        }
    }

    [Fact]
    public void UnsupportedFamilyAndPayloadMismatch_ReturnStableDiagnostics()
    {
        var signalDocument = InteractionDocument(
            "interaction/signal/reviewed",
            new SignalContractDefinition(Schema(StringContract, "signal/v1")));
        var mismatchedEvent = EventDocument("interaction/event/reviewed", BooleanContract);
        var signalDecision = Decision(Reference(signalDocument));
        var payloadDecision = Decision(Reference(mismatchedEvent));

        var signalValidation = TransitionEmissionEnvelopeLowerer.TryLower(
            signalDecision,
            Catalog(signalDocument),
            DirectPolicy(signalDecision),
            out var signalEnvelopes);
        var payloadValidation = TransitionEmissionEnvelopeLowerer.TryLower(
            payloadDecision,
            Catalog(mismatchedEvent),
            DirectPolicy(payloadDecision),
            out var payloadEnvelopes);

        Assert.Empty(signalEnvelopes);
        Assert.Equal(
            TransitionEmissionLoweringDiagnosticCodes.ContractKindUnsupported,
            Assert.Single(signalValidation.Diagnostics).Code);
        Assert.Empty(payloadEnvelopes);
        Assert.Contains(
            payloadValidation.Diagnostics,
            static diagnostic => diagnostic.Code == InteractionEnvelopeDiagnosticCodes.PayloadContractMismatch);
    }

    [Fact]
    public void RequestWithoutTarget_AndDuplicateIdentity_FailTheWholeSequence()
    {
        var fixture = Fixture();
        var missingTarget = new TransitionEmissionLoweringPolicy(
            (intent, index) => DirectContext(fixture.Decision, intent, $"direct/{index}"));
        var duplicateIdentity = new TransitionEmissionLoweringPolicy(
            (intent, _) => DirectContext(fixture.Decision, intent, "duplicate"),
            static (_, _) => new TransitionInteractionTarget(
                DefinitionReference("transition/target", '9'),
                new("continuation/request"),
                Entity));

        var targetValidation = TransitionEmissionEnvelopeLowerer.TryLower(
            fixture.Decision,
            fixture.Catalog,
            missingTarget,
            out var targetEnvelopes);
        var identityValidation = TransitionEmissionEnvelopeLowerer.TryLower(
            fixture.Decision,
            fixture.Catalog,
            duplicateIdentity,
            out var identityEnvelopes);

        Assert.Empty(targetEnvelopes);
        Assert.Equal(
            TransitionEmissionLoweringDiagnosticCodes.RequestTargetUnavailable,
            Assert.Single(targetValidation.Diagnostics).Code);
        Assert.Empty(identityEnvelopes);
        Assert.Equal(
            TransitionEmissionLoweringDiagnosticCodes.EmissionIdentityDuplicate,
            Assert.Single(identityValidation.Diagnostics).Code);
    }

    [Fact]
    public void OriginMustRetainExactTransitionEmissionAndOutcomeEvidence()
    {
        var fixture = Fixture();
        var policy = new TransitionEmissionLoweringPolicy(
            (intent, index) => Context(
                $"invalid-origin/{index}",
                new TransitionInteractionOrigin(
                    fixture.Decision.Evidence.Definition,
                    new("another-emission"),
                    Entity,
                    new("outcome"))),
            static (_, _) => new TransitionInteractionTarget(
                DefinitionReference("transition/target", '9'),
                new("continuation/request"),
                Entity));

        var validation = TransitionEmissionEnvelopeLowerer.TryLower(
            fixture.Decision,
            fixture.Catalog,
            policy,
            out var envelopes);

        Assert.Empty(envelopes);
        Assert.Equal(2, validation.Diagnostics.Length);
        Assert.All(
            validation.Diagnostics,
            static diagnostic => Assert.Equal(
                TransitionEmissionLoweringDiagnosticCodes.OriginIncompatible,
                diagnostic.Code));
    }

    [Fact]
    public void HostedTransitionOrigin_RequiresSubjectTransitionEmissionAndOutcomeTogether()
    {
        var process = DefinitionReference("process/onboarding", '8');
        var transition = DefinitionReference("transition/review", '7');
        var continuation = new ProcessContinuationIdentity(new("process/1"), new("attempt/1"));

        Assert.Throws<ArgumentException>(() => new ProcessInteractionOrigin(
            process,
            new("invoke/review"),
            continuation,
            new("activation/1"),
            new("token/1"),
            entity: Entity,
            transition: transition,
            outcome: new("outcome")));
        Assert.Throws<ArgumentException>(() => new ProcessInteractionOrigin(
            process,
            new("invoke/review"),
            continuation,
            new("activation/1"),
            new("token/1"),
            entity: Entity,
            transition: transition,
            outcome: null,
            transitionNode: new("emit/0")));
    }

    static LoweringFixture Fixture()
    {
        var eventDocument = EventDocument("interaction/event/reviewed", StringContract);
        var requestDocument = InteractionDocument(
            "interaction/request/notify",
            new RequestContractDefinition(
                Schema(StringContract, "request/v1"),
                ResponseObligation()));
        var decision = Decision(Reference(eventDocument), Reference(requestDocument));
        return new(decision, Catalog(eventDocument, requestDocument));
    }

    static TransitionDecision Decision(params ExecutionDefinitionReference[] contracts)
    {
        List<TransitionNode> steps = new(contracts.Length + 1);
        for (var index = 0; index < contracts.Length; index++)
        {
            steps.Add(new EmitTransitionNode(
                new($"emit/{index}"),
                contracts[index],
                Expr.Const($"payload/{index}")));
        }
        steps.Add(new OutcomeTransitionNode(
            new("outcome"),
            TransitionOutcomeDisposition.NoChange,
            Expr.Const("done")));
        var definition = new CanonicalTransitionDefinition(
            EmptyObjectContract,
            EmptyObjectContract,
            StringContract,
            [],
            new SequenceTransitionNode(new("body"), [.. steps]));
        var document = TransitionDefinitionDocuments.Create(
            new("transition/review"),
            new("revision/1"),
            definition,
            Provenance());
        var compilation = TransitionStaticCompiler.Compile(document);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        return TransitionReferenceInterpreter.DecideFullState(
            plan,
            new("activation/review"),
            PortableValue.Concrete(EmptyObjectContract, ObservationValue.EmptyObject),
            PortableValue.Concrete(EmptyObjectContract, ObservationValue.EmptyObject));
    }

    static TransitionEmissionLoweringPolicy DirectPolicy(TransitionDecision decision) =>
        new(
            (intent, index) => DirectContext(decision, intent, $"direct/{index}"),
            (_, _) => new TransitionInteractionTarget(
                decision.Evidence.Definition,
                new("continuation/request"),
                Entity));

    static InteractionEnvelopeContext DirectContext(
        TransitionDecision decision,
        TransitionEmissionIntent intent,
        string identity) => Context(
        identity,
        new TransitionInteractionOrigin(
            decision.Evidence.Definition,
            intent.Node,
            Entity,
            new("outcome")));

    static TransitionEmissionLoweringPolicy ProcessPolicy(TransitionDecision decision) =>
        new(
            (intent, index) => Context(
                $"process/{index}",
                new ProcessInteractionOrigin(
                    DefinitionReference("process/onboarding", '8'),
                    new("invoke/review"),
                    new(new("process/1"), new("attempt/1")),
                    new("process-activation/1"),
                    new("token/1"),
                    Entity,
                    decision.Evidence.Definition,
                    new("outcome"),
                    intent.Node)),
            (_, _) => new TransitionInteractionTarget(
                decision.Evidence.Definition,
                new("continuation/request"),
                Entity));

    static InteractionEnvelopeContext Context(string identity, InteractionOrigin origin) =>
        new(
            new($"emission/{identity}"),
            origin,
            new("correlation/review"),
            causationId: null,
            new("authority/motion", "tenant/acme"),
            new($"idempotency/{identity}"),
            ordering: null,
            Delivery,
            Provenance());

    static InteractionContractCatalog Catalog(params ExecutionDefinitionDocument[] documents)
    {
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        Assert.True(validation.IsValid, Format(validation));
        return Assert.IsType<InteractionContractCatalog>(catalog);
    }

    static ExecutionDefinitionDocument EventDocument(string id, ValueContract contract) =>
        InteractionDocument(id, new DomainEventContractDefinition(Schema(contract, "event/v1")));

    static ExecutionDefinitionDocument InteractionDocument(
        string id,
        InteractionContractDefinition definition) => InteractionContractDocuments.Create(
        new(id),
        new("revision/1"),
        definition,
        Provenance());

    static RequestResponseObligation ResponseObligation() => new(
        [
            new RequestResultDefinition(new("result"), Schema(StringContract, "result/v1")),
            new RequestFailureDefinition(new("failure"), Schema(StringContract, "failure/v1"))
        ],
        RequestOptionalTerminalSemantics.Unsupported,
        RequestOptionalTerminalSemantics.Unsupported,
        RequestResultDisposition.Observe,
        RequestResultDisposition.Reject,
        RequestResultDisposition.ReusePriorDisposition,
        RequestRetrySemantics.StableIdentity,
        RequestResolutionSemantics.TerminalFailure,
        RequestResolutionSemantics.TerminalFailure,
        TimeSpan.FromDays(30));

    static InteractionValueSchema Schema(ValueContract contract, string revision) => new(contract, new(revision));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionDefinitionReference DefinitionReference(string id, char fingerprintDigit) => new(
        new(id),
        new("revision/1"),
        Fingerprint(fingerprintDigit));

    static ExecutionDefinitionFingerprint Fingerprint(char digit) => new(
        ExecutionDefinitionFingerprinter.Algorithm,
        ExecutionDefinitionFingerprinter.Canonicalization,
        new string(digit, 64));

    static ExecutionProvenance Provenance() => new(
        new("transition-emission-lowering-tests", "1"),
        new("tests/execution-kernel/transition-emission-lowering"),
        DocumentOrigin.Generated);

    static InteractionContractReference Contract(InteractionEnvelope envelope) => envelope switch
    {
        DomainEventEnvelope domainEvent => domainEvent.Contract,
        RequestEnvelope request => request.Contract,
        _ => throw new InvalidOperationException("Unexpected lowered interaction family.")
    };

    static PortableValue Payload(InteractionEnvelope envelope) => envelope switch
    {
        DomainEventEnvelope domainEvent => domainEvent.Payload,
        RequestEnvelope request => request.Payload,
        _ => throw new InvalidOperationException("Unexpected lowered interaction family.")
    };

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed record LoweringFixture(TransitionDecision Decision, InteractionContractCatalog Catalog);
}
