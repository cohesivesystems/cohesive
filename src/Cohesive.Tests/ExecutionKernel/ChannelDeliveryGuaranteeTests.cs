using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ChannelDeliveryGuaranteeTests
{
    static readonly ChannelDirectionId Outbound = new("outbound");
    static readonly ChannelRequirementScope Scope = ChannelRequirementScope.ForDirection(Outbound);

    [Fact]
    public void ProtocolExactlyOnce_IsStrongerChannelDeliveryEvidenceButNotApplicationAtomicity()
    {
        var atMostOnce = Delivery("delivery/at-most-once", ChannelDeliveryGuaranteeKind.AtMostOnce);
        var atLeastOnce = Delivery("delivery/at-least-once", ChannelDeliveryGuaranteeKind.AtLeastOnce);
        var protocolExactlyOnce = Delivery("delivery/protocol-exactly-once", ChannelDeliveryGuaranteeKind.ProtocolExactlyOnce);

        Assert.True(ChannelRequirementCompatibility.Satisfies(atMostOnce, protocolExactlyOnce));
        Assert.True(ChannelRequirementCompatibility.Satisfies(atLeastOnce, protocolExactlyOnce));
        Assert.False(ChannelRequirementCompatibility.Satisfies(protocolExactlyOnce, atLeastOnce));
        Assert.False(ChannelRequirementCompatibility.Satisfies(protocolExactlyOnce, atMostOnce));

        var definition = Definition(
            protocolExactlyOnce,
            reliability: ChannelReliabilityKind.Reliable);
        Assert.True(ChannelDefinitionValidator.Validate(definition).IsValid);
        Assert.Empty(definition.Requirements.OfType<ChannelAtomicityRequirement>());
    }

    [Fact]
    public void ProtocolExactlyOnce_RejectsUnreliableTransportAndRoundTripsAsStrictCanonicalIr()
    {
        var invalid = Definition(
            Delivery("delivery/protocol-exactly-once", ChannelDeliveryGuaranteeKind.ProtocolExactlyOnce),
            reliability: ChannelReliabilityKind.Unreliable);

        var validation = ChannelDefinitionValidator.Validate(invalid);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.DeliveryInvalid);

        var valid = Definition(
            Delivery("delivery/protocol-exactly-once", ChannelDeliveryGuaranteeKind.ProtocolExactlyOnce),
            reliability: ChannelReliabilityKind.Reliable);
        var document = ChannelDefinitionDocuments.Create(
            definitionId: new("channel/protocol-exactly-once"),
            revisionId: new("1"),
            definition: valid,
            provenance: Provenance());
        var json = ExecutionDefinitionJsonSerializer.Serialize(document);
        var restored = ExecutionDefinitionJsonSerializer.Deserialize(json);

        Assert.Equal(document, restored);
        Assert.Contains("\"ProtocolExactlyOnce\"", json, StringComparison.Ordinal);
    }

    static ChannelDeliveryRequirement Delivery(string id, ChannelDeliveryGuaranteeKind guarantee) => new(
        id: new(id),
        scope: Scope,
        guarantee: guarantee,
        ordering: ChannelOrderingScopeKind.None);

    static ChannelDefinition Definition(
        ChannelDeliveryRequirement delivery,
        ChannelReliabilityKind reliability) =>
        new(
            new OneWayChannelExchange(Outbound),
            [
                new ChannelTopologyRequirement(
                    id: new("topology"),
                    scope: ChannelRequirementScope.Exchange,
                    distribution: ChannelDistributionKind.PointToPoint,
                    interaction: ChannelInteractionShape.FireAndForget),
                new ChannelRoutingRequirement(
                    id: new("routing/outbound"),
                    scope: Scope,
                    routing: ChannelRoutingKind.OperationEndpoint,
                    isolation: ChannelRoutingIsolationKind.None),
                new ChannelFramingRequirement(
                    id: new("framing/outbound"),
                    scope: Scope,
                    framing: ChannelFramingKind.TypedMessage,
                    boundaries: ChannelBoundarySemantics.Preserved),
                new ChannelPersistenceRequirement(
                    id: new("persistence/outbound"),
                    scope: Scope,
                    retention: ChannelRetentionKind.ActivationLocal,
                    replay: ChannelReplayKind.None),
                delivery,
                new ChannelReliabilityRequirement(
                    id: new("reliability/outbound"),
                    scope: Scope,
                    reliability: reliability)
            ]);

    static ExecutionProvenance Provenance() => new(
        producer: new("tests/channel-guarantee", "1"),
        source: new("tests://channel-guarantee"),
        origin: DocumentOrigin.User);
}
