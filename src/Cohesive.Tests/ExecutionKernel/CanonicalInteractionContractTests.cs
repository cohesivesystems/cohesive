using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class CanonicalInteractionContractTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void InteractionFamilies_AreClosedNominalAndKeepPersistenceOutsideTheEmissionUnion()
    {
        var contracts = DerivedTypes(typeof(InteractionContractDefinition));
        var references = DerivedTypes(typeof(InteractionContractReference));
        var envelopes = DerivedTypes(typeof(InteractionEnvelope));
        var terminalDefinitions = DerivedTypes(typeof(RequestTerminalOutcomeDefinition));
        var terminalOutcomes = DerivedTypes(typeof(RequestTerminalOutcome));
        var targets = DerivedTypes(typeof(InteractionTarget));

        Assert.Equal(
            [
                (typeof(DomainEventContractDefinition), InteractionWireNames.DomainEvent),
                (typeof(RequestContractDefinition), InteractionWireNames.Request),
                (typeof(SignalContractDefinition), InteractionWireNames.Signal),
                (typeof(ReplyContractDefinition), InteractionWireNames.Reply)
            ],
            contracts);
        Assert.Equal(
            [
                (typeof(DomainEventContractReference), InteractionWireNames.DomainEvent),
                (typeof(RequestContractReference), InteractionWireNames.Request),
                (typeof(SignalContractReference), InteractionWireNames.Signal),
                (typeof(ReplyContractReference), InteractionWireNames.Reply)
            ],
            references);
        Assert.Equal(
            [
                (typeof(DomainEventEnvelope), InteractionWireNames.DomainEvent),
                (typeof(RequestEnvelope), InteractionWireNames.Request),
                (typeof(SignalEnvelope), InteractionWireNames.Signal),
                (typeof(ReplyEnvelope), InteractionWireNames.Reply)
            ],
            envelopes);
        Assert.Equal(
            [
                (typeof(RequestResultDefinition), InteractionWireNames.ResultOutcome),
                (typeof(RequestFailureDefinition), InteractionWireNames.FailureOutcome),
                (typeof(RequestTimeoutDefinition), InteractionWireNames.TimeoutOutcome),
                (typeof(RequestCancellationDefinition), InteractionWireNames.CancellationOutcome)
            ],
            terminalDefinitions);
        Assert.Equal(
            [
                (typeof(RequestResultOutcome), InteractionWireNames.ResultOutcome),
                (typeof(RequestFailureOutcome), InteractionWireNames.FailureOutcome),
                (typeof(RequestTimeoutOutcome), InteractionWireNames.TimeoutOutcome),
                (typeof(RequestCancellationOutcome), InteractionWireNames.CancellationOutcome)
            ],
            terminalOutcomes);
        Assert.Equal(
            [
                (typeof(ProcessTokenInteractionTarget), InteractionWireNames.ProcessTokenTarget),
                (typeof(TransitionInteractionTarget), InteractionWireNames.TransitionTarget)
            ],
            targets);

        Assert.All(
            contracts
                .Concat(references)
                .Concat(envelopes)
                .Concat(terminalDefinitions)
                .Concat(terminalOutcomes)
                .Concat(targets),
            static variant => Assert.True(variant.Type.IsSealed));
        Assert.All(
            new[]
            {
                typeof(InteractionContractDefinition),
                typeof(InteractionContractReference),
                typeof(InteractionEnvelope),
                typeof(RequestTerminalOutcomeDefinition),
                typeof(RequestTerminalOutcome),
                typeof(InteractionOrigin),
                typeof(InteractionTarget)
            },
            static root =>
            {
                var closureWitness = root.GetMethod(
                    "EnsureDeclaredVariant",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Assert.NotNull(closureWitness);
                Assert.True(closureWitness.IsAssembly);
                Assert.True(closureWitness.IsAbstract);
            });
        Assert.DoesNotContain(
            contracts.Concat(envelopes),
            static variant => variant.Type.Name.Contains("Persistence", StringComparison.Ordinal));
        Assert.Null(typeof(DomainEventContractDefinition).GetProperty(nameof(RequestContractDefinition.Response)));
        Assert.NotNull(typeof(RequestContractDefinition).GetProperty(nameof(RequestContractDefinition.Response)));
    }

    [Fact]
    public void RequestResponseObligation_NormalizesVariantsAndRequiresCoherentExplicitPolicies()
    {
        var outcomes = Outcomes();
        var obligation = Obligation(outcomes: [.. outcomes.Reverse()]);
        var equivalent = Obligation(outcomes: outcomes);

        Assert.Equal(
            ["cancellation", "failure", "result", "timeout"],
            obligation.TerminalOutcomes.Select(static outcome => outcome.Id.Value));
        Assert.Equal(new RequestTerminalOutcomeId("result"), obligation.Find(new("result"))?.Id);
        Assert.Null(obligation.Find(new("unknown")));
        Assert.Equal(equivalent, obligation);
        Assert.Equal(equivalent.GetHashCode(), obligation.GetHashCode());

        Assert.Throws<ArgumentException>(() => Obligation(outcomes: []));
        Assert.Throws<ArgumentException>(() => Obligation(
            outcomes:
            [
                new RequestResultDefinition(new("same"), StringSchema("result/v1")),
                new RequestFailureDefinition(new("same"), StringSchema("failure/v1"))
            ],
            timeout: RequestOptionalTerminalSemantics.Unsupported,
            cancellation: RequestOptionalTerminalSemantics.Unsupported));
        Assert.Throws<ArgumentException>(() => Obligation(
            outcomes:
            [
                new RequestTimeoutDefinition(new("timeout"), StringSchema("timeout/v1")),
                new RequestCancellationDefinition(new("cancellation"), StringSchema("cancellation/v1"))
            ]));
        Assert.Throws<ArgumentException>(() => Obligation(timeout: RequestOptionalTerminalSemantics.Unsupported));
        Assert.Throws<ArgumentException>(() => Obligation(
            outcomes: [.. outcomes.Where(static outcome => outcome is not RequestTimeoutDefinition)]));
        Assert.Throws<ArgumentException>(() => Obligation(cancellation: RequestOptionalTerminalSemantics.Unsupported));
        Assert.Throws<ArgumentException>(() => Obligation(
            outcomes: [.. outcomes.Where(static outcome => outcome is not RequestCancellationDefinition)]));
        Assert.Throws<ArgumentException>(() => Obligation(
            outcomes: [new RequestResultDefinition(new("result"), StringSchema("result/v1"))],
            timeout: RequestOptionalTerminalSemantics.Unsupported,
            cancellation: RequestOptionalTerminalSemantics.Unsupported,
            ambiguousOutcome: RequestResolutionSemantics.TerminalFailure));
        Assert.Throws<ArgumentOutOfRangeException>(() => Obligation(retentionHorizon: TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => obligation.Find(default));

        Action[] unspecifiedPolicies =
        [
            () => Obligation(timeout: RequestOptionalTerminalSemantics.Unspecified),
            () => Obligation(cancellation: RequestOptionalTerminalSemantics.Unspecified),
            () => Obligation(lateResult: RequestResultDisposition.Unspecified),
            () => Obligation(staleResult: RequestResultDisposition.Unspecified),
            () => Obligation(duplicateResult: RequestResultDisposition.Unspecified),
            () => Obligation(retry: RequestRetrySemantics.Unspecified),
            () => Obligation(ambiguousOutcome: RequestResolutionSemantics.Unspecified),
            () => Obligation(unresolvedOutcome: RequestResolutionSemantics.Unspecified)
        ];
        Assert.All(
            unspecifiedPolicies,
            static construct => Assert.Throws<ArgumentOutOfRangeException>(construct));
    }

    [Fact]
    public void RequestPolicyAndValueSchemaRevisions_AreFingerprintBearingWhileVariantOrderIsNot()
    {
        var outcomes = Outcomes();
        var canonical = Document(
            "interaction/request/canonical",
            RequestDefinition(Obligation(outcomes: outcomes)));
        var reordered = Document(
            "interaction/request/reordered",
            RequestDefinition(Obligation(outcomes: [.. outcomes.Reverse()])));
        var changedPolicy = Document(
            "interaction/request/changed-policy",
            RequestDefinition(Obligation(
                outcomes: outcomes,
                lateResult: RequestResultDisposition.Reject)));
        var changedPayloadRevision = Document(
            "interaction/request/changed-schema",
            new RequestContractDefinition(
                StringSchema("request-payload/v2"),
                Obligation(outcomes: outcomes)));

        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(canonical),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(reordered));
        Assert.Equal(canonical.Metadata.Fingerprint, reordered.Metadata.Fingerprint);
        Assert.NotEqual(canonical.Metadata.Fingerprint, changedPolicy.Metadata.Fingerprint);
        Assert.NotEqual(canonical.Metadata.Fingerprint, changedPayloadRevision.Metadata.Fingerprint);
    }

    [Fact]
    public void EveryContractKind_RoundTripsThroughTheSharedDefinitionAuthority()
    {
        var requestDocument = Document(
            "interaction/request/collect-evidence",
            RequestDefinition());
        var requestReference = new RequestContractReference(Reference(requestDocument));
        (string Id, InteractionContractDefinition Definition)[] definitions =
        [
            (
                "interaction/event/evidence-collected",
                new DomainEventContractDefinition(StringSchema("event-payload/v1"))),
            (
                "interaction/request/collect-evidence",
                RequestDefinition()),
            (
                "interaction/signal/review-ready",
                new SignalContractDefinition(StringSchema("signal-payload/v1"))),
            (
                "interaction/reply/evidence-collected",
                new ReplyContractDefinition(requestReference, new("result")))
        ];

        foreach (var (id, definition) in definitions)
        {
            var document = Document(id, definition);
            var json = ExecutionDefinitionJsonSerializer.Serialize(document);

            var validation = InteractionContractDocuments.TryDeserialize(
                json,
                out var restoredDocument,
                out var restoredDefinition);

            Assert.True(validation.IsValid, FormatDiagnostics(validation));
            Assert.NotNull(restoredDocument);
            Assert.NotNull(restoredDefinition);
            Assert.Equal(definition.GetType(), restoredDefinition.GetType());
            Assert.Equal(document, restoredDocument);
            Assert.Equal(document.Metadata.DefinitionId, restoredDocument.Metadata.DefinitionId);
            Assert.Equal(document.Metadata.RevisionId, restoredDocument.Metadata.RevisionId);
            Assert.Equal(document.Metadata.Fingerprint, restoredDocument.Metadata.Fingerprint);
            Assert.Equal(document.Metadata.Provenance, restoredDocument.Metadata.Provenance);
            Assert.Equal(
                ExecutionDefinitionJsonSerializer.GetCanonicalBytes(document),
                ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument));
        }
    }

    [Theory]
    [InlineData(InteractionWireNames.DomainEvent)]
    [InlineData("persistenceEvent")]
    [InlineData("unknown")]
    public void RequestWire_CannotBeReclassifiedAsAnotherOrPersistenceEvent(string discriminator)
    {
        var request = Document("interaction/request/review", RequestDefinition());
        var rewritten = RewriteDefinition(
            request,
            definition => definition[InteractionWireNames.InteractionDiscriminator] = discriminator);

        var validation = InteractionContractDocuments.TryDeserialize(
            rewritten,
            out var restoredDocument,
            out var restoredDefinition);

        Assert.NotNull(restoredDocument);
        Assert.Null(restoredDefinition);
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(InteractionContractDocumentDiagnosticCodes.DefinitionProjectionInvalid, diagnostic.Code);
        Assert.Equal("/definition", diagnostic.Location);
    }

    [Fact]
    public void ContractValidator_RejectsOpaqueRuntimePayloadAndResultSchemasWithPrecisePaths()
    {
        var opaque = new InteractionValueSchema(
            new(new OpaqueRuntimeTypeRef("Example.Transport.Payload, Example.Transport")),
            new("opaque/v1"));
        var domainEvent = new DomainEventContractDefinition(opaque);
        var request = RequestDefinition(Obligation(
            outcomes:
            [
                new RequestResultDefinition(new("result"), opaque),
                new RequestFailureDefinition(new("failure"), StringSchema("failure/v1"))
            ],
            timeout: RequestOptionalTerminalSemantics.Unsupported,
            cancellation: RequestOptionalTerminalSemantics.Unsupported));

        var eventDiagnostic = Assert.Single(InteractionContractValidator.Validate(domainEvent).Diagnostics);
        var resultDiagnostic = Assert.Single(InteractionContractValidator.Validate(request).Diagnostics);

        Assert.Equal(InteractionContractDiagnosticCodes.ValueSchemaInvalid, eventDiagnostic.Code);
        Assert.Equal("/payload/contract/type", eventDiagnostic.Location);
        Assert.Equal(PortableExecutionDiagnosticCodes.OpaqueRuntimeType, eventDiagnostic.Evidence?.Observed);
        Assert.Equal(InteractionContractDiagnosticCodes.ValueSchemaInvalid, resultDiagnostic.Code);
        Assert.Equal("/response/terminalOutcomes/1/schema/contract/type", resultDiagnostic.Location);
        Assert.Equal(PortableExecutionDiagnosticCodes.OpaqueRuntimeType, resultDiagnostic.Evidence?.Observed);
    }

    [Fact]
    public void CanonicalTransitionEmission_ResolvesItsExactContractWithoutDuplicatingInteractionKind()
    {
        var contractDocument = Document(
            "interaction/event/case-reviewed",
            new DomainEventContractDefinition(StringSchema("case-reviewed/v1")));
        var catalogValidation = InteractionContractCatalog.TryCreate(
            [contractDocument],
            out var catalog);
        var emission = new EmitTransitionNode(
            new("emit/case-reviewed"),
            Reference(contractDocument),
            Expr.Const("reviewed"));

        Assert.True(catalogValidation.IsValid, FormatDiagnostics(catalogValidation));
        Assert.NotNull(catalog);
        Assert.True(catalog.TryResolve(emission.Contract, out var resolved));
        Assert.IsType<DomainEventContractDefinition>(resolved);
        Assert.DoesNotContain(
            typeof(EmitTransitionNode).GetProperties(),
            static property => property.Name.Contains("Kind", StringComparison.Ordinal));
    }

    static RequestContractDefinition RequestDefinition(RequestResponseObligation? response = null) =>
        new(StringSchema("request-payload/v1"), response ?? Obligation());

    static RequestResponseObligation Obligation(
        ImmutableArray<RequestTerminalOutcomeDefinition> outcomes = default,
        RequestOptionalTerminalSemantics timeout = RequestOptionalTerminalSemantics.TerminalOutcome,
        RequestOptionalTerminalSemantics cancellation = RequestOptionalTerminalSemantics.TerminalOutcome,
        RequestResultDisposition lateResult = RequestResultDisposition.Observe,
        RequestResultDisposition staleResult = RequestResultDisposition.Reject,
        RequestResultDisposition duplicateResult = RequestResultDisposition.ReusePriorDisposition,
        RequestRetrySemantics retry = RequestRetrySemantics.StableIdentity,
        RequestResolutionSemantics ambiguousOutcome = RequestResolutionSemantics.Reconcile,
        RequestResolutionSemantics unresolvedOutcome = RequestResolutionSemantics.Escalate,
        TimeSpan? retentionHorizon = null) =>
        new(
            outcomes.IsDefault ? Outcomes() : outcomes,
            timeout,
            cancellation,
            lateResult,
            staleResult,
            duplicateResult,
            retry,
            ambiguousOutcome,
            unresolvedOutcome,
            retentionHorizon ?? TimeSpan.FromDays(30));

    static ImmutableArray<RequestTerminalOutcomeDefinition> Outcomes() =>
    [
        new RequestResultDefinition(new("result"), StringSchema("result/v1")),
        new RequestFailureDefinition(new("failure"), StringSchema("failure/v1")),
        new RequestTimeoutDefinition(new("timeout"), StringSchema("timeout/v1")),
        new RequestCancellationDefinition(new("cancellation"), StringSchema("cancellation/v1"))
    ];

    static InteractionValueSchema StringSchema(string revision) => new(StringContract, new(revision));

    static ExecutionDefinitionDocument Document(
        string id,
        InteractionContractDefinition definition) =>
        InteractionContractDocuments.Create(
            new(id),
            new("revision/1"),
            definition,
            Provenance());

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(
            document.Metadata.DefinitionId,
            document.Metadata.RevisionId,
            document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance() =>
        new(
            new("interaction-tests", "1"),
            new("tests/execution-kernel/interactions"),
            DocumentOrigin.Generated);

    static (Type Type, string Discriminator)[] DerivedTypes(Type root) =>
        [.. root
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(static attribute => (
                attribute.DerivedType,
                Assert.IsType<string>(attribute.TypeDiscriminator)))];

    static string RewriteDefinition(
        ExecutionDefinitionDocument document,
        Action<JsonObject> rewrite)
    {
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        var root = JsonNode.Parse(ExecutionDefinitionJsonSerializer.Serialize(document))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse the interaction test document.");
        var definition = root["definition"]?.AsObject()
            ?? throw new InvalidOperationException("The interaction test document has no definition object.");
        rewrite(definition);

        using var parsedDefinition = JsonDocument.Parse(definition.ToJsonString(options));
        var fingerprint = ExecutionDefinitionFingerprinter.Compute(
            document.Metadata.SchemaVersion,
            document.Kind,
            parsedDefinition.RootElement,
            document.Extensions);
        var fingerprintNode = root["metadata"]?["fingerprint"]?.AsObject()
            ?? throw new InvalidOperationException("The interaction test document has no fingerprint object.");
        fingerprintNode["algorithm"] = fingerprint.Algorithm;
        fingerprintNode["canonicalization"] = fingerprint.Canonicalization;
        fingerprintNode["value"] = fingerprint.Value;
        return root.ToJsonString(options);
    }

    static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
