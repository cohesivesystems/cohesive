using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ChannelFlowLifecycleTests
{
    static readonly ChannelRequirementScope Exchange = ChannelRequirementScope.Exchange;

    [Fact]
    public void InitiationLease_IsAValidatedSessionCapabilityIndependentOfResumeAndCancellation()
    {
        var flow = new ChannelFlowRequirement(
            id: new("flow/session"),
            scope: Exchange,
            control: ChannelFlowControlKind.Demand,
            completion: ChannelStreamCompletionKind.HalfClose,
            continuity: ChannelSessionContinuityKind.BoundedResume,
            maximumInFlight: 32,
            resumeWindow: TimeSpan.FromMinutes(1),
            cancellation: ChannelCancellationKind.InvocationOrStream,
            initiationLease: new(
                minimumInitiations: 8,
                minimumValidity: TimeSpan.FromSeconds(30)));

        Assert.Equal(ChannelCancellationKind.InvocationOrStream, flow.Cancellation);
        Assert.Equal(8, flow.InitiationLease!.MinimumInitiations);
        Assert.Equal(TimeSpan.FromSeconds(30), flow.InitiationLease.MinimumValidity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelInitiationLease(
            minimumInitiations: 0,
            minimumValidity: TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelInitiationLease(
            minimumInitiations: 1,
            minimumValidity: TimeSpan.Zero));
    }

    [Fact]
    public void FlowCompatibility_RequiresTheExactCancellationEffectAndSufficientLeaseCapacity()
    {
        var required = Flow(
            cancellation: ChannelCancellationKind.InvocationOrStream,
            lease: new(minimumInitiations: 8, minimumValidity: TimeSpan.FromSeconds(30)));
        var sufficient = Flow(
            cancellation: ChannelCancellationKind.InvocationOrStream,
            lease: new(minimumInitiations: 16, minimumValidity: TimeSpan.FromMinutes(1)));
        var insufficientAllowance = Flow(
            cancellation: ChannelCancellationKind.InvocationOrStream,
            lease: new(minimumInitiations: 4, minimumValidity: TimeSpan.FromMinutes(1)));
        var wrongCancellation = Flow(
            cancellation: ChannelCancellationKind.Session,
            lease: new(minimumInitiations: 16, minimumValidity: TimeSpan.FromMinutes(1)));

        Assert.True(ChannelRequirementCompatibility.Satisfies(required, sufficient));
        Assert.False(ChannelRequirementCompatibility.Satisfies(required, insufficientAllowance));
        Assert.False(ChannelRequirementCompatibility.Satisfies(required, wrongCancellation));
    }

    [Fact]
    public void InitiationLease_RejectsPublicationButIsValidForReactiveFireAndForget()
    {
        var publication = OneWay(
            interaction: ChannelInteractionShape.Publication,
            flow: Flow(
                cancellation: ChannelCancellationKind.None,
                lease: new(minimumInitiations: 1, minimumValidity: TimeSpan.FromSeconds(1))));
        var reactive = OneWay(
            interaction: ChannelInteractionShape.FireAndForget,
            flow: Flow(
                cancellation: ChannelCancellationKind.InvocationOrStream,
                lease: new(minimumInitiations: 1, minimumValidity: TimeSpan.FromSeconds(1))));

        Assert.Contains(
            ChannelDefinitionValidator.Validate(publication).Diagnostics,
            static diagnostic => diagnostic.Code == ChannelDefinitionDiagnosticCodes.FlowInvalid);
        Assert.True(ChannelDefinitionValidator.Validate(reactive).IsValid);
    }

    static ChannelFlowRequirement Flow(
        ChannelCancellationKind cancellation,
        ChannelInitiationLease? lease) => new(
            id: new("flow/session"),
            scope: Exchange,
            control: ChannelFlowControlKind.Demand,
            completion: ChannelStreamCompletionKind.Terminal,
            continuity: ChannelSessionContinuityKind.BoundedResume,
            maximumInFlight: 32,
            resumeWindow: TimeSpan.FromMinutes(1),
            cancellation: cancellation,
            initiationLease: lease);

    static ChannelDefinition OneWay(
        ChannelInteractionShape interaction,
        ChannelFlowRequirement flow)
    {
        ChannelDirectionId direction = new("outbound");
        var scope = ChannelRequirementScope.ForDirection(direction);
        return new(
            new OneWayChannelExchange(direction),
            [
                new ChannelTopologyRequirement(
                    id: new("topology"),
                    scope: Exchange,
                    distribution: ChannelDistributionKind.PointToPoint,
                    interaction: interaction),
                new ChannelRoutingRequirement(
                    id: new("routing/outbound"),
                    scope: scope,
                    routing: ChannelRoutingKind.OperationEndpoint,
                    isolation: ChannelRoutingIsolationKind.InvocationScoped),
                new ChannelFramingRequirement(
                    id: new("framing/outbound"),
                    scope: scope,
                    framing: ChannelFramingKind.TypedMessage,
                    boundaries: ChannelBoundarySemantics.Preserved),
                new ChannelPersistenceRequirement(
                    id: new("persistence/outbound"),
                    scope: scope,
                    retention: ChannelRetentionKind.ActivationLocal,
                    replay: ChannelReplayKind.None),
                new ChannelDeliveryRequirement(
                    id: new("delivery/outbound"),
                    scope: scope,
                    guarantee: interaction == ChannelInteractionShape.FireAndForget
                        ? ChannelDeliveryGuaranteeKind.InvocationAttempt
                        : ChannelDeliveryGuaranteeKind.AtMostOnce,
                    ordering: ChannelOrderingScopeKind.Connection),
                new ChannelReliabilityRequirement(
                    id: new("reliability/outbound"),
                    scope: scope,
                    reliability: ChannelReliabilityKind.Reliable),
                flow
            ]);
    }
}
