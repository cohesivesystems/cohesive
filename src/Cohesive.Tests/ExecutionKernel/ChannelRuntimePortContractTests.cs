using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ChannelRuntimePortContractTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly ChannelScopeId Scope = new("channel/orders");

    [Fact]
    public void Payload_RequiresCanonicalLogicalIdentityAndNonNullGenericValue()
    {
        Assert.Throws<ArgumentException>(() => new ChannelPayload<SamplePayload>(default, new("order/42")));
        Assert.Throws<ArgumentNullException>(() =>
            new ChannelPayload<SamplePayload>(new("emission/42"), value: null!));

        ChannelPayload<int> scalar = new(new("emission/count/42"), 42);

        Assert.Equal(new EmissionId("emission/count/42"), scalar.LogicalIdentity);
        Assert.Equal(42, scalar.Value);
    }

    [Fact]
    public void CapabilityPorts_DoNotImplyIncompatibleCapabilitiesByInheritance()
    {
        Type[] ports =
        [
            typeof(IChannelPublicationPort<SamplePayload>),
            typeof(IChannelUnaryInvocationPort<SamplePayload, SampleResponse>),
            typeof(IChannelDemandStreamPort<SamplePayload, SampleResponse>),
            typeof(IChannelDatagramSendPort<SamplePayload>),
            typeof(IChannelDatagramReceivePort<SamplePayload>),
            typeof(IChannelPositionedReplayPort<SamplePayload>),
            typeof(IChannelLeasedReceiptPort<SamplePayload>),
            typeof(IChannelSelectiveSubscriptionPort<SampleSelection, SamplePayload>),
            typeof(IChannelSettlementPort<SampleSettlementIntent>),
            typeof(IChannelAtomicOperationPort<SampleAtomicOperation, SampleAtomicResult>)
        ];

        Assert.All(ports, static port =>
        {
            Assert.True(port.IsInterface);
            Assert.Empty(port.GetInterfaces());
        });

        foreach (var supplied in ports)
        {
            foreach (var demanded in ports)
            {
                if (supplied == demanded)
                    continue;

                Assert.False(
                    demanded.IsAssignableFrom(supplied),
                    $"{supplied.Name} unexpectedly implies {demanded.Name}.");
            }
        }
    }

    [Fact]
    public async Task SameLogicalPayload_CanFlowThroughDistinctPhysicalPortsWithoutIdentityTranslation()
    {
        MultiprotocolReference adapter = new();
        EmissionId logicalIdentity = new("emission/order-created/42");
        ChannelPayload<SamplePayload> publication = new(logicalIdentity, new("order/42"));
        ChannelPayload<SamplePayload> datagram = new(logicalIdentity, new("order/42"));
        ChannelPayload<SamplePayload> invocation = new(logicalIdentity, new("order/42"));

        await ((IChannelPublicationPort<SamplePayload>)adapter).PublishAsync(publication, CancellationToken.None);
        await ((IChannelDatagramSendPort<SamplePayload>)adapter).SendAsync(datagram, CancellationToken.None);
        var response = await ((IChannelUnaryInvocationPort<SamplePayload, SampleResponse>)adapter)
            .InvokeAsync(invocation, CancellationToken.None);

        Assert.NotSame(publication, datagram);
        Assert.NotSame(datagram, invocation);
        Assert.Equal(logicalIdentity, adapter.Publication!.LogicalIdentity);
        Assert.Equal(logicalIdentity, adapter.Datagram!.LogicalIdentity);
        Assert.Equal(logicalIdentity, adapter.Invocation!.LogicalIdentity);
        Assert.Equal(new EmissionId("emission/response/42"), response.LogicalIdentity);
    }

    [Fact]
    public void CapabilitySpecificDeliveries_RequireTheirOwnRuntimeEvidence()
    {
        ChannelPayload<SamplePayload> payload = new(new("emission/42"), new("order/42"));
        ChannelDeliveryAttemptEvidence transient = new(
            attempt: new("attempt/transient"),
            observedAtUtc: Now,
            scope: Scope);

        Assert.Throws<ArgumentException>(() => new ChannelPositionedDelivery<SamplePayload>(payload, transient));
        Assert.Throws<ArgumentException>(() => new ChannelLeasedDelivery<SamplePayload>(payload, transient));

        ChannelSettlementAuthority nonExpiringAuthority = new(
            id: new("authority/non-expiring"),
            attempt: new("attempt/non-expiring"),
            coupling: new("delivery/non-expiring"));
        ChannelDeliveryAttemptEvidence nonExpiring = new(
            attempt: nonExpiringAuthority.Attempt,
            observedAtUtc: Now,
            scope: Scope,
            settlementAuthority: nonExpiringAuthority);
        Assert.Throws<ArgumentException>(() => new ChannelLeasedDelivery<SamplePayload>(payload, nonExpiring));

        ChannelReplayCursor cursor = new(
            formatVersion: 1,
            scope: Scope,
            orderingDomain: new("partition/0"),
            value: "42");
        ChannelDeliveryAttemptEvidence positioned = new(
            attempt: new("attempt/positioned"),
            observedAtUtc: Now,
            scope: Scope,
            replayCursor: cursor);
        var positionedDelivery = new ChannelPositionedDelivery<SamplePayload>(payload, positioned);

        ChannelSettlementAuthority authority = new(
            id: new("authority/leased"),
            attempt: new("attempt/leased"),
            coupling: new("delivery/42"),
            expiresAtUtc: Now.AddMinutes(1));
        ChannelDeliveryAttemptEvidence leased = new(
            attempt: authority.Attempt,
            observedAtUtc: Now,
            scope: Scope,
            providerDelivery: new("provider/42"),
            settlementAuthority: authority);
        var leasedDelivery = new ChannelLeasedDelivery<SamplePayload>(payload, leased);

        Assert.Equal(cursor, positionedDelivery.ReplayCursor);
        Assert.Null(positionedDelivery.Attempt.SettlementAuthority);
        Assert.Equal(authority, leasedDelivery.SettlementAuthority);
        Assert.Null(leasedDelivery.Attempt.ReplayCursor);
        Assert.Same(payload, positionedDelivery.Payload);
        Assert.Same(payload, leasedDelivery.Payload);
    }

    [Fact]
    public void TransientPortContracts_DoNotExposeReplayOrSettlementMembers()
    {
        Type[] transientPorts =
        [
            typeof(IChannelPublicationPort<SamplePayload>),
            typeof(IChannelUnaryInvocationPort<SamplePayload, SampleResponse>),
            typeof(IChannelDemandStreamPort<SamplePayload, SampleResponse>),
            typeof(IChannelDatagramSendPort<SamplePayload>),
            typeof(IChannelDatagramReceivePort<SamplePayload>),
            typeof(IChannelSelectiveSubscriptionPort<SampleSelection, SamplePayload>)
        ];

        Assert.All(transientPorts, static port =>
        {
            var exposedTypes = port
                .GetMethods()
                .SelectMany(static method =>
                    method.GetParameters().Select(static parameter => parameter.ParameterType)
                        .Append(method.ReturnType))
                .SelectMany(FlattenType)
                .ToHashSet();

            Assert.DoesNotContain(typeof(ChannelReplayCursor), exposedTypes);
            Assert.DoesNotContain(typeof(ChannelDeliveryAttemptEvidence), exposedTypes);
            Assert.DoesNotContain(typeof(ChannelSettlementAuthority), exposedTypes);
            Assert.DoesNotContain(typeof(ChannelSettlementReceipt), exposedTypes);
            Assert.DoesNotContain(typeof(ChannelDurableProgressEvidence), exposedTypes);
            Assert.DoesNotContain(typeof(ChannelApplicationProgressReference), exposedTypes);
        });
    }

    [Fact]
    public async Task SettlementAndAtomicity_RemainExplicitTypedBoundaries()
    {
        ChannelProviderDeliveryId delivery = new("provider/42");
        ChannelSettlementAuthority authority = new(
            id: new("authority/42"),
            attempt: new("attempt/42"),
            coupling: new("delivery/42"),
            expiresAtUtc: Now.AddMinutes(1));
        ChannelDurableProgressEvidence progress = new(
            pending: new ChannelStableDeliverySetProgress(Scope, [delivery]));
        ChannelApplicationProgressReference applicationProgress = new(Scope, "checkpoint/42");
        SettlementReference settlement = new();

        var receipt = await settlement.SettleAsync(
            intent: new(authority, delivery),
            durableProgress: progress,
            applicationProgress: applicationProgress,
            cancellationToken: CancellationToken.None);

        AtomicReference atomic = new();
        var atomicResult = await atomic.ExecuteAsync(
            scope: new("consume-checkpoint-settle"),
            operation: new("operation/42"),
            cancellationToken: CancellationToken.None);

        Assert.Equal(authority.Coupling, receipt.Coupling);
        Assert.Equal(ChannelSettlementCouplingKind.PerDelivery, receipt.CouplingKind);
        Assert.Equal(delivery, Assert.Single(receipt.Deliveries));
        Assert.Equal(applicationProgress, receipt.ApplicationProgress);
        Assert.Equal("committed/operation/42", atomicResult.Value);
        Assert.False(typeof(IChannelAtomicOperationPort<SampleAtomicOperation, SampleAtomicResult>)
            .IsAssignableFrom(settlement.GetType()));
        Assert.False(typeof(IChannelSettlementPort<SampleSettlementIntent>)
            .IsAssignableFrom(atomic.GetType()));

        ChannelSettlementAuthority staleAuthority = new(
            id: new("authority/stale"),
            attempt: new("attempt/stale"),
            coupling: new("delivery/stale"),
            expiresAtUtc: Now);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await settlement.SettleAsync(
                intent: new(staleAuthority, delivery),
                durableProgress: progress,
                applicationProgress: applicationProgress,
                cancellationToken: CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await settlement.SettleAsync(
                intent: new(authority, delivery),
                durableProgress: progress,
                applicationProgress: new(new("channel/other"), "checkpoint/42"),
                cancellationToken: CancellationToken.None));
    }

    static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;
        if (!type.IsGenericType)
            yield break;

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in FlattenType(argument))
                yield return nested;
        }
    }

    sealed record SamplePayload(string Value);

    sealed record SampleResponse(string Value);

    sealed record SampleSelection(string Value);

    sealed record SampleSettlementIntent(
        ChannelSettlementAuthority Authority,
        ChannelProviderDeliveryId Delivery);

    sealed record SampleAtomicOperation(string Value);

    sealed record SampleAtomicResult(string Value);

    sealed class MultiprotocolReference :
        IChannelPublicationPort<SamplePayload>,
        IChannelDatagramSendPort<SamplePayload>,
        IChannelUnaryInvocationPort<SamplePayload, SampleResponse>
    {
        public ChannelPayload<SamplePayload>? Publication { get; private set; }
        public ChannelPayload<SamplePayload>? Datagram { get; private set; }
        public ChannelPayload<SamplePayload>? Invocation { get; private set; }

        public ValueTask PublishAsync(
            ChannelPayload<SamplePayload> payload,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);
            cancellationToken.ThrowIfCancellationRequested();
            Publication = payload;
            return ValueTask.CompletedTask;
        }

        public ValueTask SendAsync(
            ChannelPayload<SamplePayload> payload,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);
            cancellationToken.ThrowIfCancellationRequested();
            Datagram = payload;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ChannelPayload<SampleResponse>> InvokeAsync(
            ChannelPayload<SamplePayload> request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Invocation = request;
            ChannelPayload<SampleResponse> response = new(
                logicalIdentity: new("emission/response/42"),
                value: new("accepted"));
            return ValueTask.FromResult(response);
        }
    }

    sealed class SettlementReference : IChannelSettlementPort<SampleSettlementIntent>
    {
        public ValueTask<ChannelSettlementReceipt> SettleAsync(
            SampleSettlementIntent intent,
            ChannelDurableProgressEvidence durableProgress,
            ChannelApplicationProgressReference applicationProgress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(intent);
            ArgumentNullException.ThrowIfNull(durableProgress);
            ArgumentNullException.ThrowIfNull(applicationProgress);
            cancellationToken.ThrowIfCancellationRequested();
            var pending = Assert.IsType<ChannelStableDeliverySetProgress>(durableProgress.Pending);
            if (intent.Authority.ExpiresAtUtc is { } expiry && expiry <= Now)
                throw new InvalidOperationException("Settlement authority is stale.");
            if (pending.Scope != applicationProgress.Scope)
                throw new InvalidOperationException("Application and provider progress scopes differ.");
            if (!pending.Deliveries.Contains(intent.Delivery))
                throw new InvalidOperationException("Durable progress does not cover the settlement delivery.");

            ChannelSettlementReceipt receipt = new(
                kind: ChannelSettlementKind.Individual,
                couplingKind: ChannelSettlementCouplingKind.PerDelivery,
                coupling: intent.Authority.Coupling,
                applicationProgress: applicationProgress,
                settledAtUtc: Now,
                deliveries: [intent.Delivery]);
            return ValueTask.FromResult(receipt);
        }
    }

    sealed class AtomicReference : IChannelAtomicOperationPort<SampleAtomicOperation, SampleAtomicResult>
    {
        public ValueTask<SampleAtomicResult> ExecuteAsync(
            ChannelAtomicScopeId scope,
            SampleAtomicOperation operation,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(scope.Value))
                throw new ArgumentException("An atomic operation requires a non-default scope.", nameof(scope));
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(new ChannelAtomicScopeId("consume-checkpoint-settle"), scope);
            return ValueTask.FromResult(new SampleAtomicResult($"committed/{operation.Value}"));
        }
    }
}
