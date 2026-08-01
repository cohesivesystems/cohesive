using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ChannelCapabilityConformanceTests
{
    static readonly ChannelDirectionId Outbound = new("outbound");
    static readonly ChannelDirectionId Request = new("request");
    static readonly ChannelDirectionId Reply = new("reply");

    [Fact]
    public void Compiler_ProducesDeterministicNativePlanWithExactReferences()
    {
        var definition = RetainedLog();
        var document = Document("channel/retained-log", definition);
        var profile = NativeProfile("profile/retained-log", definition);
        var provenance = CompilerProvenance();

        var plan = ChannelRealizationCompiler.Compile(document, profile, provenance);
        var resolved = Resolve(plan, document, profile, provenance);

        Assert.True(resolved.IsRealizable, Format(plan.Validation));
        Assert.Equal(document.Metadata.DefinitionId, plan.Definition.DefinitionId);
        Assert.Equal(document.Metadata.RevisionId, plan.Definition.RevisionId);
        Assert.Equal(document.Metadata.Fingerprint, plan.Definition.Fingerprint);
        Assert.Equal(profile.ToReference(), plan.Profile);
        Assert.Equal(new ChannelCapabilityVariantId("default"), plan.Variant);
        Assert.Equal(definition.Requirements.Length, plan.Decisions.Length);
        Assert.All(plan.Decisions, static decision => Assert.Equal(CapabilityRealizationKind.Native, decision.Realization));
        Assert.Contains(
            plan.Configuration,
            static decision => decision.Setting == "channel.realization.variant"
                && decision.Origin == EffectiveConfigurationOrigin.AdapterConvention);

        var profileJson = ChannelRealizationJsonSerializer.SerializeProfile(profile);
        var restoredProfile = ChannelRealizationJsonSerializer.DeserializeProfile(profileJson);
        Assert.Equal(profile, restoredProfile);
        Assert.Equal(profile.Fingerprint, restoredProfile.Fingerprint);
        Assert.Equal(profileJson, ChannelRealizationJsonSerializer.SerializeProfile(restoredProfile));

        var planJson = ChannelRealizationJsonSerializer.SerializePlan(plan);
        var parsedPlan = ChannelRealizationJsonSerializer.ParsePlan(planJson);
        var restoredPlan = ChannelRealizationJsonSerializer.DeserializePlan(
            planJson,
            document,
            profile,
            provenance);
        Assert.Equal(plan, parsedPlan);
        Assert.Equal(plan, restoredPlan.Plan);
        Assert.True(restoredPlan.IsRealizable);
        Assert.Equal(plan.Fingerprint, restoredPlan.Plan.Fingerprint);
        Assert.Equal(planJson, ChannelRealizationJsonSerializer.SerializePlan(restoredPlan.Plan));

        var reversed = NativeProfile(
            "profile/retained-log",
            definition,
            reverseEvidence: true,
            description: "Another human-facing profile description.");
        Assert.Equal(profile.Fingerprint, reversed.Fingerprint);
    }

    [Fact]
    public void Compiler_RetainsComposedConstrainedAndOverrideEvidence()
    {
        var definition = TransientPublication(includePayloadLimit: true);
        var document = Document("channel/realization-kinds", definition);
        var evidence = definition.Requirements.Select(CreateNativeEvidence).ToDictionary(static item => item.Capability.Id);
        var routing = Assert.IsType<ChannelRoutingRequirement>(definition.Find(new("routing/outbound")));
        var framing = Assert.IsType<ChannelFramingRequirement>(definition.Find(new("framing/outbound")));
        var delivery = Assert.IsType<ChannelDeliveryRequirement>(definition.Find(new("delivery/outbound")));
        var routingOverride = new ChannelCapabilityEvidence(
            id: EvidenceId(routing),
            capability: routing,
            realization: CapabilityRealizationKind.Override,
            configuration:
            [
                new(
                    setting: "channel.routing.binding",
                    origin: EffectiveConfigurationOrigin.Explicit,
                    authority: "tests://explicit-routing")
            ],
            sourceReferences: ["tests://routing-override"]);
        var framingConstraint = new ChannelCapabilityEvidence(
            id: EvidenceId(framing),
            capability: framing,
            realization: CapabilityRealizationKind.Constrained,
            operatingBoundaries:
            [
                new(
                    id: new("boundary/payload-bytes"),
                    scope: framing.Scope,
                    kind: ChannelLimitKind.PayloadBytes,
                    value: 2_048)
            ],
            sourceReferences: ["tests://framing-constraint"]);
        var deliveryComposition = new ChannelCapabilityEvidence(
            id: EvidenceId(delivery),
            capability: delivery,
            realization: CapabilityRealizationKind.Composed,
            auxiliaries:
            [
                EvidenceId(definition.Find(new("persistence/outbound"))!),
                EvidenceId(definition.Find(new("reliability/outbound"))!)
            ],
            sourceReferences: ["tests://delivery-composition"]);
        evidence[routing.Id] = routingOverride;
        evidence[framing.Id] = framingConstraint;
        evidence[delivery.Id] = deliveryComposition;
        var profile = Profile(
            "profile/realization-kinds",
            new ChannelCapabilityVariant(new("default"), [.. evidence.Values]));
        var provenance = CompilerProvenance();

        var plan = ChannelRealizationCompiler.Compile(document, profile, provenance);
        var resolved = Resolve(plan, document, profile, provenance);

        Assert.True(resolved.IsRealizable, Format(plan.Validation));
        var routingDecision = Decision(plan, routing.Id);
        Assert.Equal(CapabilityRealizationKind.Override, routingDecision.Realization);
        Assert.Contains(
            plan.Configuration,
            static decision => decision.Setting == "channel.routing.binding"
                && decision.Origin == EffectiveConfigurationOrigin.Explicit);

        var framingDecision = Decision(plan, framing.Id);
        Assert.Equal(CapabilityRealizationKind.Constrained, framingDecision.Realization);
        var boundary = Assert.Single(framingDecision.OperatingBoundaries);
        Assert.Equal(ChannelLimitKind.PayloadBytes, boundary.Kind);
        Assert.Equal(2_048, boundary.Value);

        var deliveryDecision = Decision(plan, delivery.Id);
        Assert.Equal(CapabilityRealizationKind.Composed, deliveryDecision.Realization);
        Assert.Equal(2, deliveryDecision.Auxiliaries.Length);
        Assert.Contains("tests://delivery-composition", deliveryDecision.SourceReferences);
        Assert.Contains("tests://capability/persistence/outbound", deliveryDecision.SourceReferences);
        Assert.Contains("tests://capability/reliability/outbound", deliveryDecision.SourceReferences);

        var restoredProfile = ChannelRealizationJsonSerializer.DeserializeProfile(
            ChannelRealizationJsonSerializer.SerializeProfile(profile));
        var restoredPlan = ChannelRealizationJsonSerializer.DeserializePlan(
            ChannelRealizationJsonSerializer.SerializePlan(plan),
            document,
            profile,
            provenance);
        Assert.Equal(profile, restoredProfile);
        Assert.Equal(plan, restoredPlan.Plan);
        Assert.Equal(profile.Fingerprint, restoredProfile.Fingerprint);
        Assert.Equal(plan.Fingerprint, restoredPlan.Plan.Fingerprint);
    }

    [Fact]
    public void Compiler_DoesNotCombineEvidenceAcrossIncoherentVariants()
    {
        var definition = TransientPublication(includePayloadLimit: false);
        var document = Document("channel/split-profile", definition);
        var midpoint = definition.Requirements.Length / 2;
        var first = new ChannelCapabilityVariant(
            new("a"),
            [.. definition.Requirements.Take(midpoint).Select(CreateNativeEvidence)]);
        var second = new ChannelCapabilityVariant(
            new("b"),
            [.. definition.Requirements.Skip(midpoint).Select(CreateNativeEvidence)]);
        var profile = new ChannelCapabilityProfile(
            id: new("profile/split"),
            subject: "tests/split-target",
            variants: [second, first],
            provenance: ProfileProvenance());
        var provenance = CompilerProvenance();

        var plan = ChannelRealizationCompiler.Compile(document, profile, provenance);
        var resolved = Resolve(plan, document, profile, provenance);

        Assert.False(resolved.IsRealizable);
        Assert.Equal(new ChannelCapabilityVariantId("a"), plan.Variant);
        Assert.Equal(definition.Requirements.Length, plan.Decisions.Length);
        Assert.Contains(plan.Decisions, static decision => decision.Realization == CapabilityRealizationKind.Unavailable);
        Assert.All(
            plan.Validation.Diagnostics,
            static diagnostic => Assert.Equal(ChannelRealizationDiagnosticCodes.RequirementUnavailable, diagnostic.Code));
        Assert.All(
            plan.Decisions.Where(static decision => decision.Realization == CapabilityRealizationKind.Unavailable),
            static decision =>
            {
                Assert.Null(decision.Evidence);
                Assert.Empty(decision.Auxiliaries);
                Assert.Empty(decision.OperatingBoundaries);
                Assert.Empty(decision.SourceReferences);
            });
    }

    [Fact]
    public void Compiler_RejectsConstrainedEvidenceBelowDemandedCapacity()
    {
        var definition = TransientPublication(includePayloadLimit: true);
        var document = Document("channel/insufficient-boundary", definition);
        var framing = Assert.IsType<ChannelFramingRequirement>(definition.Find(new("framing/outbound")));
        var evidence = definition.Requirements
            .Select(requirement => requirement.Id == framing.Id
                ? new ChannelCapabilityEvidence(
                    id: EvidenceId(framing),
                    capability: framing,
                    realization: CapabilityRealizationKind.Constrained,
                    operatingBoundaries:
                    [
                        new(
                            id: new("boundary/payload-bytes"),
                            scope: framing.Scope,
                            kind: ChannelLimitKind.PayloadBytes,
                            value: 512)
                    ],
                    sourceReferences: ["tests://insufficient-boundary"])
                : CreateNativeEvidence(requirement))
            .ToImmutableArray();
        var profile = Profile(
            "profile/insufficient-boundary",
            new ChannelCapabilityVariant(new("default"), evidence));
        var provenance = CompilerProvenance();

        var plan = ChannelRealizationCompiler.Compile(document, profile, provenance);
        var resolved = Resolve(plan, document, profile, provenance);

        Assert.False(resolved.IsRealizable);
        Assert.Equal(CapabilityRealizationKind.Unavailable, Decision(plan, framing.Id).Realization);
        var diagnostic = Assert.Single(
            plan.Validation.Diagnostics,
            static item => item.Code == ChannelRealizationDiagnosticCodes.RequirementUnavailable);
        Assert.Contains("operating boundary", diagnostic.Evidence!.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiler_RejectsConstrainedEvidenceWithoutMatchingDeclaredWorkloadBoundary()
    {
        var definition = TransientPublication(includePayloadLimit: false);
        var document = Document("channel/undeclared-boundary", definition);
        var framing = Assert.IsType<ChannelFramingRequirement>(definition.Find(new("framing/outbound")));
        var evidence = definition.Requirements
            .Select(requirement => requirement.Id == framing.Id
                ? new ChannelCapabilityEvidence(
                    id: EvidenceId(framing),
                    capability: framing,
                    realization: CapabilityRealizationKind.Constrained,
                    operatingBoundaries:
                    [
                        new(
                            id: new("boundary/frame-bytes"),
                            scope: framing.Scope,
                            kind: ChannelLimitKind.FrameBytes,
                            value: 2_048)
                    ],
                    sourceReferences: ["tests://undeclared-boundary"])
                : CreateNativeEvidence(requirement))
            .ToImmutableArray();
        var profile = Profile(
            "profile/undeclared-boundary",
            new ChannelCapabilityVariant(new("default"), evidence));
        var provenance = CompilerProvenance();

        var plan = ChannelRealizationCompiler.Compile(document, profile, provenance);
        var resolved = Resolve(plan, document, profile, provenance);

        Assert.False(resolved.IsRealizable);
        Assert.Equal(CapabilityRealizationKind.Unavailable, Decision(plan, framing.Id).Realization);
        Assert.Contains(
            plan.Validation.Diagnostics,
            static item => item.Code == ChannelRealizationDiagnosticCodes.RequirementUnavailable
                && item.Evidence!.Observed!.Contains("operating boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_RejectsUnsupportedProfileSchemaAndRetainsItsExactReference()
    {
        var definition = TransientPublication(includePayloadLimit: false);
        var document = Document("channel/future-profile", definition);
        var current = NativeProfile("profile/future", definition);
        var future = new ChannelCapabilityProfile(
            schemaVersion: "cohesive-channel-capability-profile/v2",
            id: current.Id,
            subject: current.Subject,
            variants: current.Variants,
            provenance: current.Provenance,
            fingerprint: null,
            description: current.Description);
        var provenance = CompilerProvenance();

        var plan = ChannelRealizationCompiler.Compile(document, future, provenance);
        var resolution = ChannelRealizationPlanValidator.TryResolve(
            plan,
            document,
            future,
            provenance,
            out var resolved);

        Assert.False(resolution.IsValid);
        Assert.Null(resolved);
        Assert.Equal(future.ToReference(), plan.Profile);
        Assert.All(plan.Decisions, static decision => Assert.Equal(CapabilityRealizationKind.Native, decision.Realization));
        Assert.Contains(
            plan.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelRealizationDiagnosticCodes.ProfileSchemaUnsupported);
        Assert.Contains(
            resolution.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelRealizationDiagnosticCodes.ProfileSchemaUnsupported);
    }

    [Fact]
    public void Validator_RejectsRefingerprintedDecisionConfigurationDiagnosticAndProvenanceTampering()
    {
        var definition = TransientPublication(includePayloadLimit: false);
        var document = Document("channel/authoritative-validation", definition);
        var profile = NativeProfile("profile/authoritative-validation", definition);
        var provenance = CompilerProvenance();
        var plan = ChannelRealizationCompiler.Compile(document, profile, provenance);
        Assert.True(Resolve(plan, document, profile, provenance).IsRealizable);

        var first = plan.Decisions[0];
        var wrongEvidence = plan.Decisions[1].Evidence!.Value;
        var forgedDecision = new ChannelRealizationDecision(
            requirement: first.Requirement,
            realization: CapabilityRealizationKind.Native,
            evidence: wrongEvidence,
            sourceReferences: first.SourceReferences);
        var decisionTampered = Rebuild(
            plan,
            decisions: [forgedDecision, .. plan.Decisions.Skip(1)]);
        Assert.True(decisionTampered.ClaimsRealizable);
        AssertInvalid(
            decisionTampered,
            document,
            profile,
            provenance,
            ChannelRealizationDiagnosticCodes.DecisionEvidenceMismatch);

        var decisionMissing = Rebuild(plan, decisions: [.. plan.Decisions.Skip(1)]);
        Assert.True(decisionMissing.ClaimsRealizable);
        AssertInvalid(
            decisionMissing,
            document,
            profile,
            provenance,
            ChannelRealizationDiagnosticCodes.DecisionCoverageMismatch);

        var configurationTampered = Rebuild(
            plan,
            configuration:
            [
                new(
                    setting: "channel.realization.variant",
                    origin: EffectiveConfigurationOrigin.Explicit,
                    authority: "tests://forged-configuration")
            ]);
        AssertInvalid(
            configurationTampered,
            document,
            profile,
            provenance,
            ChannelRealizationDiagnosticCodes.ConfigurationMismatch);

        var diagnosticsTampered = Rebuild(
            plan,
            validation: new(
            [
                new(
                    "tests.channel.forged-warning",
                    DiagnosticSeverity.Warning,
                    "A forged non-error diagnostic.",
                    "/validation")
            ]));
        Assert.True(diagnosticsTampered.ClaimsRealizable);
        AssertInvalid(
            diagnosticsTampered,
            document,
            profile,
            provenance,
            ChannelRealizationDiagnosticCodes.ValidationMismatch);

        var provenanceTampered = Rebuild(plan, provenance: ForgedCompilerProvenance());
        AssertInvalid(
            provenanceTampered,
            document,
            profile,
            provenance,
            ChannelRealizationDiagnosticCodes.ProvenanceMismatch);
    }

    [Fact]
    public void Validator_RejectsWrongExactContextAndNonDeterministicVariantSelection()
    {
        var definition = TransientPublication(includePayloadLimit: false);
        var document = Document("channel/context-a", definition);
        var evidence = definition.Requirements.Select(CreateNativeEvidence).ToImmutableArray();
        var profile = new ChannelCapabilityProfile(
            id: new("profile/context-a"),
            subject: "tests/channel-target",
            variants:
            [
                new(new("a"), evidence),
                new(new("b"), evidence)
            ],
            provenance: ProfileProvenance());
        var provenance = CompilerProvenance();
        var plan = ChannelRealizationCompiler.Compile(document, profile, provenance);
        Assert.Equal(new ChannelCapabilityVariantId("a"), plan.Variant);

        var variantTampered = Rebuild(plan, variant: new("b"));
        AssertInvalid(
            variantTampered,
            document,
            profile,
            provenance,
            ChannelRealizationDiagnosticCodes.VariantSelectionMismatch);

        var otherDocument = Document("channel/context-b", definition);
        AssertInvalid(
            plan,
            otherDocument,
            profile,
            provenance,
            ChannelRealizationDiagnosticCodes.DefinitionReferenceMismatch);

        var otherProfile = new ChannelCapabilityProfile(
            id: new("profile/context-b"),
            subject: profile.Subject,
            variants: profile.Variants,
            provenance: profile.Provenance);
        AssertInvalid(
            plan,
            document,
            otherProfile,
            provenance,
            ChannelRealizationDiagnosticCodes.ProfileReferenceMismatch);

        var json = ChannelRealizationJsonSerializer.SerializePlan(plan);
        Assert.Throws<JsonException>(() => ChannelRealizationJsonSerializer.DeserializePlan(
            json,
            otherDocument,
            profile,
            provenance));
        Assert.Throws<JsonException>(() => ChannelRealizationJsonSerializer.DeserializePlan(
            json,
            document,
            profile,
            ForgedCompilerProvenance()));
    }

    [Fact]
    public void PlanParsing_RejectsFingerprintTamperingEmptyDecisionsAndFutureSchemas()
    {
        var definition = TransientPublication(includePayloadLimit: false);
        var document = Document("channel/parse-negative", definition);
        var profile = NativeProfile("profile/parse-negative", definition);
        var plan = ChannelRealizationCompiler.Compile(document, profile, CompilerProvenance());
        var json = ChannelRealizationJsonSerializer.SerializePlan(plan);
        var forgedFingerprint = new string('0', plan.Fingerprint.Value.Length);
        var fingerprintTampered = json.Replace(
            plan.Fingerprint.Value,
            forgedFingerprint,
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => ChannelRealizationJsonSerializer.ParsePlan(fingerprintTampered));
        Assert.Throws<ArgumentException>(() => Rebuild(plan, decisions: []));
        Assert.Throws<ArgumentException>(() => Rebuild(
            plan,
            schemaVersion: "cohesive-channel-realization-plan/v2"));
    }

    [Fact]
    public void RequirementCompatibility_UsesSupersetMinimumAndMaximumLaws()
    {
        var exchange = ChannelRequirementScope.Exchange;
        var direction = ChannelRequirementScope.ForDirection(Outbound);
        var requiredSecurity = new ChannelSecurityRequirement(
            id: new("security/required"),
            scope: exchange,
            properties: [ChannelSecurityKind.Confidentiality, ChannelSecurityKind.Integrity]);
        var strongerSecurity = new ChannelSecurityRequirement(
            id: new("security/available"),
            scope: exchange,
            properties:
            [
                ChannelSecurityKind.Confidentiality,
                ChannelSecurityKind.Integrity,
                ChannelSecurityKind.PeerAuthentication
            ]);
        Assert.True(ChannelRequirementCompatibility.Satisfies(requiredSecurity, strongerSecurity));
        Assert.False(ChannelRequirementCompatibility.Satisfies(strongerSecurity, requiredSecurity));

        var requiredSettlement = new ChannelSettlementRequirement(
            id: new("settlement/required"),
            scope: direction,
            coupling: ChannelSettlementCouplingKind.PerDelivery,
            operation: ChannelSettlementKind.Individual);
        var availableSettlement = new ChannelSettlementRequirement(
            id: new("settlement/available"),
            scope: direction,
            coupling: ChannelSettlementCouplingKind.PerDelivery,
            operation: ChannelSettlementKind.Individual);
        var negativeSettlement = new ChannelSettlementRequirement(
            id: new("settlement/negative"),
            scope: direction,
            coupling: ChannelSettlementCouplingKind.PerDelivery,
            operation: ChannelSettlementKind.Negative);
        Assert.True(ChannelRequirementCompatibility.Satisfies(requiredSettlement, availableSettlement));
        Assert.False(ChannelRequirementCompatibility.Satisfies(requiredSettlement, negativeSettlement));
        Assert.False(ChannelRequirementCompatibility.Satisfies(negativeSettlement, availableSettlement));

        var requiredPayload = new ChannelLimitRequirement(
            id: new("limit/required"),
            scope: direction,
            kind: ChannelLimitKind.PayloadBytes,
            value: 1_024);
        var largerPayload = new ChannelLimitRequirement(
            id: new("limit/available"),
            scope: direction,
            kind: ChannelLimitKind.PayloadBytes,
            value: 4_096);
        Assert.True(ChannelRequirementCompatibility.Satisfies(requiredPayload, largerPayload));
        Assert.False(ChannelRequirementCompatibility.Satisfies(largerPayload, requiredPayload));

        var requiredFlow = new ChannelFlowRequirement(
            id: new("flow/required"),
            scope: exchange,
            control: ChannelFlowControlKind.Demand,
            completion: ChannelStreamCompletionKind.HalfClose,
            continuity: ChannelSessionContinuityKind.BoundedResume,
            maximumInFlight: 100,
            resumeWindow: TimeSpan.FromMinutes(5));
        var tighterFlow = new ChannelFlowRequirement(
            id: new("flow/available"),
            scope: exchange,
            control: ChannelFlowControlKind.Demand,
            completion: ChannelStreamCompletionKind.HalfClose,
            continuity: ChannelSessionContinuityKind.BoundedResume,
            maximumInFlight: 32,
            resumeWindow: TimeSpan.FromMinutes(10));
        Assert.True(ChannelRequirementCompatibility.Satisfies(requiredFlow, tighterFlow));
        Assert.False(ChannelRequirementCompatibility.Satisfies(tighterFlow, requiredFlow));

        var requiredPartial = new ChannelReliabilityRequirement(
            id: new("reliability/required"),
            scope: direction,
            reliability: ChannelReliabilityKind.PartiallyReliable,
            maximumLifetime: TimeSpan.FromSeconds(10),
            maximumRetransmissions: 5);
        var tighterPartial = new ChannelReliabilityRequirement(
            id: new("reliability/available"),
            scope: direction,
            reliability: ChannelReliabilityKind.PartiallyReliable,
            maximumLifetime: TimeSpan.FromSeconds(5),
            maximumRetransmissions: 2);
        Assert.True(ChannelRequirementCompatibility.Satisfies(requiredPartial, tighterPartial));
        Assert.False(ChannelRequirementCompatibility.Satisfies(tighterPartial, requiredPartial));
    }

    [Theory]
    [MemberData(nameof(ProviderNeutralArchetypes))]
    public void ProviderNeutralArchetypes_AreRepresentableByOneCapabilityContract(
        string archetype,
        ChannelDefinition definition)
    {
        var validation = ChannelDefinitionValidator.Validate(definition);
        Assert.True(validation.IsValid, $"{archetype}: {Format(validation)}");
        var document = Document($"channel/{archetype}", definition);
        var profile = NativeProfile($"profile/{archetype}", definition);
        var provenance = CompilerProvenance();

        var plan = ChannelRealizationCompiler.Compile(document, profile, provenance);
        var resolved = Resolve(plan, document, profile, provenance);

        Assert.True(resolved.IsRealizable, $"{archetype}: {Format(plan.Validation)}");
        Assert.Equal(definition.Requirements.Length, plan.Decisions.Length);
    }

    public static IEnumerable<object[]> ProviderNeutralArchetypes()
    {
        yield return ["retained-log", RetainedLog()];
        yield return ["hybrid-subscription", HybridSubscription()];
        yield return ["leased-queue", LeasedQueue()];
        yield return ["transient-publication", TransientPublication(includePayloadLimit: false)];
        yield return ["unary-invocation", RequestReply(ChannelInteractionShape.UnaryInvocation, includeFlow: false)];
        yield return ["response-stream", RequestReply(ChannelInteractionShape.ResponseStream, includeFlow: true)];
        yield return ["partial-datagram", PartialDatagram()];
    }

    static ChannelDefinition RetainedLog() => OneWay(
        interaction: ChannelInteractionShape.Publication,
        distribution: ChannelDistributionKind.CompetingConsumers,
        routing: ChannelRoutingKind.KeyOrSessionAffinity,
        isolation: ChannelRoutingIsolationKind.SelectiveAcquisition,
        framing: ChannelFramingKind.TypedMessage,
        retention: ChannelRetentionKind.RetainedHistory,
        replay: ChannelReplayKind.OrderedPosition,
        guarantee: ChannelDeliveryGuaranteeKind.AtLeastOnce,
        ordering: ChannelOrderingScopeKind.PartitionKeyOrSession,
        reliability: new(
            id: new("reliability/outbound"),
            scope: ChannelRequirementScope.ForDirection(Outbound),
            reliability: ChannelReliabilityKind.Reliable),
        progress: new(
            id: new("progress/outbound"),
            scope: ChannelRequirementScope.ForDirection(Outbound),
            floor: ChannelProgressFloorKind.CumulativePrefix,
            pending: ChannelPendingProgressKind.None),
        minimumRetention: TimeSpan.FromHours(1));

    static ChannelDefinition HybridSubscription() => OneWay(
        interaction: ChannelInteractionShape.Publication,
        distribution: ChannelDistributionKind.FanOut,
        routing: ChannelRoutingKind.TopicOrFilter,
        isolation: ChannelRoutingIsolationKind.SelectiveAcquisition,
        framing: ChannelFramingKind.TypedMessage,
        retention: ChannelRetentionKind.RetainedHistory,
        replay: ChannelReplayKind.OrderedPosition,
        guarantee: ChannelDeliveryGuaranteeKind.AtLeastOnce,
        ordering: ChannelOrderingScopeKind.PartitionKeyOrSession,
        reliability: Reliable(),
        progress: new(
            id: new("progress/outbound"),
            scope: ChannelRequirementScope.ForDirection(Outbound),
            floor: ChannelProgressFloorKind.CumulativePrefix,
            pending: ChannelPendingProgressKind.PrefixWithUnresolvedGaps),
        settlements:
        [
            new(
                id: new("settlement/individual"),
                scope: ChannelRequirementScope.ForDirection(Outbound),
                coupling: ChannelSettlementCouplingKind.PerDelivery,
                operation: ChannelSettlementKind.Individual),
            new(
                id: new("settlement/negative"),
                scope: ChannelRequirementScope.ForDirection(Outbound),
                coupling: ChannelSettlementCouplingKind.PerDelivery,
                operation: ChannelSettlementKind.Negative)
        ],
        minimumRetention: TimeSpan.FromHours(1));

    static ChannelDefinition LeasedQueue() => OneWay(
        interaction: ChannelInteractionShape.FireAndForget,
        distribution: ChannelDistributionKind.CompetingConsumers,
        routing: ChannelRoutingKind.OperationEndpoint,
        isolation: ChannelRoutingIsolationKind.SelectiveAcquisition,
        framing: ChannelFramingKind.TypedMessage,
        retention: ChannelRetentionKind.DurableUntilSettled,
        replay: ChannelReplayKind.None,
        guarantee: ChannelDeliveryGuaranteeKind.AtLeastOnce,
        ordering: ChannelOrderingScopeKind.None,
        reliability: Reliable(),
        progress: new(
            id: new("progress/outbound"),
            scope: ChannelRequirementScope.ForDirection(Outbound),
            floor: ChannelProgressFloorKind.None,
            pending: ChannelPendingProgressKind.ExactStableDeliverySet),
        settlements:
        [
            new(
                id: new("settlement/individual"),
                scope: ChannelRequirementScope.ForDirection(Outbound),
                coupling: ChannelSettlementCouplingKind.PerDelivery,
                operation: ChannelSettlementKind.Individual),
            new(
                id: new("settlement/negative"),
                scope: ChannelRequirementScope.ForDirection(Outbound),
                coupling: ChannelSettlementCouplingKind.PerDelivery,
                operation: ChannelSettlementKind.Negative)
        ]);

    static ChannelDefinition TransientPublication(bool includePayloadLimit)
    {
        var definition = OneWay(
            interaction: ChannelInteractionShape.Publication,
            distribution: ChannelDistributionKind.FanOut,
            routing: ChannelRoutingKind.TopicOrFilter,
            isolation: ChannelRoutingIsolationKind.None,
            framing: ChannelFramingKind.TypedMessage,
            retention: ChannelRetentionKind.ActivationLocal,
            replay: ChannelReplayKind.None,
            guarantee: ChannelDeliveryGuaranteeKind.AtMostOnce,
            ordering: ChannelOrderingScopeKind.None,
            reliability: new(
                id: new("reliability/outbound"),
                scope: ChannelRequirementScope.ForDirection(Outbound),
                reliability: ChannelReliabilityKind.Unreliable));
        if (!includePayloadLimit)
            return definition;

        return new(
            definition.Exchange,
            [
                .. definition.Requirements,
                new ChannelLimitRequirement(
                    id: new("limit/payload"),
                    scope: ChannelRequirementScope.ForDirection(Outbound),
                    kind: ChannelLimitKind.PayloadBytes,
                    value: 1_024)
            ]);
    }

    static ChannelDefinition PartialDatagram() => OneWay(
        interaction: ChannelInteractionShape.Datagram,
        distribution: ChannelDistributionKind.PointToPoint,
        routing: ChannelRoutingKind.ConnectionOrStream,
        isolation: ChannelRoutingIsolationKind.None,
        framing: ChannelFramingKind.Datagram,
        retention: ChannelRetentionKind.ActivationLocal,
        replay: ChannelReplayKind.None,
        guarantee: ChannelDeliveryGuaranteeKind.AtMostOnce,
        ordering: ChannelOrderingScopeKind.None,
        reliability: new(
            id: new("reliability/outbound"),
            scope: ChannelRequirementScope.ForDirection(Outbound),
            reliability: ChannelReliabilityKind.PartiallyReliable,
            maximumLifetime: TimeSpan.FromSeconds(2),
            maximumRetransmissions: 3));

    static ChannelDefinition RequestReply(ChannelInteractionShape interaction, bool includeFlow)
    {
        List<ChannelRequirement> requirements =
        [
            new ChannelTopologyRequirement(
                id: new("topology"),
                scope: ChannelRequirementScope.Exchange,
                distribution: ChannelDistributionKind.PointToPoint,
                interaction: interaction)
        ];
        AddInvocationDirection(requirements, Request, "request", ChannelRoutingKind.OperationEndpoint);
        AddInvocationDirection(requirements, Reply, "reply", ChannelRoutingKind.ConnectionOrStream);
        if (includeFlow)
        {
            requirements.Add(new ChannelFlowRequirement(
                id: new("flow"),
                scope: ChannelRequirementScope.Exchange,
                control: ChannelFlowControlKind.Demand,
                completion: ChannelStreamCompletionKind.HalfClose,
                continuity: ChannelSessionContinuityKind.Reconnect,
                maximumInFlight: 64));
        }
        return new(new RequestReplyChannelExchange(Request, Reply), [.. requirements]);
    }

    static void AddInvocationDirection(
        ICollection<ChannelRequirement> requirements,
        ChannelDirectionId direction,
        string suffix,
        ChannelRoutingKind routing)
    {
        var scope = ChannelRequirementScope.ForDirection(direction);
        requirements.Add(new ChannelRoutingRequirement(
            id: new($"routing/{suffix}"),
            scope: scope,
            routing: routing,
            isolation: ChannelRoutingIsolationKind.InvocationScoped));
        requirements.Add(new ChannelFramingRequirement(
            id: new($"framing/{suffix}"),
            scope: scope,
            framing: ChannelFramingKind.TypedMessage,
            boundaries: ChannelBoundarySemantics.Preserved));
        requirements.Add(new ChannelPersistenceRequirement(
            id: new($"persistence/{suffix}"),
            scope: scope,
            retention: ChannelRetentionKind.ActivationLocal,
            replay: ChannelReplayKind.None));
        requirements.Add(new ChannelDeliveryRequirement(
            id: new($"delivery/{suffix}"),
            scope: scope,
            guarantee: ChannelDeliveryGuaranteeKind.InvocationAttempt,
            ordering: ChannelOrderingScopeKind.Connection));
        requirements.Add(new ChannelReliabilityRequirement(
            id: new($"reliability/{suffix}"),
            scope: scope,
            reliability: ChannelReliabilityKind.Reliable));
    }

    static ChannelDefinition OneWay(
        ChannelInteractionShape interaction,
        ChannelDistributionKind distribution,
        ChannelRoutingKind routing,
        ChannelRoutingIsolationKind isolation,
        ChannelFramingKind framing,
        ChannelRetentionKind retention,
        ChannelReplayKind replay,
        ChannelDeliveryGuaranteeKind guarantee,
        ChannelOrderingScopeKind ordering,
        ChannelReliabilityRequirement reliability,
        ChannelProgressRequirement? progress = null,
        ImmutableArray<ChannelSettlementRequirement> settlements = default,
        TimeSpan? minimumRetention = null)
    {
        var scope = ChannelRequirementScope.ForDirection(Outbound);
        List<ChannelRequirement> requirements =
        [
            new ChannelTopologyRequirement(
                id: new("topology"),
                scope: ChannelRequirementScope.Exchange,
                distribution: distribution,
                interaction: interaction),
            new ChannelRoutingRequirement(
                id: new("routing/outbound"),
                scope: scope,
                routing: routing,
                isolation: isolation),
            new ChannelFramingRequirement(
                id: new("framing/outbound"),
                scope: scope,
                framing: framing,
                boundaries: ChannelBoundarySemantics.Preserved),
            new ChannelPersistenceRequirement(
                id: new("persistence/outbound"),
                scope: scope,
                retention: retention,
                replay: replay,
                minimumRetention: minimumRetention),
            new ChannelDeliveryRequirement(
                id: new("delivery/outbound"),
                scope: scope,
                guarantee: guarantee,
                ordering: ordering),
            reliability
        ];
        if (progress is not null)
            requirements.Add(progress);
        if (!settlements.IsDefaultOrEmpty)
            requirements.AddRange(settlements);
        return new(new OneWayChannelExchange(Outbound), [.. requirements]);
    }

    static ChannelReliabilityRequirement Reliable() => new(
        id: new("reliability/outbound"),
        scope: ChannelRequirementScope.ForDirection(Outbound),
        reliability: ChannelReliabilityKind.Reliable);

    static ChannelCapabilityProfile NativeProfile(
        string id,
        ChannelDefinition definition,
        bool reverseEvidence = false,
        string? description = "Human-facing profile description.")
    {
        var evidence = definition.Requirements.Select(CreateNativeEvidence);
        if (reverseEvidence)
            evidence = evidence.Reverse();
        return new(
            id: new(id),
            subject: "tests/channel-target",
            variants:
            [
                new ChannelCapabilityVariant(
                    id: new("default"),
                    evidence: [.. evidence],
                    description: description)
            ],
            provenance: ProfileProvenance(),
            description: description);
    }

    static ChannelCapabilityProfile Profile(string id, ChannelCapabilityVariant variant) => new(
        id: new(id),
        subject: "tests/channel-target",
        variants: [variant],
        provenance: ProfileProvenance());

    static ChannelCapabilityEvidence CreateNativeEvidence(ChannelRequirement requirement) => new(
        id: EvidenceId(requirement),
        capability: requirement,
        realization: CapabilityRealizationKind.Native,
        sourceReferences: [$"tests://capability/{requirement.Id.Value}"]);

    static ChannelCapabilityEvidenceId EvidenceId(ChannelRequirement requirement) =>
        new($"evidence/{requirement.Id.Value}");

    static ExecutionDefinitionDocument Document(string id, ChannelDefinition definition) =>
        ChannelDefinitionDocuments.Create(
            definitionId: new(id),
            revisionId: new("1"),
            definition: definition,
            provenance: DefinitionProvenance());

    static ChannelRealizationDecision Decision(ChannelRealizationPlan plan, ChannelRequirementId requirement) =>
        Assert.Single(plan.Decisions, decision => decision.Requirement == requirement);

    static ResolvedChannelRealizationPlan Resolve(
        ChannelRealizationPlan plan,
        ExecutionDefinitionDocument document,
        ChannelCapabilityProfile profile,
        ExecutionProvenance provenance)
    {
        var validation = ChannelRealizationPlanValidator.TryResolve(
            plan,
            document,
            profile,
            provenance,
            out var resolved);
        Assert.True(validation.IsValid, Format(validation));
        return Assert.IsType<ResolvedChannelRealizationPlan>(resolved);
    }

    static void AssertInvalid(
        ChannelRealizationPlan plan,
        ExecutionDefinitionDocument document,
        ChannelCapabilityProfile profile,
        ExecutionProvenance provenance,
        string expectedCode)
    {
        var validation = ChannelRealizationPlanValidator.TryResolve(
            plan,
            document,
            profile,
            provenance,
            out var resolved);
        Assert.False(validation.IsValid);
        Assert.Null(resolved);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    static ChannelRealizationPlan Rebuild(
        ChannelRealizationPlan plan,
        string? schemaVersion = null,
        ExecutionDefinitionReference? definition = null,
        ChannelCapabilityProfileReference? profile = null,
        ChannelCapabilityVariantId? variant = null,
        ImmutableArray<ChannelRealizationDecision> decisions = default,
        ImmutableArray<EffectiveConfigurationDecision> configuration = default,
        ExecutionProvenance? provenance = null,
        DocumentValidationResult? validation = null) =>
        new(
            schemaVersion: schemaVersion ?? plan.SchemaVersion,
            definition: definition ?? plan.Definition,
            profile: profile ?? plan.Profile,
            variant: variant ?? plan.Variant,
            decisions: decisions.IsDefault ? plan.Decisions : decisions,
            configuration: configuration.IsDefault ? plan.Configuration : configuration,
            provenance: provenance ?? plan.Provenance,
            validation: validation ?? plan.Validation,
            fingerprint: null);

    static ExecutionProvenance DefinitionProvenance() => new(
        producer: new("tests/channel-definition", "1"),
        source: new("tests://channel-definition"),
        origin: DocumentOrigin.User);

    static ExecutionProvenance ProfileProvenance() => new(
        producer: new("tests/channel-profile", "1"),
        source: new("tests://channel-profile"),
        origin: DocumentOrigin.Generated);

    static ExecutionProvenance CompilerProvenance() => new(
        producer: new("tests/channel-compiler", "1"),
        source: new("tests://channel-realization"),
        origin: DocumentOrigin.Generated);

    static ExecutionProvenance ForgedCompilerProvenance() => new(
        producer: new("tests/forged-channel-compiler", "1"),
        source: new("tests://forged-channel-realization"),
        origin: DocumentOrigin.Generated);

    static string Format(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
