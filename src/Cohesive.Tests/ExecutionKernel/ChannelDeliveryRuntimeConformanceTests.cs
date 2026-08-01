using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ChannelDeliveryRuntimeConformanceTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PositionedLog_PreservesPartitionLocalOrderAcrossGapsReplayLeaseTransferAndRetention()
    {
        PositionedLogReference log = new();
        log.Append(partition: "partition/0", offset: 0, logicalIdentity: "logical/a");
        log.Append(partition: "partition/0", offset: 2, logicalIdentity: "logical/b");
        log.Append(partition: "partition/1", offset: 0, logicalIdentity: "logical/c");
        var firstLease = log.Assign(partition: "partition/0", owner: "worker/a");

        var first = log.Read(partition: "partition/0", lease: firstLease, nextOffset: 0, observedAtUtc: Now);
        var replay = log.Read(partition: "partition/0", lease: firstLease, nextOffset: 0, observedAtUtc: Now.AddSeconds(1));

        Assert.Equal([0L, 2L], first.Select(static delivery => delivery.Offset));
        Assert.Equal(
            first.Select(static delivery => delivery.Attempt.ProviderDelivery),
            replay.Select(static delivery => delivery.Attempt.ProviderDelivery));
        Assert.NotEqual(first[0].Attempt.Attempt, replay[0].Attempt.Attempt);
        Assert.All(first, static delivery =>
            Assert.Equal("partition/0", delivery.Attempt.ReplayCursor!.OrderingDomain.Value));

        log.Commit(partition: "partition/0", lease: firstLease, nextOffset: 3);
        Assert.Equal(3, log.CommittedNextOffset(partition: "partition/0"));
        Assert.Throws<InvalidOperationException>(() =>
            log.Commit(partition: "partition/0", lease: firstLease, nextOffset: 2));

        var transferredLease = log.Assign(partition: "partition/0", owner: "worker/b");
        Assert.NotEqual(firstLease, transferredLease);
        Assert.Throws<InvalidOperationException>(() =>
            log.Read(partition: "partition/0", lease: firstLease, nextOffset: 0, observedAtUtc: Now));

        log.TrimBefore(partition: "partition/0", retainedFromOffset: 2);
        Assert.Throws<InvalidOperationException>(() =>
            log.Read(partition: "partition/0", lease: transferredLease, nextOffset: 0, observedAtUtc: Now));
        Assert.Equal(
            [2L],
            log.Read(partition: "partition/0", lease: transferredLease, nextOffset: 2, observedAtUtc: Now)
                .Select(static delivery => delivery.Offset));

        var otherLease = log.Assign(partition: "partition/1", owner: "worker/c");
        Assert.Equal(
            [0L],
            log.Read(partition: "partition/1", lease: otherLease, nextOffset: 0, observedAtUtc: Now)
                .Select(static delivery => delivery.Offset));
        Assert.NotEqual(first[0].Attempt.ReplayCursor!.OrderingDomain, new ChannelOrderingDomainId("partition/1"));
    }

    [Fact]
    public void HybridSubscription_RetainsReplayFloorAndPendingGapsWhileSettlementModesRemainIndependent()
    {
        HybridSubscriptionReference subscription = new(scope: "subscription/orders", orderingDomain: "key/customer-1");
        var first = subscription.Deliver(providerDelivery: "message/10", replayPosition: 10, observedAtUtc: Now);
        var second = subscription.Deliver(providerDelivery: "message/12", replayPosition: 12, observedAtUtc: Now);
        var progress = subscription.Checkpoint(
            replayPosition: 13,
            acknowledgedFloor: "message/9",
            unresolved: ["message/10", "message/12"]);

        Assert.Equal("13", progress.ReplayCursor!.Value);
        Assert.Equal(
            new ChannelProviderDeliveryId("message/9"),
            Assert.IsType<ChannelProviderDeliveryProgressFloor>(progress.Floor).Delivery);
        Assert.Equal(
            ["message/10", "message/12"],
            Assert.IsType<ChannelUnresolvedGapProgress>(progress.Pending)
                .Deliveries.Select(static delivery => delivery.Value));

        var individual = subscription.SettleIndividual(
            first,
            progress: new(subscription.Scope, "checkpoint/individual"),
            settledAtUtc: Now.AddSeconds(1));
        Assert.Equal(ChannelSettlementKind.Individual, individual.Kind);
        Assert.Equal(new ChannelProviderDeliveryId("message/10"), Assert.Single(individual.Deliveries));
        Assert.Null(individual.ThroughCursor);

        var staleAuthority = second.SettlementAuthority!;
        var redelivered = subscription.Release(second, observedAtUtc: Now.AddSeconds(2));
        Assert.Equal(second.ProviderDelivery, redelivered.ProviderDelivery);
        Assert.NotEqual(second.Attempt, redelivered.Attempt);
        Assert.NotEqual(staleAuthority.Id, redelivered.SettlementAuthority!.Id);
        Assert.Throws<InvalidOperationException>(() => subscription.SettleIndividual(
            second,
            progress: new(subscription.Scope, "checkpoint/stale"),
            settledAtUtc: Now.AddSeconds(3)));

        var cumulative = subscription.SettleCumulative(
            throughPosition: 12,
            progress: new(subscription.Scope, "checkpoint/cumulative"),
            settledAtUtc: Now.AddSeconds(4));
        Assert.Equal(ChannelSettlementKind.CumulativePrefix, cumulative.Kind);
        Assert.Equal("12", cumulative.ThroughCursor!.Value);
        Assert.Empty(cumulative.Deliveries);
    }

    [Fact]
    public void LeasedQueue_RotatesAuthorityAndRequiresExactDurableCoverageBeforePartialSettlement()
    {
        LeasedQueueReference queue = new(
            scope: "queue/work",
            lockDuration: TimeSpan.FromSeconds(30),
            quarantine: QuarantineRealization.Composed);
        queue.Enqueue(logicalIdentity: "logical/a", providerDelivery: "delivery/a");
        queue.Enqueue(logicalIdentity: "logical/b", providerDelivery: "delivery/b");
        queue.Enqueue(logicalIdentity: "logical/c", providerDelivery: "delivery/c");

        var batch = queue.Receive(maxCount: 3, observedAtUtc: Now, CancellationToken.None);
        Assert.Equal(3, batch.Length);
        Assert.All(batch, static delivery => Assert.Null(delivery.Attempt.ReplayCursor));

        var renewed = queue.Renew(batch[0], expiresAtUtc: Now.AddMinutes(1), renewedAtUtc: Now.AddSeconds(5));
        Assert.Equal(batch[0].Attempt.Attempt, renewed.Attempt.Attempt);
        Assert.NotEqual(
            batch[0].Attempt.SettlementAuthority!.Id,
            renewed.Attempt.SettlementAuthority!.Id);
        Assert.Throws<InvalidOperationException>(() => queue.Complete(
            batch[0],
            durableDeliveries: [batch[0].Attempt.ProviderDelivery!.Value],
            completedAtUtc: Now.AddSeconds(6)));

        Assert.Throws<InvalidOperationException>(() => queue.Complete(
            renewed,
            durableDeliveries: [batch[1].Attempt.ProviderDelivery!.Value],
            completedAtUtc: Now.AddSeconds(6)));
        queue.Complete(
            renewed,
            durableDeliveries: [renewed.Attempt.ProviderDelivery!.Value],
            completedAtUtc: Now.AddSeconds(6));
        queue.Complete(
            batch[1],
            durableDeliveries: [batch[1].Attempt.ProviderDelivery!.Value],
            completedAtUtc: Now.AddSeconds(6));

        var redelivered = queue.Receive(maxCount: 1, observedAtUtc: Now.AddSeconds(31), CancellationToken.None);
        var delivery = Assert.Single(redelivered);
        Assert.Equal("logical/c", delivery.LogicalIdentity);
        Assert.Equal(batch[2].Attempt.ProviderDelivery, delivery.Attempt.ProviderDelivery);
        Assert.NotEqual(batch[2].Attempt.Attempt, delivery.Attempt.Attempt);
        Assert.NotEqual(
            batch[2].Attempt.SettlementAuthority!.Id,
            delivery.Attempt.SettlementAuthority!.Id);

        var quarantine = queue.Quarantine(
            delivery,
            durableDeliveries: [delivery.Attempt.ProviderDelivery!.Value],
            completedAtUtc: Now.AddSeconds(32));
        Assert.Equal(QuarantineRealization.Composed, quarantine);

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            queue.Receive(maxCount: 1, observedAtUtc: Now, cancelled.Token));
    }

    sealed class PositionedLogReference
    {
        readonly Dictionary<string, List<LogEntry>> entries = new(StringComparer.Ordinal);
        readonly Dictionary<string, long> retainedFrom = new(StringComparer.Ordinal);
        readonly Dictionary<string, long> committed = new(StringComparer.Ordinal);
        readonly Dictionary<string, PartitionLease> leases = new(StringComparer.Ordinal);
        long attemptSequence;

        public void Append(string partition, long offset, string logicalIdentity)
        {
            if (!entries.TryGetValue(partition, out var partitionEntries))
            {
                partitionEntries = [];
                entries.Add(partition, partitionEntries);
            }
            if (partitionEntries.Count > 0 && partitionEntries[^1].Offset >= offset)
                throw new InvalidOperationException("Log offsets must increase inside one partition.");
            partitionEntries.Add(new(offset, logicalIdentity));
        }

        public PartitionLease Assign(string partition, string owner)
        {
            var epoch = leases.TryGetValue(partition, out var prior) ? checked(prior.Epoch + 1) : 1;
            PartitionLease lease = new(partition, owner, epoch);
            leases[partition] = lease;
            return lease;
        }

        public ImmutableArray<ObservedLogDelivery> Read(
            string partition,
            PartitionLease lease,
            long nextOffset,
            DateTimeOffset observedAtUtc)
        {
            RequireCurrent(partition, lease);
            if (retainedFrom.TryGetValue(partition, out var floor) && nextOffset < floor)
                throw new InvalidOperationException("The requested replay cursor expired outside retained history.");
            if (!entries.TryGetValue(partition, out var partitionEntries))
                return [];

            ImmutableArray<ObservedLogDelivery>.Builder result = ImmutableArray.CreateBuilder<ObservedLogDelivery>();
            foreach (var entry in partitionEntries)
            {
                if (entry.Offset < nextOffset)
                    continue;
                var scope = new ChannelScopeId($"log/{partition}");
                ChannelReplayCursor cursor = new(
                    formatVersion: 1,
                    scope: scope,
                    orderingDomain: new(partition),
                    value: entry.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                ChannelDeliveryAttemptEvidence attempt = new(
                    attempt: new($"attempt/{++attemptSequence}"),
                    observedAtUtc: observedAtUtc,
                    scope: scope,
                    providerDelivery: new($"{partition}/{entry.Offset}"),
                    replayCursor: cursor,
                    evidenceReference: $"lease/{lease.Epoch}");
                result.Add(new(entry.Offset, entry.LogicalIdentity, attempt));
            }
            return result.ToImmutable();
        }

        public void Commit(string partition, PartitionLease lease, long nextOffset)
        {
            RequireCurrent(partition, lease);
            if (committed.TryGetValue(partition, out var prior) && nextOffset < prior)
                throw new InvalidOperationException("A cumulative log commit cannot regress.");
            committed[partition] = nextOffset;
        }

        public long? CommittedNextOffset(string partition) =>
            committed.TryGetValue(partition, out var value) ? value : null;

        public void TrimBefore(string partition, long retainedFromOffset) =>
            retainedFrom[partition] = retainedFromOffset;

        void RequireCurrent(string partition, PartitionLease lease)
        {
            if (!leases.TryGetValue(partition, out var current) || current != lease)
                throw new InvalidOperationException("The partition lease was transferred to another owner or epoch.");
        }
    }

    readonly record struct LogEntry(long Offset, string LogicalIdentity);
    readonly record struct PartitionLease(string Partition, string Owner, long Epoch);
    readonly record struct ObservedLogDelivery(
        long Offset,
        string LogicalIdentity,
        ChannelDeliveryAttemptEvidence Attempt);

    sealed class HybridSubscriptionReference
    {
        readonly Dictionary<ChannelProviderDeliveryId, ChannelDeliveryAttemptEvidence> current = [];
        long attemptSequence;
        long authoritySequence;

        public HybridSubscriptionReference(string scope, string orderingDomain)
        {
            Scope = new(scope);
            OrderingDomain = new(orderingDomain);
        }

        public ChannelScopeId Scope { get; }
        public ChannelOrderingDomainId OrderingDomain { get; }

        public ChannelDeliveryAttemptEvidence Deliver(
            string providerDelivery,
            long replayPosition,
            DateTimeOffset observedAtUtc)
        {
            ChannelProviderDeliveryId delivery = new(providerDelivery);
            var attempt = new ChannelDeliveryAttemptId($"attempt/{++attemptSequence}");
            ChannelSettlementAuthority authority = new(
                id: new($"authority/{++authoritySequence}"),
                attempt: attempt,
                coupling: new($"subscription/{Scope.Value}"),
                expiresAtUtc: observedAtUtc.AddMinutes(1));
            ChannelDeliveryAttemptEvidence observation = new(
                attempt: attempt,
                observedAtUtc: observedAtUtc,
                scope: Scope,
                providerDelivery: delivery,
                replayCursor: Cursor(replayPosition),
                settlementAuthority: authority);
            current[delivery] = observation;
            return observation;
        }

        public ChannelDurableProgressEvidence Checkpoint(
            long replayPosition,
            string acknowledgedFloor,
            ImmutableArray<string> unresolved) =>
            new(
                replayCursor: Cursor(replayPosition),
                floor: new ChannelProviderDeliveryProgressFloor(
                    scope: Scope,
                    orderingDomain: OrderingDomain,
                    delivery: new(acknowledgedFloor)),
                pending: new ChannelUnresolvedGapProgress(
                    scope: Scope,
                    deliveries: [.. unresolved.Select(static value => new ChannelProviderDeliveryId(value))]));

        public ChannelDeliveryAttemptEvidence Release(
            ChannelDeliveryAttemptEvidence delivery,
            DateTimeOffset observedAtUtc)
        {
            RequireCurrent(delivery);
            return Deliver(
                providerDelivery: delivery.ProviderDelivery!.Value.Value,
                replayPosition: long.Parse(
                    delivery.ReplayCursor!.Value,
                    System.Globalization.CultureInfo.InvariantCulture),
                observedAtUtc: observedAtUtc);
        }

        public ChannelSettlementReceipt SettleIndividual(
            ChannelDeliveryAttemptEvidence delivery,
            ChannelApplicationProgressReference progress,
            DateTimeOffset settledAtUtc)
        {
            RequireCurrent(delivery);
            current.Remove(delivery.ProviderDelivery!.Value);
            return new(
                kind: ChannelSettlementKind.Individual,
                couplingKind: ChannelSettlementCouplingKind.PerDelivery,
                coupling: delivery.SettlementAuthority!.Coupling,
                applicationProgress: progress,
                settledAtUtc: settledAtUtc,
                deliveries: [delivery.ProviderDelivery.Value]);
        }

        public ChannelSettlementReceipt SettleCumulative(
            long throughPosition,
            ChannelApplicationProgressReference progress,
            DateTimeOffset settledAtUtc) =>
            new(
                kind: ChannelSettlementKind.CumulativePrefix,
                couplingKind: ChannelSettlementCouplingKind.OrderingScope,
                coupling: new($"subscription/{Scope.Value}"),
                applicationProgress: progress,
                settledAtUtc: settledAtUtc,
                throughCursor: Cursor(throughPosition));

        ChannelReplayCursor Cursor(long position) => new(
            formatVersion: 1,
            scope: Scope,
            orderingDomain: OrderingDomain,
            value: position.ToString(System.Globalization.CultureInfo.InvariantCulture));

        void RequireCurrent(ChannelDeliveryAttemptEvidence delivery)
        {
            if (delivery.ProviderDelivery is not { } id
                || !current.TryGetValue(id, out var currentAttempt)
                || currentAttempt.Attempt != delivery.Attempt
                || currentAttempt.SettlementAuthority?.Id != delivery.SettlementAuthority?.Id)
            {
                throw new InvalidOperationException("Settlement authority is stale for this delivery attempt.");
            }
        }
    }

    enum QuarantineRealization
    {
        Native,
        Composed
    }

    sealed class LeasedQueueReference
    {
        readonly ChannelScopeId scope;
        readonly TimeSpan lockDuration;
        readonly QuarantineRealization quarantine;
        readonly Queue<QueuedDelivery> available = [];
        readonly Dictionary<ChannelProviderDeliveryId, LeasedDelivery> leased = [];
        long attemptSequence;
        long authoritySequence;

        public LeasedQueueReference(string scope, TimeSpan lockDuration, QuarantineRealization quarantine)
        {
            this.scope = new(scope);
            this.lockDuration = lockDuration;
            this.quarantine = quarantine;
        }

        public void Enqueue(string logicalIdentity, string providerDelivery) =>
            available.Enqueue(new(logicalIdentity, new(providerDelivery)));

        public ImmutableArray<LeasedDelivery> Receive(
            int maxCount,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maxCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCount));
            RequeueExpired(observedAtUtc);
            var count = Math.Min(maxCount, available.Count);
            var result = ImmutableArray.CreateBuilder<LeasedDelivery>(count);
            for (var index = 0; index < count; index++)
            {
                var queued = available.Dequeue();
                result.Add(Lease(queued, observedAtUtc));
            }
            return result.MoveToImmutable();
        }

        public LeasedDelivery Renew(
            LeasedDelivery delivery,
            DateTimeOffset expiresAtUtc,
            DateTimeOffset renewedAtUtc)
        {
            RequireCurrent(delivery, renewedAtUtc);
            ChannelSettlementAuthority authority = new(
                id: new($"receipt/{++authoritySequence}"),
                attempt: delivery.Attempt.Attempt,
                coupling: delivery.Attempt.SettlementAuthority!.Coupling,
                expiresAtUtc: expiresAtUtc);
            var renewed = delivery with
            {
                Attempt = new(
                    attempt: delivery.Attempt.Attempt,
                    observedAtUtc: delivery.Attempt.ObservedAtUtc,
                    scope: scope,
                    providerDelivery: delivery.Attempt.ProviderDelivery,
                    settlementAuthority: authority)
            };
            leased[delivery.Attempt.ProviderDelivery!.Value] = renewed;
            return renewed;
        }

        public void Complete(
            LeasedDelivery delivery,
            ImmutableArray<ChannelProviderDeliveryId> durableDeliveries,
            DateTimeOffset completedAtUtc)
        {
            RequireCurrent(delivery, completedAtUtc);
            if (!durableDeliveries.Contains(delivery.Attempt.ProviderDelivery!.Value))
                throw new InvalidOperationException("The durable application checkpoint does not cover this delivery.");
            leased.Remove(delivery.Attempt.ProviderDelivery.Value);
        }

        public QuarantineRealization Quarantine(
            LeasedDelivery delivery,
            ImmutableArray<ChannelProviderDeliveryId> durableDeliveries,
            DateTimeOffset completedAtUtc)
        {
            Complete(delivery, durableDeliveries, completedAtUtc);
            return quarantine;
        }

        LeasedDelivery Lease(QueuedDelivery queued, DateTimeOffset observedAtUtc)
        {
            var attempt = new ChannelDeliveryAttemptId($"attempt/{++attemptSequence}");
            ChannelSettlementAuthority authority = new(
                id: new($"receipt/{++authoritySequence}"),
                attempt: attempt,
                coupling: new($"delivery/{queued.ProviderDelivery.Value}"),
                expiresAtUtc: observedAtUtc.Add(lockDuration));
            ChannelDeliveryAttemptEvidence evidence = new(
                attempt: attempt,
                observedAtUtc: observedAtUtc,
                scope: scope,
                providerDelivery: queued.ProviderDelivery,
                settlementAuthority: authority);
            LeasedDelivery leasedDelivery = new(queued.LogicalIdentity, evidence);
            leased[queued.ProviderDelivery] = leasedDelivery;
            return leasedDelivery;
        }

        void RequeueExpired(DateTimeOffset observedAtUtc)
        {
            foreach (var delivery in leased.Values.ToArray())
            {
                if (delivery.Attempt.SettlementAuthority!.ExpiresAtUtc > observedAtUtc)
                    continue;
                leased.Remove(delivery.Attempt.ProviderDelivery!.Value);
                available.Enqueue(new(delivery.LogicalIdentity, delivery.Attempt.ProviderDelivery.Value));
            }
        }

        void RequireCurrent(LeasedDelivery delivery, DateTimeOffset atUtc)
        {
            var id = delivery.Attempt.ProviderDelivery!.Value;
            if (!leased.TryGetValue(id, out var current)
                || current.Attempt.Attempt != delivery.Attempt.Attempt
                || current.Attempt.SettlementAuthority!.Id != delivery.Attempt.SettlementAuthority!.Id
                || current.Attempt.SettlementAuthority.ExpiresAtUtc <= atUtc)
            {
                throw new InvalidOperationException("The settlement receipt is stale, expired, or belongs to another attempt.");
            }
        }
    }

    readonly record struct QueuedDelivery(string LogicalIdentity, ChannelProviderDeliveryId ProviderDelivery);
    sealed record LeasedDelivery(string LogicalIdentity, ChannelDeliveryAttemptEvidence Attempt);
}
