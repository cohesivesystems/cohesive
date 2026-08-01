using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

/// <summary>
/// Provider and protocol examples expressed exclusively through canonical Channel semantics. Provider names are
/// test-case labels and evidence references; they are deliberately not production capability types.
/// </summary>
public sealed class ChannelProviderProtocolConformanceTests
{
    static readonly ChannelDirectionId Outbound = new("outbound");
    static readonly ChannelDirectionId Request = new("request");
    static readonly ChannelDirectionId Reply = new("reply");
    static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Mqtt_OrdinaryAndSharedSubscriptionsHaveDifferentCanonicalDistributionSemantics()
    {
        var ordinary = MqttPublication(ChannelDistributionKind.FanOut, ChannelDeliveryGuaranteeKind.AtLeastOnce);
        var shared = MqttPublication(
            ChannelDistributionKind.CompetingConsumers,
            ChannelDeliveryGuaranteeKind.AtLeastOnce);

        AssertValid(ordinary);
        AssertValid(shared);
        Assert.Equal(ChannelDistributionKind.FanOut, Topology(ordinary).Distribution);
        Assert.Equal(ChannelDistributionKind.CompetingConsumers, Topology(shared).Distribution);

        var ordinaryProfile = NativeProfile("mqtt/ordinary", ordinary);
        Assert.True(Compile("mqtt/ordinary", ordinary, ordinaryProfile).Validation.IsValid);
        Assert.False(Compile("mqtt/shared-on-ordinary", shared, ordinaryProfile).Validation.IsValid);
        Assert.True(Compile("mqtt/shared", shared, NativeProfile("mqtt/shared", shared)).Validation.IsValid);
    }

    [Theory]
    [InlineData(0, ChannelDeliveryGuaranteeKind.AtMostOnce, ChannelReliabilityKind.Unreliable)]
    [InlineData(1, ChannelDeliveryGuaranteeKind.AtLeastOnce, ChannelReliabilityKind.Reliable)]
    [InlineData(2, ChannelDeliveryGuaranteeKind.ProtocolExactlyOnce, ChannelReliabilityKind.Reliable)]
    public void Mqtt_QosIsAProtocolDeliveryBoundary_NotApplicationEffectIdempotency(
        int qos,
        ChannelDeliveryGuaranteeKind expectedGuarantee,
        ChannelReliabilityKind expectedReliability)
    {
        var definition = MqttPublication(ChannelDistributionKind.FanOut, expectedGuarantee, expectedReliability);
        var profile = NativeProfile($"mqtt/qos-{qos}", definition);
        var plan = Compile($"mqtt/qos-{qos}", definition, profile);

        Assert.True(plan.Validation.IsValid, Format(plan.Validation));
        Assert.Equal(expectedGuarantee, definition.Requirements.OfType<ChannelDeliveryRequirement>().Single().Guarantee);
        Assert.Equal(expectedReliability, definition.Requirements.OfType<ChannelReliabilityRequirement>().Single().Reliability);
        Assert.Empty(definition.Requirements.OfType<ChannelAtomicityRequirement>());

        var applicationExactlyOnce = new ChannelDefinition(
            definition.Exchange,
            [
                .. definition.Requirements,
                new ChannelAtomicityRequirement(
                    id: new("atomic/application-effect"),
                    scope: ChannelRequirementScope.Exchange,
                    atomicScope: new("application-effect-and-publication"),
                    operations:
                    [
                        ChannelAtomicOperationKind.StateMutation,
                        ChannelAtomicOperationKind.Publication
                    ])
            ]);
        AssertValid(applicationExactlyOnce);
        Assert.False(Compile($"mqtt/qos-{qos}-application-effect", applicationExactlyOnce, profile).Validation.IsValid);
    }

    [Fact]
    public void Mqtt_SessionContinuityRetainedLatestAndResponseRoutingRemainIndependentRequirements()
    {
        var retainedSession = OneWay(
            interaction: ChannelInteractionShape.Publication,
            distribution: ChannelDistributionKind.FanOut,
            routing: ChannelRoutingKind.TopicOrFilter,
            isolation: ChannelRoutingIsolationKind.None,
            framing: ChannelFramingKind.TypedMessage,
            retention: ChannelRetentionKind.RetainedLatest,
            replay: ChannelReplayKind.None,
            guarantee: ChannelDeliveryGuaranteeKind.AtLeastOnce,
            ordering: ChannelOrderingScopeKind.None,
            reliability: ChannelReliabilityKind.Reliable,
            flow: new(
                id: new("flow/session"),
                scope: ChannelRequirementScope.Exchange,
                control: ChannelFlowControlKind.BoundedBuffer,
                completion: ChannelStreamCompletionKind.Terminal,
                continuity: ChannelSessionContinuityKind.Reconnect,
                maximumInFlight: 32));
        var response = RequestReply(
            interaction: ChannelInteractionShape.CorrelatedRequestReply,
            requestRouting: ChannelRoutingKind.TopicOrFilter,
            replyRouting: ChannelRoutingKind.ExplicitResponseTarget,
            replyIsolation: ChannelRoutingIsolationKind.DedicatedTarget);

        Assert.True(Compile("mqtt/retained-session", retainedSession, NativeProfile("mqtt/session", retainedSession))
            .Validation.IsValid);
        Assert.True(Compile("mqtt/response", response, NativeProfile("mqtt/response", response)).Validation.IsValid);
        Assert.Equal(
            ChannelRetentionKind.RetainedLatest,
            retainedSession.Requirements.OfType<ChannelPersistenceRequirement>().Single().Retention);
        Assert.Equal(
            ChannelSessionContinuityKind.Reconnect,
            retainedSession.Requirements.OfType<ChannelFlowRequirement>().Single().Continuity);
        var replyRouting = response.Requirements.OfType<ChannelRoutingRequirement>()
            .Single(requirement => requirement.Scope.Direction == Reply);
        Assert.Equal(ChannelRoutingKind.ExplicitResponseTarget, replyRouting.Routing);
        Assert.Equal(ChannelRoutingIsolationKind.DedicatedTarget, replyRouting.Isolation);
    }

    [Theory]
    [MemberData(nameof(ZeroMqPatterns))]
    public void ZeroMq_PatternsProjectToCanonicalTopologyAndRouting(
        string pattern,
        ChannelDefinition definition,
        ChannelInteractionShape expectedInteraction,
        ChannelDistributionKind expectedDistribution)
    {
        var plan = Compile($"zeromq/{pattern}", definition, NativeProfile($"zeromq/{pattern}", definition));

        Assert.True(plan.Validation.IsValid, Format(plan.Validation));
        Assert.Equal(expectedInteraction, Topology(definition).Interaction);
        Assert.Equal(expectedDistribution, Topology(definition).Distribution);
        Assert.All(
            definition.Requirements.OfType<ChannelPersistenceRequirement>(),
            static persistence => Assert.Equal(ChannelRetentionKind.ActivationLocal, persistence.Retention));
    }

    [Fact]
    public void ZeroMq_ActivationLocalTransportRejectsDurabilityUnlessAnExplicitCompositionSuppliesIt()
    {
        var transient = ZeroMqPushPull();
        var durable = ReplaceOneWayDelivery(
            transient,
            retention: ChannelRetentionKind.RetainedHistory,
            replay: ChannelReplayKind.OrderedPosition,
            guarantee: ChannelDeliveryGuaranteeKind.AtLeastOnce,
            reliability: ChannelReliabilityKind.Reliable,
            ordering: ChannelOrderingScopeKind.PartitionKeyOrSession,
            minimumRetention: TimeSpan.FromHours(1));

        AssertValid(durable);
        var rejected = Compile("zeromq/durable-native", durable, NativeProfile("zeromq/native", transient));
        var composed = Compile("zeromq/durable-composed", durable, ComposedProfile("zeromq/composed", durable));

        Assert.False(rejected.Validation.IsValid);
        Assert.True(composed.Validation.IsValid, Format(composed.Validation));
        Assert.Equal(
            CapabilityRealizationKind.Composed,
            Decision(composed, "persistence/outbound").Realization);
        Assert.Contains(
            Decision(composed, "persistence/outbound").SourceReferences,
            static reference => reference.Contains("composition", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public void UnaryInvocation_LeavesRetryDeadlineAndCancellationToCanonicalRequestSemantics(string protocol)
    {
        var channel = RequestReply(
            ChannelInteractionShape.UnaryInvocation,
            flow: new(
                id: new($"flow/{protocol}/unary"),
                scope: ChannelRequirementScope.Exchange,
                control: ChannelFlowControlKind.None,
                completion: ChannelStreamCompletionKind.Terminal,
                continuity: ChannelSessionContinuityKind.None,
                cancellation: ChannelCancellationKind.InvocationOrStream));
        var plan = Compile($"{protocol}/unary", channel, NativeProfile($"{protocol}/unary", channel));
        var fixture = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.StableIdentity,
            timeoutAfter: TimeSpan.FromMinutes(2),
            supportsCancellation: true);
        var state = fixture.CreateState($"emission/{protocol}/1");

        Assert.True(plan.Validation.IsValid, Format(plan.Validation));
        Assert.All(
            channel.Requirements.OfType<ChannelDeliveryRequirement>(),
            static delivery => Assert.Equal(ChannelDeliveryGuaranteeKind.InvocationAttempt, delivery.Guarantee));
        Assert.Equal(RequestRetrySemantics.StableIdentity, fixture.Response.Retry);
        Assert.Equal(RequestOptionalTerminalSemantics.TerminalOutcome, fixture.Response.Timeout);
        Assert.Equal(RequestOptionalTerminalSemantics.TerminalOutcome, fixture.Response.Cancellation);
        Assert.Equal(
            ChannelCancellationKind.InvocationOrStream,
            channel.Requirements.OfType<ChannelFlowRequirement>().Single().Cancellation);
        Assert.False(string.IsNullOrWhiteSpace(state.Request.Context.IdempotencyKey.Value));
        Assert.Equal(state.Request.Context.EmissionId.Value, state.OperationId.Value);
    }

    [Fact]
    public void UnaryInvocation_InFlightTimeoutAndCancellationPreserveAmbiguousCompletionAndLateReplySemantics()
    {
        var timeoutFixture = DurableOperationTestFixture.Create(
            timeoutAfter: TimeSpan.FromMinutes(2),
            supportsCancellation: true);
        var claimed = timeoutFixture.Executor.Claim(
            timeoutFixture.CreateState("emission/http/timeout"),
            new("attempt/http/timeout"),
            claimant: "worker/http",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = timeoutFixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var timedOut = timeoutFixture.Executor.ResolveTimeout(
            dispatched.State,
            timeoutFixture.Timeout(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var late = timeoutFixture.Executor.RecordObservation(
            timedOut.State,
            claim.AttemptId,
            claim.Fence,
            timeoutFixture.Success("late"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));

        var cancellationFixture = DurableOperationTestFixture.Create(supportsCancellation: true);
        var cancelled = cancellationFixture.Executor.ResolveCancellation(
            cancellationFixture.CreateState("emission/grpc/cancel"),
            cancellationFixture.Cancellation(),
            DurableOperationTestFixture.CreatedAtUtc);

        Assert.Equal(DurableOperationEffectEvidence.Ambiguous, timedOut.State.CurrentAttempt?.Failure?.EffectEvidence);
        Assert.Equal(DurableOperationObservationDisposition.LateResult, late.Disposition);
        Assert.IsType<RequestTimeoutOutcome>(late.State.Acknowledgement?.Outcome);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, cancelled.Disposition);
        Assert.IsType<RequestCancellationOutcome>(cancelled.State.Acknowledgement?.Outcome);
    }

    [Fact]
    public void GrpcStreamsWebSocketAndSseRequireDistinctFlowCompletionAndReplayProofs()
    {
        var grpc = RequestReply(
            interaction: ChannelInteractionShape.ResponseStream,
            flow: new(
                id: new("flow/grpc"),
                scope: ChannelRequirementScope.Exchange,
                control: ChannelFlowControlKind.Demand,
                completion: ChannelStreamCompletionKind.HalfClose,
                continuity: ChannelSessionContinuityKind.Reconnect,
                maximumInFlight: 64,
                cancellation: ChannelCancellationKind.InvocationOrStream));
        var webSocket = RequestReply(
            interaction: ChannelInteractionShape.BidirectionalStream,
            framing: ChannelFramingKind.FramedMessage,
            flow: new(
                id: new("flow/websocket"),
                scope: ChannelRequirementScope.Exchange,
                control: ChannelFlowControlKind.BoundedBuffer,
                completion: ChannelStreamCompletionKind.IndependentDirections,
                continuity: ChannelSessionContinuityKind.Reconnect,
                maximumInFlight: 128,
                cancellation: ChannelCancellationKind.Session));
        var sse = OneWay(
            interaction: ChannelInteractionShape.Publication,
            distribution: ChannelDistributionKind.FanOut,
            routing: ChannelRoutingKind.ExplicitResponseTarget,
            isolation: ChannelRoutingIsolationKind.DedicatedTarget,
            framing: ChannelFramingKind.FramedMessage,
            retention: ChannelRetentionKind.RetainedHistory,
            replay: ChannelReplayKind.OrderedPosition,
            guarantee: ChannelDeliveryGuaranteeKind.AtLeastOnce,
            ordering: ChannelOrderingScopeKind.Channel,
            reliability: ChannelReliabilityKind.Reliable,
            minimumRetention: TimeSpan.FromMinutes(15));
        var transientSse = ReplaceOneWayDelivery(
            sse,
            retention: ChannelRetentionKind.ActivationLocal,
            replay: ChannelReplayKind.None,
            guarantee: ChannelDeliveryGuaranteeKind.InvocationAttempt,
            reliability: ChannelReliabilityKind.Reliable);

        Assert.True(Compile("grpc/response-stream", grpc, NativeProfile("grpc/stream", grpc)).Validation.IsValid);
        Assert.True(Compile("websocket/bidirectional", webSocket, NativeProfile("websocket", webSocket)).Validation.IsValid);
        Assert.True(Compile("sse/replay", sse, NativeProfile("sse/replay", sse)).Validation.IsValid);
        Assert.False(Compile("sse/replay-on-transient", sse, NativeProfile("sse/transient", transientSse)).Validation.IsValid);

        ChannelReplayCursor cursor = new(
            formatVersion: 1,
            scope: new("sse/orders"),
            orderingDomain: new("event-stream/orders"),
            value: "event-id/42");
        ChannelDurableProgressEvidence progress = new(replayCursor: cursor);
        Assert.Equal("event-id/42", progress.ReplayCursor?.Value);
        Assert.Equal(ChannelFlowControlKind.Demand, grpc.Requirements.OfType<ChannelFlowRequirement>().Single().Control);
        Assert.Equal(
            ChannelCancellationKind.InvocationOrStream,
            grpc.Requirements.OfType<ChannelFlowRequirement>().Single().Cancellation);
        Assert.Equal(
            ChannelStreamCompletionKind.IndependentDirections,
            webSocket.Requirements.OfType<ChannelFlowRequirement>().Single().Completion);
        Assert.Equal(
            ChannelCancellationKind.Session,
            webSocket.Requirements.OfType<ChannelFlowRequirement>().Single().Cancellation);
    }

    [Theory]
    [MemberData(nameof(RealtimeTransportCases))]
    public void WebTransportAndWebRtc_PreserveFramingReliabilityOrderingAndLimits(
        string caseName,
        ChannelDefinition definition,
        ChannelFramingKind expectedFraming,
        ChannelReliabilityKind expectedReliability,
        ChannelOrderingScopeKind expectedOrdering,
        ChannelLimitKind expectedLimit)
    {
        var plan = Compile(caseName, definition, NativeProfile(caseName, definition));

        Assert.True(plan.Validation.IsValid, Format(plan.Validation));
        Assert.All(definition.Requirements.OfType<ChannelFramingRequirement>(), framing =>
            Assert.Equal(expectedFraming, framing.Framing));
        Assert.All(definition.Requirements.OfType<ChannelReliabilityRequirement>(), reliability =>
            Assert.Equal(expectedReliability, reliability.Reliability));
        Assert.All(definition.Requirements.OfType<ChannelDeliveryRequirement>(), delivery =>
            Assert.Equal(expectedOrdering, delivery.Ordering));
        Assert.All(definition.Requirements.OfType<ChannelLimitRequirement>(), limit =>
            Assert.Equal(expectedLimit, limit.Kind));
    }

    [Fact]
    public void WebTransportAndWebRtc_ActivationLocalBindingsRejectDurabilityWithoutComposition()
    {
        var transient = WebTransportDatagram();
        var durable = ReplaceOneWayDelivery(
            transient,
            retention: ChannelRetentionKind.DurableUntilSettled,
            replay: ChannelReplayKind.None,
            guarantee: ChannelDeliveryGuaranteeKind.AtLeastOnce,
            reliability: ChannelReliabilityKind.Reliable);

        Assert.False(Compile("webtransport/durable-native", durable, NativeProfile("webtransport/native", transient))
            .Validation.IsValid);
        Assert.True(Compile("webtransport/durable-composed", durable, ComposedProfile("webtransport/composed", durable))
            .Validation.IsValid);
    }

    [Theory]
    [MemberData(nameof(RSocketInteractionCases))]
    public void RSocket_FourInteractionShapesUseCanonicalTopologyFlowAndCompletion(
        string interactionName,
        ChannelDefinition definition,
        ChannelInteractionShape expectedShape)
    {
        var plan = Compile($"rsocket/{interactionName}", definition, NativeProfile($"rsocket/{interactionName}", definition));

        Assert.True(plan.Validation.IsValid, Format(plan.Validation));
        Assert.Equal(expectedShape, Topology(definition).Interaction);
        var flow = Assert.Single(definition.Requirements.OfType<ChannelFlowRequirement>());
        var lease = Assert.IsType<ChannelInitiationLease>(flow.InitiationLease);
        Assert.Equal(16, lease.MinimumInitiations);
        Assert.Equal(TimeSpan.FromSeconds(30), lease.MinimumValidity);
        if (expectedShape is ChannelInteractionShape.ResponseStream or ChannelInteractionShape.BidirectionalStream)
        {
            Assert.Equal(ChannelFlowControlKind.Demand, flow.Control);
            Assert.True(flow.Cancellation is ChannelCancellationKind.InvocationOrStream or ChannelCancellationKind.Direction);
        }
    }

    [Fact]
    public void RSocket_DemandCancellationHalfCloseLeaseAndResumeAreBoundedSessionSemantics()
    {
        ReactiveSessionReference session = new(
            leaseAllowance: 1,
            resumeWindow: TimeSpan.FromSeconds(30),
            connectedAtUtc: Now);
        session.Request(elements: 2);

        Assert.True(session.TryDeliver("frame/1"));
        Assert.False(session.TryDeliver("frame/2"));
        session.RenewLease(allowance: 2);
        Assert.True(session.TryDeliver("frame/2"));
        Assert.False(session.TryDeliver("frame/3"));

        session.HalfCloseOutbound();
        Assert.True(session.OutboundClosed);
        Assert.False(session.InboundClosed);
        session.Disconnect(Now.AddSeconds(10));
        Assert.True(session.TryResume(Now.AddSeconds(35)));
        session.Disconnect(Now.AddSeconds(40));
        Assert.False(session.TryResume(Now.AddSeconds(71)));

        session.Cancel();
        Assert.True(session.Cancelled);
        Assert.False(session.TryDeliver("frame/after-cancel"));
    }

    [Fact]
    public void RSocket_TerminalErrorRemainsACanonicalTypedReplyOutcome()
    {
        var definition = RSocketRequestStream();
        var document = ChannelDefinitionDocuments.Create(
            definitionId: new("channel/rsocket/terminal-error"),
            revisionId: new("1"),
            definition: definition,
            provenance: Provenance("definition"));
        ExecutionDefinitionReference channel = new(
            document.Metadata.DefinitionId,
            document.Metadata.RevisionId,
            document.Metadata.Fingerprint);
        var interactions = DurableOperationTestFixture.Create();
        var binding = new ChannelRequestReplyBinding(
            kind: ChannelRequestReplyBindingKind.CoupledExchange,
            request: interactions.RequestContract,
            reply: interactions.FailureReplyContract,
            requestDirection: new(channel, Request),
            replyDirection: new(channel, Reply));

        var validation = ChannelInteractionBindingValidator.Validate(
            binding,
            interactions.Catalog,
            [document]);

        Assert.True(validation.IsValid, Format(validation));
        var failureDefinition = Assert.Single(
            interactions.Response.TerminalOutcomes.OfType<RequestFailureDefinition>());
        Assert.True(interactions.Catalog.TryResolve(
            interactions.FailureReplyContract,
            out var resolvedReplyDefinition));
        var replyDefinition = Assert.IsType<ReplyContractDefinition>(resolvedReplyDefinition);
        Assert.Equal(failureDefinition.Id, replyDefinition.Outcome);
        RequestTerminalOutcome terminalError = new RequestFailureOutcome(
            failureDefinition.Id,
            DurableOperationTestFixture.StringValue("rsocket/application-error"));
        Assert.IsType<RequestFailureOutcome>(terminalError);

        // Flow owns transport lifecycle (demand, half-close, cancellation, and resume). The Request-owned Reply
        // outcome remains the single semantic authority for a typed application terminal error.
        var flow = Assert.Single(definition.Requirements.OfType<ChannelFlowRequirement>());
        Assert.Equal(ChannelStreamCompletionKind.HalfClose, flow.Completion);
        Assert.Equal(ChannelCancellationKind.InvocationOrStream, flow.Cancellation);
    }

    [Fact]
    public void RSocket_BoundedResumeDoesNotClaimDurableRetainedReplay()
    {
        var boundedSession = RSocketRequestChannel(ChannelRetentionKind.ActivationLocal, ChannelReplayKind.None);
        var durableReplay = RSocketRequestChannel(
            ChannelRetentionKind.RetainedHistory,
            ChannelReplayKind.OrderedPosition,
            minimumRetention: TimeSpan.FromMinutes(30));

        Assert.Equal(
            ChannelSessionContinuityKind.BoundedResume,
            boundedSession.Requirements.OfType<ChannelFlowRequirement>().Single().Continuity);
        Assert.False(Compile("rsocket/durable-on-resume", durableReplay, NativeProfile("rsocket/resume", boundedSession))
            .Validation.IsValid);
        var composed = Compile("rsocket/durable-composed", durableReplay, ComposedProfile("rsocket/composed", durableReplay));
        Assert.True(composed.Validation.IsValid, Format(composed.Validation));
        Assert.Equal(CapabilityRealizationKind.Composed, Decision(composed, "persistence/request").Realization);
    }

    public static IEnumerable<object[]> ZeroMqPatterns()
    {
        yield return [
            "pub-sub",
            ZeroMqPubSub(),
            ChannelInteractionShape.Publication,
            ChannelDistributionKind.FanOut
        ];
        yield return [
            "push-pull",
            ZeroMqPushPull(),
            ChannelInteractionShape.FireAndForget,
            ChannelDistributionKind.CompetingConsumers
        ];
        yield return [
            "req-rep",
            RequestReply(ChannelInteractionShape.UnaryInvocation),
            ChannelInteractionShape.UnaryInvocation,
            ChannelDistributionKind.PointToPoint
        ];
        yield return [
            "dealer-router",
            RequestReply(
                ChannelInteractionShape.CorrelatedRequestReply,
                requestRouting: ChannelRoutingKind.KeyOrSessionAffinity,
                replyRouting: ChannelRoutingKind.ExplicitResponseTarget,
                replyIsolation: ChannelRoutingIsolationKind.SelectiveAcquisition),
            ChannelInteractionShape.CorrelatedRequestReply,
            ChannelDistributionKind.PointToPoint
        ];
    }

    public static IEnumerable<object[]> RealtimeTransportCases()
    {
        yield return [
            "webtransport/reliable-stream",
            WebTransportStream(),
            ChannelFramingKind.ByteStream,
            ChannelReliabilityKind.Reliable,
            ChannelOrderingScopeKind.Connection,
            ChannelLimitKind.FrameBytes
        ];
        yield return [
            "webtransport/partial-datagram",
            WebTransportDatagram(),
            ChannelFramingKind.Datagram,
            ChannelReliabilityKind.PartiallyReliable,
            ChannelOrderingScopeKind.None,
            ChannelLimitKind.DatagramBytes
        ];
        yield return [
            "webrtc/reliable-data-channel",
            WebRtcDataChannel(reliable: true),
            ChannelFramingKind.FramedMessage,
            ChannelReliabilityKind.Reliable,
            ChannelOrderingScopeKind.Connection,
            ChannelLimitKind.PayloadBytes
        ];
        yield return [
            "webrtc/partial-data-channel",
            WebRtcDataChannel(reliable: false),
            ChannelFramingKind.FramedMessage,
            ChannelReliabilityKind.PartiallyReliable,
            ChannelOrderingScopeKind.None,
            ChannelLimitKind.PayloadBytes
        ];
    }

    public static IEnumerable<object[]> RSocketInteractionCases()
    {
        yield return ["fire-and-forget", RSocketFireAndForget(), ChannelInteractionShape.FireAndForget];
        yield return ["request-response", RSocketRequestResponse(), ChannelInteractionShape.UnaryInvocation];
        yield return ["request-stream", RSocketRequestStream(), ChannelInteractionShape.ResponseStream];
        yield return [
            "request-channel",
            RSocketRequestChannel(ChannelRetentionKind.ActivationLocal, ChannelReplayKind.None),
            ChannelInteractionShape.BidirectionalStream
        ];
    }

    static ChannelDefinition MqttPublication(
        ChannelDistributionKind distribution,
        ChannelDeliveryGuaranteeKind guarantee,
        ChannelReliabilityKind? reliability = null) =>
        OneWay(
            interaction: ChannelInteractionShape.Publication,
            distribution: distribution,
            routing: ChannelRoutingKind.TopicOrFilter,
            isolation: distribution == ChannelDistributionKind.CompetingConsumers
                ? ChannelRoutingIsolationKind.SelectiveAcquisition
                : ChannelRoutingIsolationKind.None,
            framing: ChannelFramingKind.TypedMessage,
            retention: ChannelRetentionKind.ActivationLocal,
            replay: ChannelReplayKind.None,
            guarantee: guarantee,
            ordering: ChannelOrderingScopeKind.PartitionKeyOrSession,
            reliability: reliability ?? (guarantee == ChannelDeliveryGuaranteeKind.AtMostOnce
                ? ChannelReliabilityKind.Unreliable
                : ChannelReliabilityKind.Reliable));

    static ChannelDefinition ZeroMqPubSub() => OneWay(
        interaction: ChannelInteractionShape.Publication,
        distribution: ChannelDistributionKind.FanOut,
        routing: ChannelRoutingKind.TopicOrFilter,
        isolation: ChannelRoutingIsolationKind.None,
        framing: ChannelFramingKind.FramedMessage,
        retention: ChannelRetentionKind.ActivationLocal,
        replay: ChannelReplayKind.None,
        guarantee: ChannelDeliveryGuaranteeKind.AtMostOnce,
        ordering: ChannelOrderingScopeKind.None,
        reliability: ChannelReliabilityKind.Unreliable);

    static ChannelDefinition ZeroMqPushPull() => OneWay(
        interaction: ChannelInteractionShape.FireAndForget,
        distribution: ChannelDistributionKind.CompetingConsumers,
        routing: ChannelRoutingKind.OperationEndpoint,
        isolation: ChannelRoutingIsolationKind.SelectiveAcquisition,
        framing: ChannelFramingKind.FramedMessage,
        retention: ChannelRetentionKind.ActivationLocal,
        replay: ChannelReplayKind.None,
        guarantee: ChannelDeliveryGuaranteeKind.AtMostOnce,
        ordering: ChannelOrderingScopeKind.None,
        reliability: ChannelReliabilityKind.Unreliable);

    static ChannelDefinition WebTransportStream()
    {
        var definition = RequestReply(
            interaction: ChannelInteractionShape.BidirectionalStream,
            framing: ChannelFramingKind.ByteStream,
            boundaries: ChannelBoundarySemantics.CodecReconstructed,
            codec: "length-prefixed/v1",
            ordering: ChannelOrderingScopeKind.Connection,
            flow: new(
                id: new("flow/webtransport"),
                scope: ChannelRequirementScope.Exchange,
                control: ChannelFlowControlKind.Credit,
                completion: ChannelStreamCompletionKind.IndependentDirections,
                continuity: ChannelSessionContinuityKind.Reconnect,
                maximumInFlight: 64));
        return AddLimit(definition, ChannelLimitKind.FrameBytes, 65_536);
    }

    static ChannelDefinition WebTransportDatagram() => AddLimit(
        OneWay(
            interaction: ChannelInteractionShape.Datagram,
            distribution: ChannelDistributionKind.PointToPoint,
            routing: ChannelRoutingKind.ConnectionOrStream,
            isolation: ChannelRoutingIsolationKind.None,
            framing: ChannelFramingKind.Datagram,
            retention: ChannelRetentionKind.ActivationLocal,
            replay: ChannelReplayKind.None,
            guarantee: ChannelDeliveryGuaranteeKind.AtMostOnce,
            ordering: ChannelOrderingScopeKind.None,
            reliability: ChannelReliabilityKind.PartiallyReliable,
            maximumLifetime: TimeSpan.FromSeconds(2),
            maximumRetransmissions: 3),
        ChannelLimitKind.DatagramBytes,
        1_200);

    static ChannelDefinition WebRtcDataChannel(bool reliable) => AddLimit(
        OneWay(
            interaction: ChannelInteractionShape.FireAndForget,
            distribution: ChannelDistributionKind.PointToPoint,
            routing: ChannelRoutingKind.ConnectionOrStream,
            isolation: ChannelRoutingIsolationKind.None,
            framing: ChannelFramingKind.FramedMessage,
            retention: ChannelRetentionKind.ActivationLocal,
            replay: ChannelReplayKind.None,
            guarantee: ChannelDeliveryGuaranteeKind.AtMostOnce,
            ordering: reliable ? ChannelOrderingScopeKind.Connection : ChannelOrderingScopeKind.None,
            reliability: reliable ? ChannelReliabilityKind.Reliable : ChannelReliabilityKind.PartiallyReliable,
            maximumLifetime: reliable ? null : TimeSpan.FromSeconds(1),
            maximumRetransmissions: reliable ? null : 2),
        ChannelLimitKind.PayloadBytes,
        16_384);

    static ChannelDefinition RSocketFireAndForget() => OneWay(
        interaction: ChannelInteractionShape.FireAndForget,
        distribution: ChannelDistributionKind.PointToPoint,
        routing: ChannelRoutingKind.OperationEndpoint,
        isolation: ChannelRoutingIsolationKind.InvocationScoped,
        framing: ChannelFramingKind.TypedMessage,
        retention: ChannelRetentionKind.ActivationLocal,
        replay: ChannelReplayKind.None,
        guarantee: ChannelDeliveryGuaranteeKind.InvocationAttempt,
        ordering: ChannelOrderingScopeKind.Connection,
        reliability: ChannelReliabilityKind.Reliable,
        flow: new(
            id: new("flow/rsocket-fire-and-forget"),
            scope: ChannelRequirementScope.Exchange,
                control: ChannelFlowControlKind.None,
                completion: ChannelStreamCompletionKind.Terminal,
                continuity: ChannelSessionContinuityKind.BoundedResume,
                resumeWindow: TimeSpan.FromMinutes(5),
                initiationLease: RSocketLease()));

    static ChannelDefinition RSocketRequestResponse() => RequestReply(
        interaction: ChannelInteractionShape.UnaryInvocation,
        flow: new(
            id: new("flow/rsocket-request-response"),
            scope: ChannelRequirementScope.Exchange,
            control: ChannelFlowControlKind.None,
            completion: ChannelStreamCompletionKind.Terminal,
            continuity: ChannelSessionContinuityKind.BoundedResume,
            resumeWindow: TimeSpan.FromMinutes(5),
            cancellation: ChannelCancellationKind.InvocationOrStream,
            initiationLease: RSocketLease()));

    static ChannelDefinition RSocketRequestStream() => RequestReply(
        interaction: ChannelInteractionShape.ResponseStream,
        flow: new(
            id: new("flow/rsocket-request-stream"),
            scope: ChannelRequirementScope.Exchange,
            control: ChannelFlowControlKind.Demand,
            completion: ChannelStreamCompletionKind.HalfClose,
            continuity: ChannelSessionContinuityKind.BoundedResume,
            maximumInFlight: 32,
            resumeWindow: TimeSpan.FromMinutes(5),
            cancellation: ChannelCancellationKind.InvocationOrStream,
            initiationLease: RSocketLease()));

    static ChannelDefinition RSocketRequestChannel(
        ChannelRetentionKind retention,
        ChannelReplayKind replay,
        TimeSpan? minimumRetention = null) =>
        RequestReply(
            interaction: ChannelInteractionShape.BidirectionalStream,
            retention: retention,
            replay: replay,
            guarantee: retention == ChannelRetentionKind.ActivationLocal
                ? ChannelDeliveryGuaranteeKind.InvocationAttempt
                : ChannelDeliveryGuaranteeKind.AtLeastOnce,
            ordering: ChannelOrderingScopeKind.RpcStream,
            minimumRetention: minimumRetention,
            flow: new(
                id: new("flow/rsocket-request-channel"),
                scope: ChannelRequirementScope.Exchange,
                control: ChannelFlowControlKind.Demand,
                completion: ChannelStreamCompletionKind.IndependentDirections,
                continuity: ChannelSessionContinuityKind.BoundedResume,
                maximumInFlight: 32,
                resumeWindow: TimeSpan.FromMinutes(5),
                cancellation: ChannelCancellationKind.Direction,
                initiationLease: RSocketLease()));

    static ChannelInitiationLease RSocketLease() => new(
        minimumInitiations: 16,
        minimumValidity: TimeSpan.FromSeconds(30));

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
        ChannelReliabilityKind reliability,
        ChannelFlowRequirement? flow = null,
        TimeSpan? minimumRetention = null,
        TimeSpan? maximumLifetime = null,
        int? maximumRetransmissions = null)
    {
        var scope = ChannelRequirementScope.ForDirection(Outbound);
        List<ChannelRequirement> requirements =
        [
            new ChannelTopologyRequirement(
                id: new("topology"),
                scope: ChannelRequirementScope.Exchange,
                distribution: distribution,
                interaction: interaction),
            new ChannelRoutingRequirement(id: new("routing/outbound"), scope: scope, routing: routing, isolation: isolation),
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
            new ChannelReliabilityRequirement(
                id: new("reliability/outbound"),
                scope: scope,
                reliability: reliability,
                maximumLifetime: maximumLifetime,
                maximumRetransmissions: maximumRetransmissions)
        ];
        if (flow is not null)
            requirements.Add(flow);
        return new(new OneWayChannelExchange(Outbound), [.. requirements]);
    }

    static ChannelDefinition RequestReply(
        ChannelInteractionShape interaction,
        ChannelRoutingKind requestRouting = ChannelRoutingKind.OperationEndpoint,
        ChannelRoutingKind replyRouting = ChannelRoutingKind.ConnectionOrStream,
        ChannelRoutingIsolationKind replyIsolation = ChannelRoutingIsolationKind.InvocationScoped,
        ChannelFramingKind framing = ChannelFramingKind.TypedMessage,
        ChannelBoundarySemantics boundaries = ChannelBoundarySemantics.Preserved,
        string? codec = null,
        ChannelRetentionKind retention = ChannelRetentionKind.ActivationLocal,
        ChannelReplayKind replay = ChannelReplayKind.None,
        ChannelDeliveryGuaranteeKind guarantee = ChannelDeliveryGuaranteeKind.InvocationAttempt,
        ChannelOrderingScopeKind ordering = ChannelOrderingScopeKind.Connection,
        TimeSpan? minimumRetention = null,
        ChannelFlowRequirement? flow = null)
    {
        List<ChannelRequirement> requirements =
        [
            new ChannelTopologyRequirement(
                id: new("topology"),
                scope: ChannelRequirementScope.Exchange,
                distribution: ChannelDistributionKind.PointToPoint,
                interaction: interaction)
        ];
        AddDirection(Request, "request", requestRouting, ChannelRoutingIsolationKind.InvocationScoped);
        AddDirection(Reply, "reply", replyRouting, replyIsolation);
        if (flow is not null)
            requirements.Add(flow);
        return new(new RequestReplyChannelExchange(Request, Reply), [.. requirements]);

        void AddDirection(
            ChannelDirectionId direction,
            string suffix,
            ChannelRoutingKind routing,
            ChannelRoutingIsolationKind isolation)
        {
            var scope = ChannelRequirementScope.ForDirection(direction);
            requirements.Add(new ChannelRoutingRequirement(new($"routing/{suffix}"), scope, routing, isolation));
            requirements.Add(new ChannelFramingRequirement(new($"framing/{suffix}"), scope, framing, boundaries, codec));
            requirements.Add(new ChannelPersistenceRequirement(
                new($"persistence/{suffix}"),
                scope,
                retention,
                replay,
                minimumRetention));
            requirements.Add(new ChannelDeliveryRequirement(
                new($"delivery/{suffix}"),
                scope,
                guarantee,
                ordering));
            requirements.Add(new ChannelReliabilityRequirement(
                new($"reliability/{suffix}"),
                scope,
                ChannelReliabilityKind.Reliable));
        }
    }

    static ChannelDefinition ReplaceOneWayDelivery(
        ChannelDefinition source,
        ChannelRetentionKind retention,
        ChannelReplayKind replay,
        ChannelDeliveryGuaranteeKind guarantee,
        ChannelReliabilityKind reliability,
        ChannelOrderingScopeKind? ordering = null,
        TimeSpan? minimumRetention = null)
    {
        List<ChannelRequirement> requirements = [];
        foreach (var requirement in source.Requirements)
        {
            requirements.Add(requirement switch
            {
                ChannelPersistenceRequirement persistence => new ChannelPersistenceRequirement(
                    persistence.Id,
                    persistence.Scope,
                    retention,
                    replay,
                    minimumRetention),
                ChannelDeliveryRequirement delivery => new ChannelDeliveryRequirement(
                    delivery.Id,
                    delivery.Scope,
                    guarantee,
                    ordering ?? delivery.Ordering,
                    (ordering ?? delivery.Ordering) == ChannelOrderingScopeKind.Named
                        ? delivery.NamedOrderingScope
                        : null),
                ChannelReliabilityRequirement channelReliability => new ChannelReliabilityRequirement(
                    channelReliability.Id,
                    channelReliability.Scope,
                    reliability),
                _ => requirement
            });
        }
        return new(source.Exchange, [.. requirements]);
    }

    static ChannelDefinition AddLimit(ChannelDefinition definition, ChannelLimitKind kind, long value)
    {
        var directions = definition.Exchange.GetDirections();
        List<ChannelRequirement> requirements = [.. definition.Requirements];
        foreach (var direction in directions)
        {
            requirements.Add(new ChannelLimitRequirement(
                id: new($"limit/{direction.Value}/{kind}"),
                scope: ChannelRequirementScope.ForDirection(direction),
                kind: kind,
                value: value));
        }
        return new(definition.Exchange, [.. requirements]);
    }

    static ChannelCapabilityProfile NativeProfile(string id, ChannelDefinition definition) =>
        Profile(
            id,
            [
                .. definition.Requirements.Select(requirement => new ChannelCapabilityEvidence(
                    id: EvidenceId(requirement),
                    capability: requirement,
                    realization: CapabilityRealizationKind.Native,
                    sourceReferences: [$"tests://provider/{id}/{requirement.Id.Value}"]))
            ]);

    static ChannelCapabilityProfile ComposedProfile(string id, ChannelDefinition definition)
    {
        ChannelCapabilityEvidence journalOutbox = new(
            id: new("evidence/composition/journal-outbox"),
            capability: new ChannelAtomicityRequirement(
                id: new("composition/journal-outbox"),
                scope: ChannelRequirementScope.Exchange,
                atomicScope: new("composition/journal-outbox"),
                operations:
                [
                    ChannelAtomicOperationKind.StateMutation,
                    ChannelAtomicOperationKind.Publication
                ]),
            realization: CapabilityRealizationKind.Native,
            sourceReferences: [$"tests://provider/{id}/composition/journal-outbox"]);
        ChannelCapabilityEvidence inboxCheckpoint = new(
            id: new("evidence/composition/inbox-checkpoint"),
            capability: new ChannelAtomicityRequirement(
                id: new("composition/inbox-checkpoint"),
                scope: ChannelRequirementScope.Exchange,
                atomicScope: new("composition/inbox-checkpoint"),
                operations:
                [
                    ChannelAtomicOperationKind.Consumption,
                    ChannelAtomicOperationKind.ApplicationCheckpoint
                ]),
            realization: CapabilityRealizationKind.Native,
            sourceReferences: [$"tests://provider/{id}/composition/inbox-checkpoint"]);
        var evidence = ImmutableArray.CreateBuilder<ChannelCapabilityEvidence>(definition.Requirements.Length + 2);
        evidence.Add(journalOutbox);
        evidence.Add(inboxCheckpoint);
        foreach (var requirement in definition.Requirements)
        {
            var requiresDurableComposition = requirement is ChannelPersistenceRequirement
                or ChannelProgressRequirement
                or ChannelDeliveryRequirement
                or ChannelReliabilityRequirement
                or ChannelSettlementRequirement;
            evidence.Add(new(
                id: EvidenceId(requirement),
                capability: requirement,
                realization: requiresDurableComposition
                    ? CapabilityRealizationKind.Composed
                    : CapabilityRealizationKind.Native,
                auxiliaries: requiresDurableComposition ? [journalOutbox.Id, inboxCheckpoint.Id] : [],
                sourceReferences: requiresDurableComposition
                    ? [$"tests://provider/{id}/composition/journal-relay-inbox/{requirement.Id.Value}"]
                    : [$"tests://provider/{id}/native/{requirement.Id.Value}"]));
        }
        return Profile(id, evidence.MoveToImmutable());
    }

    static ChannelCapabilityProfile Profile(string id, ImmutableArray<ChannelCapabilityEvidence> evidence) => new(
        id: new($"profile/{id}"),
        subject: $"tests/provider/{id}",
        variants: [new ChannelCapabilityVariant(new("default"), evidence)],
        provenance: Provenance("profile"));

    static ChannelCapabilityEvidenceId EvidenceId(ChannelRequirement requirement) =>
        new($"evidence/{requirement.Id.Value}");

    static ChannelRealizationPlan Compile(
        string id,
        ChannelDefinition definition,
        ChannelCapabilityProfile profile)
    {
        AssertValid(definition);
        var document = ChannelDefinitionDocuments.Create(
            definitionId: new($"channel/{id}"),
            revisionId: new("1"),
            definition: definition,
            provenance: Provenance("definition"));
        return ChannelRealizationCompiler.Compile(document, profile, Provenance("compiler"));
    }

    static void AssertValid(ChannelDefinition definition)
    {
        var validation = ChannelDefinitionValidator.Validate(definition);
        Assert.True(validation.IsValid, Format(validation));
    }

    static ChannelTopologyRequirement Topology(ChannelDefinition definition) =>
        Assert.Single(definition.Requirements.OfType<ChannelTopologyRequirement>());

    static ChannelRealizationDecision Decision(ChannelRealizationPlan plan, string requirementId) =>
        Assert.Single(plan.Decisions, decision => decision.Requirement == new ChannelRequirementId(requirementId));

    static ExecutionProvenance Provenance(string stage) => new(
        producer: new($"tests/channel-provider-{stage}", "1"),
        source: new($"tests://execution-kernel/channel-provider/{stage}"),
        origin: DocumentOrigin.Generated);

    static string Format(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed class ReactiveSessionReference
    {
        readonly TimeSpan resumeWindow;
        int demand;
        int leaseAllowance;
        DateTimeOffset? disconnectedAtUtc;

        public ReactiveSessionReference(int leaseAllowance, TimeSpan resumeWindow, DateTimeOffset connectedAtUtc)
        {
            if (leaseAllowance < 0)
                throw new ArgumentOutOfRangeException(nameof(leaseAllowance));
            if (resumeWindow <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(resumeWindow));
            this.leaseAllowance = leaseAllowance;
            this.resumeWindow = resumeWindow;
            ConnectedAtUtc = connectedAtUtc;
        }

        public DateTimeOffset ConnectedAtUtc { get; private set; }
        public bool OutboundClosed { get; private set; }
        public bool InboundClosed { get; private set; }
        public bool Cancelled { get; private set; }
        public ImmutableArray<string> Delivered { get; private set; } = [];

        public void Request(int elements)
        {
            if (elements <= 0)
                throw new ArgumentOutOfRangeException(nameof(elements));
            demand = checked(demand + elements);
        }

        public void RenewLease(int allowance)
        {
            if (allowance < 0)
                throw new ArgumentOutOfRangeException(nameof(allowance));
            leaseAllowance = allowance;
        }

        public bool TryDeliver(string frame)
        {
            if (Cancelled || disconnectedAtUtc is not null || demand == 0 || leaseAllowance == 0)
                return false;
            demand--;
            leaseAllowance--;
            Delivered = [.. Delivered, frame];
            return true;
        }

        public void HalfCloseOutbound() => OutboundClosed = true;

        public void Disconnect(DateTimeOffset disconnectedAtUtc) => this.disconnectedAtUtc = disconnectedAtUtc;

        public bool TryResume(DateTimeOffset resumedAtUtc)
        {
            if (disconnectedAtUtc is not { } disconnected
                || resumedAtUtc < disconnected
                || resumedAtUtc - disconnected > resumeWindow)
            {
                return false;
            }
            ConnectedAtUtc = resumedAtUtc;
            disconnectedAtUtc = null;
            return true;
        }

        public void Cancel()
        {
            Cancelled = true;
            InboundClosed = true;
            OutboundClosed = true;
        }
    }
}
