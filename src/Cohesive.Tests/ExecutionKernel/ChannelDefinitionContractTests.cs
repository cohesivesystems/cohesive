using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ChannelDefinitionContractTests
{
    static readonly ChannelDirectionId Outbound = new("outbound");
    static readonly ChannelDirectionId RequestDirection = new("request");
    static readonly ChannelDirectionId ReplyDirection = new("reply");
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void ChannelDefinition_RoundTripsThroughSharedAuthority_AndRequirementOrderIsNonSemantic()
    {
        var definition = PositionedLog();
        var reordered = new ChannelDefinition(definition.Exchange, [.. definition.Requirements.Reverse()]);
        var document = Document("channel/materialization/changes", definition);
        var equivalent = Document("channel/materialization/changes", reordered);

        Assert.Equal(definition, reordered);
        Assert.Equal(document.Metadata.Fingerprint, equivalent.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(equivalent));

        var json = ExecutionDefinitionJsonSerializer.Serialize(document);
        var validation = ChannelDefinitionDocuments.TryDeserialize(
            json,
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.Equal(document, restoredDocument);
        Assert.Equal(definition, restoredDefinition);
        Assert.Equal(
            ExecutionDefinitionJsonSerializer.GetCanonicalBytes(document),
            ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));
        Assert.DoesNotContain("kafka", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pulsar", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serviceBus", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChannelExchangeAndRequirementFamilies_AreClosedAndProviderNeutral()
    {
        AssertDerivedTypes(
            typeof(ChannelExchangeDefinition),
            [
                (typeof(OneWayChannelExchange), ChannelWireNames.OneWayExchange),
                (typeof(RequestReplyChannelExchange), ChannelWireNames.RequestReplyExchange)
            ]);
        AssertDerivedTypes(
            typeof(ChannelRequirement),
            [
                (typeof(ChannelTopologyRequirement), ChannelWireNames.TopologyRequirement),
                (typeof(ChannelRoutingRequirement), ChannelWireNames.RoutingRequirement),
                (typeof(ChannelFramingRequirement), ChannelWireNames.FramingRequirement),
                (typeof(ChannelPersistenceRequirement), ChannelWireNames.PersistenceRequirement),
                (typeof(ChannelProgressRequirement), ChannelWireNames.ProgressRequirement),
                (typeof(ChannelDeliveryRequirement), ChannelWireNames.DeliveryRequirement),
                (typeof(ChannelReliabilityRequirement), ChannelWireNames.ReliabilityRequirement),
                (typeof(ChannelSettlementRequirement), ChannelWireNames.SettlementRequirement),
                (typeof(ChannelFlowRequirement), ChannelWireNames.FlowRequirement),
                (typeof(ChannelAtomicityRequirement), ChannelWireNames.AtomicityRequirement),
                (typeof(ChannelSecurityRequirement), ChannelWireNames.SecurityRequirement),
                (typeof(ChannelLimitRequirement), ChannelWireNames.LimitRequirement)
            ]);

        var productionNames = typeof(ChannelDefinition).Assembly
            .GetTypes()
            .Where(static type => type.Namespace == typeof(ChannelDefinition).Namespace
                                  && type.Name.Contains("Channel", StringComparison.Ordinal))
            .Select(static type => type.Name)
            .ToArray();
        string[] forbidden = ["Kafka", "Pulsar", "JetStream", "ServiceBus", "Sqs", "Mqtt", "ZeroMq", "Grpc", "WebRtc", "RSocket"];
        Assert.All(forbidden, name => Assert.DoesNotContain(productionNames, candidate => candidate.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Validator_PreservesReplayProgressAndSettlementAsIndependentCoexistingAxes()
    {
        var definition = PositionedLog();

        var validation = ChannelDefinitionValidator.Validate(definition);

        Assert.True(validation.IsValid, Format(validation));
        var persistence = Assert.IsType<ChannelPersistenceRequirement>(definition.Find(new("persistence/outbound")));
        var progress = Assert.IsType<ChannelProgressRequirement>(definition.Find(new("progress/outbound")));
        var settlement = Assert.IsType<ChannelSettlementRequirement>(definition.Find(new("settlement/outbound")));
        Assert.Equal(ChannelReplayKind.OrderedPosition, persistence.Replay);
        Assert.Equal(ChannelProgressFloorKind.CumulativePrefix, progress.Floor);
        Assert.Equal(ChannelPendingProgressKind.ExactStableDeliverySet, progress.Pending);
        Assert.Equal(ChannelSettlementKind.CumulativePrefix, settlement.Operation);
    }

    [Fact]
    public void SettlementRequirement_RejectsEveryIllegalOperationCouplingPair()
    {
        HashSet<(ChannelSettlementKind Operation, ChannelSettlementCouplingKind Coupling)> legal =
        [
            (ChannelSettlementKind.InvocationCoupled, ChannelSettlementCouplingKind.Invocation),
            (ChannelSettlementKind.CumulativePrefix, ChannelSettlementCouplingKind.OrderingScope),
            (ChannelSettlementKind.CumulativePrefix, ChannelSettlementCouplingKind.BatchOrCallback),
            (ChannelSettlementKind.Individual, ChannelSettlementCouplingKind.PerDelivery),
            (ChannelSettlementKind.Batch, ChannelSettlementCouplingKind.BatchOrCallback),
            (ChannelSettlementKind.Negative, ChannelSettlementCouplingKind.PerDelivery),
            (ChannelSettlementKind.Defer, ChannelSettlementCouplingKind.PerDelivery),
            (ChannelSettlementKind.Quarantine, ChannelSettlementCouplingKind.PerDelivery)
        ];

        foreach (var operation in Enum.GetValues<ChannelSettlementKind>())
        {
            foreach (var coupling in Enum.GetValues<ChannelSettlementCouplingKind>())
            {
                if (legal.Contains((operation, coupling)))
                {
                    var requirement = new ChannelSettlementRequirement(
                        id: new($"settlement/{operation}/{coupling}"),
                        scope: ChannelRequirementScope.ForDirection(Outbound),
                        coupling: coupling,
                        operation: operation);
                    Assert.Equal(operation, requirement.Operation);
                    Assert.Equal(coupling, requirement.Coupling);
                    continue;
                }

                Assert.Throws<ArgumentException>(() => new ChannelSettlementRequirement(
                    id: new($"settlement/{operation}/{coupling}"),
                    scope: ChannelRequirementScope.ForDirection(Outbound),
                    coupling: coupling,
                    operation: operation));
            }
        }
    }

    [Fact]
    public void SettlementReceipt_RequiresExactOperationCouplingAndCoverageMatrix()
    {
        ChannelScopeId scope = new("scope/orders");
        var progress = new ChannelApplicationProgressReference(scope, "checkpoint/42");
        var cursor = new ChannelReplayCursor(1, scope, new("partition/7"), "offset/42");
        ChannelProviderDeliveryId first = new("delivery/1");
        ChannelProviderDeliveryId second = new("delivery/2");

        foreach (var operation in Enum.GetValues<ChannelSettlementKind>())
        {
            var couplingKind = operation switch
            {
                ChannelSettlementKind.InvocationCoupled => ChannelSettlementCouplingKind.Invocation,
                ChannelSettlementKind.CumulativePrefix => ChannelSettlementCouplingKind.OrderingScope,
                ChannelSettlementKind.Batch => ChannelSettlementCouplingKind.BatchOrCallback,
                ChannelSettlementKind.Individual or ChannelSettlementKind.Negative
                    or ChannelSettlementKind.Defer or ChannelSettlementKind.Quarantine =>
                    ChannelSettlementCouplingKind.PerDelivery,
                _ => throw new InvalidOperationException($"Unhandled settlement operation '{operation}'.")
            };

            foreach (var hasCursor in new[] { false, true })
            {
                foreach (var deliveries in new ImmutableArray<ChannelProviderDeliveryId>[]
                         {
                             [],
                             [first],
                             [first, second]
                         })
                {
                    var valid = operation switch
                    {
                        ChannelSettlementKind.InvocationCoupled => !hasCursor && deliveries.Length == 0,
                        ChannelSettlementKind.CumulativePrefix => hasCursor && deliveries.Length == 0,
                        ChannelSettlementKind.Batch => !hasCursor && deliveries.Length >= 2,
                        ChannelSettlementKind.Individual or ChannelSettlementKind.Negative
                            or ChannelSettlementKind.Defer or ChannelSettlementKind.Quarantine =>
                            !hasCursor && deliveries.Length == 1,
                        _ => false
                    };

                    ChannelSettlementReceipt Create() => new(
                        kind: operation,
                        couplingKind: couplingKind,
                        coupling: new($"coupling/{operation}"),
                        applicationProgress: progress,
                        settledAtUtc: DateTimeOffset.UnixEpoch,
                        throughCursor: hasCursor ? cursor : null,
                        deliveries: deliveries);

                    if (valid)
                    {
                        var receipt = Create();
                        Assert.Equal(couplingKind, receipt.CouplingKind);
                    }
                    else
                    {
                        Assert.Throws<ArgumentException>(Create);
                    }
                }
            }
        }

        Assert.Throws<ArgumentException>(() => new ChannelSettlementReceipt(
            kind: ChannelSettlementKind.CumulativePrefix,
            couplingKind: ChannelSettlementCouplingKind.PerDelivery,
            coupling: new("coupling/illegal"),
            applicationProgress: progress,
            settledAtUtc: DateTimeOffset.UnixEpoch,
            throughCursor: cursor));

        var individual = new ChannelSettlementReceipt(
            kind: ChannelSettlementKind.Individual,
            couplingKind: ChannelSettlementCouplingKind.PerDelivery,
            coupling: new("coupling/individual"),
            applicationProgress: progress,
            settledAtUtc: DateTimeOffset.UnixEpoch,
            deliveries: [first]);
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(individual, options);
        Assert.Equal(individual, JsonSerializer.Deserialize<ChannelSettlementReceipt>(json, options));
        foreach (var requiredProperty in new[]
                 {
                     "kind",
                     "couplingKind",
                     "coupling",
                     "applicationProgress",
                     "settledAtUtc",
                     "deliveries"
                 })
        {
            var incomplete = JsonNode.Parse(json)!.AsObject();
            Assert.True(incomplete.Remove(requiredProperty));
            Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<ChannelSettlementReceipt>(incomplete, options));
        }

        var batch = new ChannelSettlementReceipt(
            kind: ChannelSettlementKind.Batch,
            couplingKind: ChannelSettlementCouplingKind.BatchOrCallback,
            coupling: new("coupling/batch"),
            applicationProgress: progress,
            settledAtUtc: DateTimeOffset.UnixEpoch,
            deliveries: [first, second]);
        var unsortedBatch = JsonNode.Parse(JsonSerializer.Serialize(batch, options))!.AsObject();
        unsortedBatch["deliveries"] = new JsonArray(second.Value, first.Value);
        var normalizedBatch = JsonSerializer.Deserialize<ChannelSettlementReceipt>(unsortedBatch, options)!;
        Assert.True(normalizedBatch.Deliveries.SequenceEqual([first, second]));

        var duplicatedBatch = JsonNode.Parse(JsonSerializer.Serialize(batch, options))!.AsObject();
        duplicatedBatch["deliveries"] = new JsonArray(first.Value, first.Value);
        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<ChannelSettlementReceipt>(duplicatedBatch, options));
    }

    [Fact]
    public void RuntimeProgress_RequiresGapFloor_AllowsEmptyExactSets_AndRejectsExpiredAuthority()
    {
        ChannelScopeId scope = new("scope/orders");
        var stable = new ChannelStableDeliverySetProgress(scope, []);
        var gaps = new ChannelUnresolvedGapProgress(scope, []);

        Assert.False(stable.Deliveries.IsDefault);
        Assert.Empty(stable.Deliveries);
        Assert.False(gaps.Deliveries.IsDefault);
        Assert.Empty(gaps.Deliveries);
        Assert.Throws<ArgumentException>(() => new ChannelDurableProgressEvidence(pending: gaps));
        var progress = new ChannelDurableProgressEvidence(
            floor: new ChannelTargetManagedProgressFloor(1, scope, "floor/42"),
            pending: gaps);
        Assert.Same(gaps, progress.Pending);

        var observedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var expiredAuthority = new ChannelSettlementAuthority(
            id: new("authority/expired"),
            attempt: new("attempt/42"),
            coupling: new("coupling/orders"),
            expiresAtUtc: observedAtUtc);
        Assert.Throws<ArgumentException>(() => new ChannelDeliveryAttemptEvidence(
            attempt: new("attempt/42"),
            observedAtUtc: observedAtUtc,
            scope: scope,
            settlementAuthority: expiredAuthority));
        var expiredCursor = new ChannelReplayCursor(
            formatVersion: 1,
            scope: scope,
            orderingDomain: new("partition/7"),
            value: "offset/42",
            validUntilUtc: observedAtUtc);
        Assert.Throws<ArgumentException>(() => new ChannelDeliveryAttemptEvidence(
            attempt: new("attempt/42"),
            observedAtUtc: observedAtUtc,
            scope: scope,
            replayCursor: expiredCursor));

        var currentAuthority = new ChannelSettlementAuthority(
            id: new("authority/current"),
            attempt: new("attempt/42"),
            coupling: new("coupling/orders"),
            expiresAtUtc: observedAtUtc.AddTicks(1));
        var attempt = new ChannelDeliveryAttemptEvidence(
            attempt: new("attempt/42"),
            observedAtUtc: observedAtUtc,
            scope: scope,
            settlementAuthority: currentAuthority);
        Assert.Same(currentAuthority, attempt.SettlementAuthority);
    }

    [Fact]
    public void Validator_RejectsUnorderedReplayAndProgress_RetainedLatestHistoryWindow_AndDuplicateAtomicScope()
    {
        var positioned = PositionedLog();
        var direction = ChannelRequirementScope.ForDirection(Outbound);
        var unordered = new ChannelDefinition(
            positioned.Exchange,
            [
                .. positioned.Requirements.Where(static requirement => requirement.Id.Value != "delivery/outbound"),
                new ChannelDeliveryRequirement(
                    id: new("delivery/outbound"),
                    scope: direction,
                    guarantee: ChannelDeliveryGuaranteeKind.AtLeastOnce,
                    ordering: ChannelOrderingScopeKind.None)
            ]);
        var unorderedValidation = ChannelDefinitionValidator.Validate(unordered);
        Assert.Contains(
            unorderedValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.DeliveryInvalid
                && diagnostic.Location?.EndsWith("/ordering", StringComparison.Ordinal) == true);
        Assert.Contains(
            unorderedValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.ProgressInvalid);

        var retainedLatestWindow = new ChannelDefinition(
            positioned.Exchange,
            [
                .. positioned.Requirements.Where(static requirement => requirement.Id.Value != "persistence/outbound"),
                new ChannelPersistenceRequirement(
                    id: new("persistence/outbound"),
                    scope: direction,
                    retention: ChannelRetentionKind.RetainedLatest,
                    replay: ChannelReplayKind.None,
                    minimumRetention: TimeSpan.FromHours(1))
            ]);
        Assert.Contains(
            ChannelDefinitionValidator.Validate(retainedLatestWindow).Diagnostics,
            static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.PersistenceInvalid);

        var atomic = Assert.Single(positioned.Requirements.OfType<ChannelAtomicityRequirement>());
        var duplicateAtomicScope = new ChannelDefinition(
            positioned.Exchange,
            [
                .. positioned.Requirements,
                new ChannelAtomicityRequirement(
                    id: new("atomicity/duplicate"),
                    scope: ChannelRequirementScope.Exchange,
                    atomicScope: atomic.AtomicScope,
                    operations: atomic.Operations)
            ]);
        Assert.Contains(
            ChannelDefinitionValidator.Validate(duplicateAtomicScope).Diagnostics,
            static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.RequirementDuplicate);

        var oneWayRequestAtomicity = new ChannelDefinition(
            positioned.Exchange,
            [
                .. positioned.Requirements,
                new ChannelAtomicityRequirement(
                    id: new("atomicity/request-on-one-way"),
                    scope: ChannelRequirementScope.Exchange,
                    atomicScope: new("request-admission-and-reply-obligation"),
                    operations:
                    [
                        ChannelAtomicOperationKind.RequestAdmission,
                        ChannelAtomicOperationKind.ReplyObligation
                    ])
            ]);
        Assert.Contains(
            ChannelDefinitionValidator.Validate(oneWayRequestAtomicity).Diagnostics,
            static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.AtomicityInvalid);

        Assert.Throws<ArgumentException>(() => new ChannelProgressRequirement(
            id: new("progress/gaps-without-floor"),
            scope: direction,
            floor: ChannelProgressFloorKind.None,
            pending: ChannelPendingProgressKind.PrefixWithUnresolvedGaps));
    }

    [Fact]
    public void Validator_AllowsSeveralDistinctSettlementModes_AndRejectsAnExactDuplicate()
    {
        var positioned = PositionedLog();
        var negative = new ChannelSettlementRequirement(
            id: new("settlement/negative"),
            scope: ChannelRequirementScope.ForDirection(Outbound),
            coupling: ChannelSettlementCouplingKind.PerDelivery,
            operation: ChannelSettlementKind.Negative);
        var multimode = new ChannelDefinition(
            positioned.Exchange,
            [.. positioned.Requirements, negative]);

        var validation = ChannelDefinitionValidator.Validate(multimode);

        Assert.True(validation.IsValid, Format(validation));
        Assert.Equal(2, multimode.Requirements.OfType<ChannelSettlementRequirement>().Count());
        var reordered = new ChannelDefinition(multimode.Exchange, [.. multimode.Requirements.Reverse()]);
        Assert.Equal(multimode, reordered);
        Assert.Equal(
            Document("channel/multimode", multimode).Metadata.Fingerprint,
            Document("channel/multimode", reordered).Metadata.Fingerprint);

        var duplicate = new ChannelDefinition(
            multimode.Exchange,
            [
                .. multimode.Requirements,
                new ChannelSettlementRequirement(
                    id: new("settlement/negative-copy"),
                    scope: ChannelRequirementScope.ForDirection(Outbound),
                    coupling: ChannelSettlementCouplingKind.PerDelivery,
                    operation: ChannelSettlementKind.Negative)
            ]);
        var duplicateValidation = ChannelDefinitionValidator.Validate(duplicate);
        Assert.Contains(
            duplicateValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.RequirementDuplicate);
    }

    [Fact]
    public void Validator_RejectsActivationLocalDurableProgress_UnreliableAtLeastOnce_AndUnsafeReplyRouting()
    {
        var coupled = CoupledInvocation();
        var invalidRequirements = coupled.Requirements
            .Where(static requirement => requirement.Id.Value is not "routing/reply" and not "delivery/reply" and not "reliability/reply")
            .Append(new ChannelRoutingRequirement(
                new("routing/reply"),
                ChannelRequirementScope.ForDirection(ReplyDirection),
                ChannelRoutingKind.ExplicitResponseTarget,
                ChannelRoutingIsolationKind.None))
            .Append(new ChannelDeliveryRequirement(
                new("delivery/reply"),
                ChannelRequirementScope.ForDirection(ReplyDirection),
                ChannelDeliveryGuaranteeKind.AtLeastOnce,
                ChannelOrderingScopeKind.Connection))
            .Append(new ChannelReliabilityRequirement(
                new("reliability/reply"),
                ChannelRequirementScope.ForDirection(ReplyDirection),
                ChannelReliabilityKind.Unreliable))
            .Append(new ChannelProgressRequirement(
                new("progress/reply"),
                ChannelRequirementScope.ForDirection(ReplyDirection),
                ChannelProgressFloorKind.CumulativePrefix,
                ChannelPendingProgressKind.None));
        var invalid = new ChannelDefinition(coupled.Exchange, [.. invalidRequirements]);

        var validation = ChannelDefinitionValidator.Validate(invalid);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.RoutingUnsafe);
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.ProgressInvalid);
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.DeliveryInvalid);
    }

    [Fact]
    public void StrictReader_RejectsUnknownChannelMembersAndNonCanonicalRequirementOrder()
    {
        var document = Document("channel/strict", PositionedLog());
        var root = JsonNode.Parse(ExecutionDefinitionJsonSerializer.Serialize(document))!.AsObject();
        root["unknownProviderSetting"] = "forbidden";

        var unknownValidation = ChannelDefinitionDocuments.TryDeserialize(
            root.ToJsonString(ExecutionDefinitionJsonSerializer.CreateOptions()),
            out _,
            out _);

        Assert.False(unknownValidation.IsValid);

        var canonical = JsonNode.Parse(ExecutionDefinitionJsonSerializer.Serialize(document))!.AsObject();
        var requirements = canonical["definition"]!["requirements"]!.AsArray();
        var first = requirements[0]!.DeepClone();
        var last = requirements[^1]!.DeepClone();
        requirements[0] = last;
        requirements[^1] = first;
        var reorderedValidation = ChannelDefinitionDocuments.TryDeserialize(
            canonical.ToJsonString(ExecutionDefinitionJsonSerializer.CreateOptions()),
            out _,
            out _);

        Assert.False(reorderedValidation.IsValid);
        Assert.Contains(
            reorderedValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelDefinitionDocumentDiagnosticCodes.DefinitionWireNonCanonical);

        var numericEnum = JsonNode.Parse(ExecutionDefinitionJsonSerializer.Serialize(document))!.AsObject();
        var topology = numericEnum["definition"]!["requirements"]!.AsArray()
            .Select(static requirement => requirement!.AsObject())
            .Single(static requirement => (string?)requirement[ChannelWireNames.RequirementDiscriminator]
                == ChannelWireNames.TopologyRequirement);
        topology["distribution"] = 0;

        var numericValidation = ChannelDefinitionDocuments.TryDeserialize(
            numericEnum.ToJsonString(ExecutionDefinitionJsonSerializer.CreateOptions()),
            out _,
            out _);

        Assert.False(numericValidation.IsValid);
    }

    [Fact]
    public void RequestReplyBinding_RequiresExactContractsAndTwoLogicalDirections_ForPairedOrCoupledRealizations()
    {
        var interactions = CreateInteractionFixture();
        var coupledDocument = Document("channel/rpc", CoupledInvocation());
        var coupledReference = Reference(coupledDocument);
        var coupled = new ChannelRequestReplyBinding(
            ChannelRequestReplyBindingKind.CoupledExchange,
            interactions.RequestReference,
            interactions.ReplyReference,
            new(coupledReference, RequestDirection),
            new(coupledReference, ReplyDirection));

        var coupledValidation = ChannelInteractionBindingValidator.Validate(
            coupled,
            interactions.Catalog,
            [coupledDocument]);

        Assert.True(coupledValidation.IsValid, Format(coupledValidation));

        var requestDocument = Document("channel/request-lane", OneWayLane("request-lane"));
        var replyDocument = Document("channel/reply-lane", OneWayLane("reply-lane"));
        var paired = new ChannelRequestReplyBinding(
            ChannelRequestReplyBindingKind.PairedChannels,
            interactions.RequestReference,
            interactions.ReplyReference,
            new(Reference(requestDocument), new("request-lane")),
            new(Reference(replyDocument), new("reply-lane")));
        var pairedValidation = ChannelInteractionBindingValidator.Validate(
            paired,
            interactions.Catalog,
            [requestDocument, replyDocument]);

        Assert.True(pairedValidation.IsValid, Format(pairedValidation));

        var unsafeReplyDocument = Document(
            "channel/unsafe-reply-lane",
            OneWayLane("unsafe-reply-lane", ChannelRoutingIsolationKind.None));
        var unsafeReply = new ChannelRequestReplyBinding(
            ChannelRequestReplyBindingKind.PairedChannels,
            interactions.RequestReference,
            interactions.ReplyReference,
            new(Reference(requestDocument), new("request-lane")),
            new(Reference(unsafeReplyDocument), new("unsafe-reply-lane")));
        var unsafeReplyValidation = ChannelInteractionBindingValidator.Validate(
            unsafeReply,
            interactions.Catalog,
            [requestDocument, unsafeReplyDocument]);
        Assert.Contains(
            unsafeReplyValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelInteractionBindingDiagnosticCodes.PairedExchangeInvalid);

        var sameDefinitionDifferentRevision = new ExecutionDefinitionReference(
            requestDocument.Metadata.DefinitionId,
            new("different-revision"),
            requestDocument.Metadata.Fingerprint);
        Assert.Throws<ArgumentException>(() => new ChannelRequestReplyBinding(
            ChannelRequestReplyBindingKind.PairedChannels,
            interactions.RequestReference,
            interactions.ReplyReference,
            new(Reference(requestDocument), new("request-lane")),
            new(sameDefinitionDifferentRevision, new("reply-lane"))));

        var swapped = new ChannelRequestReplyBinding(
            ChannelRequestReplyBindingKind.CoupledExchange,
            interactions.RequestReference,
            interactions.ReplyReference,
            new(coupledReference, ReplyDirection),
            new(coupledReference, RequestDirection));
        var invalid = ChannelInteractionBindingValidator.Validate(
            swapped,
            interactions.Catalog,
            [coupledDocument]);
        Assert.Contains(invalid.Diagnostics, static diagnostic => diagnostic.Code == ChannelInteractionBindingDiagnosticCodes.CoupledExchangeInvalid);
    }

    [Fact]
    public void RequestReplyCompiler_RequiresAuthoritativeRealizationsForBothLogicalDirections()
    {
        var interactions = CreateInteractionFixture();
        var compilerProvenance = CompilerProvenance();
        var coupledDocument = Document("channel/rpc-realized", CoupledInvocation());
        var coupledReference = Reference(coupledDocument);
        var coupledBinding = new ChannelRequestReplyBinding(
            ChannelRequestReplyBindingKind.CoupledExchange,
            interactions.RequestReference,
            interactions.ReplyReference,
            new(coupledReference, RequestDirection),
            new(coupledReference, ReplyDirection));
        var coupled = Resolve(coupledDocument, compilerProvenance);

        var coupledValidation = ChannelRequestReplyRealizationCompiler.TryCompile(
            coupledBinding,
            interactions.Catalog,
            [coupledDocument],
            [coupled],
            out var coupledRealization);

        Assert.True(coupledValidation.IsValid, Format(coupledValidation));
        Assert.NotNull(coupledRealization);
        Assert.Same(coupled, coupledRealization.Request);
        Assert.Same(coupled, coupledRealization.Reply);

        var requestDocument = Document("channel/request-realized", OneWayLane("request-realized"));
        var replyDocument = Document("channel/reply-realized", OneWayLane("reply-realized"));
        var pairedBinding = new ChannelRequestReplyBinding(
            ChannelRequestReplyBindingKind.PairedChannels,
            interactions.RequestReference,
            interactions.ReplyReference,
            new(Reference(requestDocument), new("request-realized")),
            new(Reference(replyDocument), new("reply-realized")));
        var request = Resolve(requestDocument, compilerProvenance);
        var reply = Resolve(replyDocument, compilerProvenance);

        var pairedValidation = ChannelRequestReplyRealizationCompiler.TryCompile(
            pairedBinding,
            interactions.Catalog,
            [requestDocument, replyDocument],
            [request, reply],
            out var pairedRealization);

        Assert.True(pairedValidation.IsValid, Format(pairedValidation));
        Assert.NotNull(pairedRealization);
        Assert.Same(request, pairedRealization.Request);
        Assert.Same(reply, pairedRealization.Reply);

        var missingReply = ChannelRequestReplyRealizationCompiler.TryCompile(
            pairedBinding,
            interactions.Catalog,
            [requestDocument, replyDocument],
            [request],
            out var missingRealization);
        Assert.Null(missingRealization);
        Assert.Contains(
            missingReply.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelInteractionBindingDiagnosticCodes.RealizationUnavailable);

        var ambiguous = ChannelRequestReplyRealizationCompiler.TryCompile(
            coupledBinding,
            interactions.Catalog,
            [coupledDocument],
            [coupled, coupled],
            out var ambiguousRealization);
        Assert.Null(ambiguousRealization);
        Assert.Contains(
            ambiguous.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelInteractionBindingDiagnosticCodes.RealizationAmbiguous);
    }

    [Fact]
    public void RuntimeEvidence_SeparatesProviderIdentityAttemptAuthorityReplayAndDurableProgress()
    {
        ChannelScopeId scope = new("scope/orders");
        var cursor = new ChannelReplayCursor(1, scope, new("partition/7"), "offset/42");
        ChannelProviderDeliveryId providerDelivery = new("provider/message/9");
        var first = new ChannelDeliveryAttemptEvidence(
            attempt: new("attempt/1"),
            observedAtUtc: DateTimeOffset.UnixEpoch,
            scope: scope,
            providerDelivery: providerDelivery,
            replayCursor: cursor,
            settlementAuthority: new(new("receipt/1"), new("attempt/1"), new("settlement/batch/4")));
        var second = new ChannelDeliveryAttemptEvidence(
            attempt: new("attempt/2"),
            observedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(1),
            scope: scope,
            providerDelivery: providerDelivery,
            replayCursor: cursor,
            settlementAuthority: new(new("receipt/2"), new("attempt/2"), new("settlement/batch/4")));
        var progress = new ChannelDurableProgressEvidence(
            replayCursor: cursor,
            floor: new ChannelReplayCursorProgressFloor(cursor),
            pending: new ChannelStableDeliverySetProgress(scope, [providerDelivery]));

        Assert.Equal(first.ProviderDelivery, second.ProviderDelivery);
        Assert.NotEqual(first.Attempt, second.Attempt);
        Assert.NotEqual(first.SettlementAuthority, second.SettlementAuthority);
        Assert.Equal(cursor, progress.ReplayCursor);
        Assert.IsType<ChannelReplayCursorProgressFloor>(progress.Floor);
        Assert.Equal(providerDelivery, Assert.Single(Assert.IsType<ChannelStableDeliverySetProgress>(progress.Pending).Deliveries));
    }

    static ChannelDefinition PositionedLog()
    {
        var direction = ChannelRequirementScope.ForDirection(Outbound);
        return new(
            new OneWayChannelExchange(Outbound),
            [
                new ChannelTopologyRequirement(
                    new("topology"),
                    ChannelRequirementScope.Exchange,
                    ChannelDistributionKind.CompetingConsumers,
                    ChannelInteractionShape.Publication),
                new ChannelRoutingRequirement(
                    new("routing/outbound"),
                    direction,
                    ChannelRoutingKind.KeyOrSessionAffinity,
                    ChannelRoutingIsolationKind.SelectiveAcquisition),
                new ChannelFramingRequirement(
                    new("framing/outbound"),
                    direction,
                    ChannelFramingKind.TypedMessage,
                    ChannelBoundarySemantics.Preserved),
                new ChannelPersistenceRequirement(
                    new("persistence/outbound"),
                    direction,
                    ChannelRetentionKind.RetainedHistory,
                    ChannelReplayKind.OrderedPosition,
                    TimeSpan.FromHours(1)),
                new ChannelProgressRequirement(
                    new("progress/outbound"),
                    direction,
                    ChannelProgressFloorKind.CumulativePrefix,
                    ChannelPendingProgressKind.ExactStableDeliverySet),
                new ChannelDeliveryRequirement(
                    new("delivery/outbound"),
                    direction,
                    ChannelDeliveryGuaranteeKind.AtLeastOnce,
                    ChannelOrderingScopeKind.PartitionKeyOrSession),
                new ChannelReliabilityRequirement(
                    new("reliability/outbound"),
                    direction,
                    ChannelReliabilityKind.Reliable),
                new ChannelSettlementRequirement(
                    new("settlement/outbound"),
                    direction,
                    ChannelSettlementCouplingKind.OrderingScope,
                    ChannelSettlementKind.CumulativePrefix),
                new ChannelAtomicityRequirement(
                    new("atomicity/apply"),
                    ChannelRequirementScope.Exchange,
                    new("apply-progress-settle"),
                    [
                        ChannelAtomicOperationKind.Consumption,
                        ChannelAtomicOperationKind.ApplicationCheckpoint,
                        ChannelAtomicOperationKind.Settlement
                    ]),
                new ChannelSecurityRequirement(
                    new("security"),
                    ChannelRequirementScope.Exchange,
                    [ChannelSecurityKind.Confidentiality, ChannelSecurityKind.Integrity]),
                new ChannelLimitRequirement(
                    new("limit/payload"),
                    direction,
                    ChannelLimitKind.PayloadBytes,
                    1_048_576)
            ]);
    }

    static ChannelDefinition CoupledInvocation()
    {
        var request = ChannelRequirementScope.ForDirection(RequestDirection);
        var reply = ChannelRequirementScope.ForDirection(ReplyDirection);
        List<ChannelRequirement> requirements =
        [
            new ChannelTopologyRequirement(
                new("topology"),
                ChannelRequirementScope.Exchange,
                ChannelDistributionKind.PointToPoint,
                ChannelInteractionShape.UnaryInvocation)
        ];
        AddInvocationDirection(requirements, request, "request", ChannelRoutingKind.OperationEndpoint);
        AddInvocationDirection(requirements, reply, "reply", ChannelRoutingKind.ConnectionOrStream);
        return new(new RequestReplyChannelExchange(RequestDirection, ReplyDirection), [.. requirements]);
    }

    static void AddInvocationDirection(
        ICollection<ChannelRequirement> requirements,
        ChannelRequirementScope scope,
        string suffix,
        ChannelRoutingKind routing)
    {
        requirements.Add(new ChannelRoutingRequirement(
            new($"routing/{suffix}"),
            scope,
            routing,
            ChannelRoutingIsolationKind.InvocationScoped));
        requirements.Add(new ChannelFramingRequirement(
            new($"framing/{suffix}"),
            scope,
            ChannelFramingKind.TypedMessage,
            ChannelBoundarySemantics.Preserved));
        requirements.Add(new ChannelPersistenceRequirement(
            new($"persistence/{suffix}"),
            scope,
            ChannelRetentionKind.ActivationLocal,
            ChannelReplayKind.None));
        requirements.Add(new ChannelDeliveryRequirement(
            new($"delivery/{suffix}"),
            scope,
            ChannelDeliveryGuaranteeKind.InvocationAttempt,
            ChannelOrderingScopeKind.Connection));
        requirements.Add(new ChannelReliabilityRequirement(
            new($"reliability/{suffix}"),
            scope,
            ChannelReliabilityKind.Reliable));
        requirements.Add(new ChannelSettlementRequirement(
            new($"settlement/{suffix}"),
            scope,
            ChannelSettlementCouplingKind.Invocation,
            ChannelSettlementKind.InvocationCoupled));
    }

    static ChannelDefinition OneWayLane(
        string directionValue,
        ChannelRoutingIsolationKind isolation = ChannelRoutingIsolationKind.DedicatedTarget)
    {
        ChannelDirectionId directionId = new(directionValue);
        var direction = ChannelRequirementScope.ForDirection(directionId);
        return new(
            new OneWayChannelExchange(directionId),
            [
                new ChannelTopologyRequirement(
                    new("topology"),
                    ChannelRequirementScope.Exchange,
                    ChannelDistributionKind.PointToPoint,
                    ChannelInteractionShape.FireAndForget),
                new ChannelRoutingRequirement(
                    new($"routing/{directionValue}"),
                    direction,
                    ChannelRoutingKind.ExplicitResponseTarget,
                    isolation),
                new ChannelFramingRequirement(
                    new($"framing/{directionValue}"),
                    direction,
                    ChannelFramingKind.TypedMessage,
                    ChannelBoundarySemantics.Preserved),
                new ChannelPersistenceRequirement(
                    new($"persistence/{directionValue}"),
                    direction,
                    ChannelRetentionKind.DurableUntilSettled,
                    ChannelReplayKind.None),
                new ChannelProgressRequirement(
                    new($"progress/{directionValue}"),
                    direction,
                    ChannelProgressFloorKind.None,
                    ChannelPendingProgressKind.ExactStableDeliverySet),
                new ChannelDeliveryRequirement(
                    new($"delivery/{directionValue}"),
                    direction,
                    ChannelDeliveryGuaranteeKind.AtLeastOnce,
                    ChannelOrderingScopeKind.None),
                new ChannelReliabilityRequirement(
                    new($"reliability/{directionValue}"),
                    direction,
                    ChannelReliabilityKind.Reliable),
                new ChannelSettlementRequirement(
                    new($"settlement/{directionValue}"),
                    direction,
                    ChannelSettlementCouplingKind.PerDelivery,
                    ChannelSettlementKind.Individual)
            ]);
    }

    static ExecutionDefinitionDocument Document(string id, ChannelDefinition definition) =>
        ChannelDefinitionDocuments.Create(
            new(id),
            new("1"),
            definition,
            Provenance());

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint);

    static ResolvedChannelRealizationPlan Resolve(
        ExecutionDefinitionDocument document,
        ExecutionProvenance compilerProvenance)
    {
        var definition = document.GetDefinition<ChannelDefinition>();
        ChannelCapabilityProfile profile = new(
            id: new($"profile/{document.Metadata.DefinitionId.Value}"),
            subject: "tests/request-reply-target",
            variants:
            [
                new ChannelCapabilityVariant(
                    id: new("default"),
                    evidence:
                    [
                        .. definition.Requirements.Select(static requirement => new ChannelCapabilityEvidence(
                            id: new($"evidence/{requirement.Id.Value}"),
                            capability: requirement,
                            realization: Cohesive.Model.CapabilityRealizationKind.Native,
                            sourceReferences: [$"tests://request-reply/{requirement.Id.Value}"]))
                    ])
            ],
            provenance: Provenance());
        var plan = ChannelRealizationCompiler.Compile(document, profile, compilerProvenance);
        var validation = ChannelRealizationPlanValidator.TryResolve(
            plan,
            document,
            profile,
            compilerProvenance,
            out var resolved);
        Assert.True(validation.IsValid, Format(validation));
        return Assert.IsType<ResolvedChannelRealizationPlan>(resolved);
    }

    static InteractionFixture CreateInteractionFixture()
    {
        var outcome = new RequestResultDefinition(
            new("accepted"),
            new(StringContract, new("reply/v1")));
        var obligation = new RequestResponseObligation(
            [outcome],
            RequestOptionalTerminalSemantics.Unsupported,
            RequestOptionalTerminalSemantics.Unsupported,
            RequestResultDisposition.Observe,
            RequestResultDisposition.Reject,
            RequestResultDisposition.ReusePriorDisposition,
            RequestRetrySemantics.StableIdentity,
            RequestResolutionSemantics.Reconcile,
            RequestResolutionSemantics.Escalate,
            TimeSpan.FromHours(1));
        var requestDocument = InteractionContractDocuments.Create(
            new("interaction/request/channel-test"),
            new("1"),
            new RequestContractDefinition(new(StringContract, new("request/v1")), obligation),
            Provenance());
        var requestReference = new RequestContractReference(Reference(requestDocument));
        var replyDocument = InteractionContractDocuments.Create(
            new("interaction/reply/channel-test"),
            new("1"),
            new ReplyContractDefinition(requestReference, new("accepted")),
            Provenance());
        var replyReference = new ReplyContractReference(Reference(replyDocument));
        var validation = InteractionContractCatalog.TryCreate(
            [requestDocument, replyDocument],
            out var catalog);
        Assert.True(validation.IsValid, Format(validation));
        return new(requestReference, replyReference, catalog!);
    }

    static ExecutionProvenance Provenance() =>
        new(
            new ExecutionProducerProvenance("tests/channel-ir", "1"),
            new ExecutionSourceProvenance("tests://channel-ir"),
            DocumentOrigin.User);

    static ExecutionProvenance CompilerProvenance() =>
        new(
            new ExecutionProducerProvenance("tests/channel-request-reply-compiler", "1"),
            new ExecutionSourceProvenance("tests://channel-request-reply-compiler"),
            DocumentOrigin.Generated);

    static ImmutableArray<(Type Type, string Discriminator)> DerivedTypes(Type root) =>
        root.GetCustomAttributes<System.Text.Json.Serialization.JsonDerivedTypeAttribute>()
            .Select(static attribute => (attribute.DerivedType, (string)attribute.TypeDiscriminator!))
            .ToImmutableArray();

    static void AssertDerivedTypes(
        Type root,
        ImmutableArray<(Type Type, string Discriminator)> expected)
    {
        var actual = DerivedTypes(root);
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Same(expected[index].Type, actual[index].Type);
            Assert.Equal(expected[index].Discriminator, actual[index].Discriminator);
        }
    }

    static string Format(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    sealed record InteractionFixture(
        RequestContractReference RequestReference,
        ReplyContractReference ReplyReference,
        InteractionContractCatalog Catalog);
}
