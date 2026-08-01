using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Execution;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Cohesive.Tests.Storage;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Cosmos;

public sealed class CosmosManagedMaterializationChangeSourceTests
{
    const string DefaultLeaseStoreIdentity = "tests/lease-store/default";
    const string TestMasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    static readonly QualifiedShapeId EntityShape = new(new("tests/cosmos-managed/v1"), new("Load"));
    static readonly QualifiedShapeId OutboxShape = new(new("tests/cosmos-managed/v1"), new("LoadMessage"));
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan =
        new("sha256", "tests/canonicalization/v1", "0123456789abcdef");
    static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    static readonly MaterializationManagedChangeRequest ManagedRequest = new(
        new("tests/materialization"),
        new ExecutionDefinitionFingerprint(
            "sha256",
            "execution-definition/v1",
            "0123456789abcdef"),
        new("generation-1"));
    static readonly byte[] AuthenticationKey =
        Enumerable.Range(1, 32).Select(static value => checked((byte)value)).ToArray();

    internal static ManagedChangeSourceConformanceCase CreateConformanceCase() => new(
        Adapter: "Cosmos DB",
        ObserveCallbackFailureAsync: ObserveCallbackFailureAsync,
        ObserveDurableCheckpointFailureAsync: ObserveDurableCheckpointFailureAsync,
        ObserveCrashBeforeSettlementAsync: ObserveCrashBeforeSettlementAsync,
        ObserveCrashAfterSettlementAsync: ObserveCrashAfterSettlementAsync,
        ObserveDuplicateReplayAsync: ObserveDuplicateReplayAsync,
        ObserveCancellationAsync: ObserveCancellationAsync,
        ObserveLeaseTransferAsync: ObserveLeaseTransferAsync);

    static async Task<ManagedChangeSourceRejectedObservation> ObserveCallbackFailureAsync()
    {
        FakeManagedProcessor processor = new();
        processor.AddBatch(EntityDocument("load-a", partitionKey: "tenant-a"));
        RecordingSettlementObserver observer = new();
        var fixture = CreateEntityFixture(processor: processor, observer: observer);
        var handlerInvocations = 0;
        var failureObserved = false;

        try
        {
            await fixture.Source.RunAsync(
                Context(),
                ManagedRequest,
                (_, _, _) =>
                {
                    handlerInvocations++;
                    throw new TestHandlerException("application failed");
                });
        }
        catch (TestHandlerException)
        {
            failureObserved = true;
        }

        return new(
            ExpectedFailureObserved: failureObserved,
            HandlerInvocations: handlerInvocations,
            ProviderSettlementAttempts: processor.CheckpointAttemptCount,
            ProviderSettlements: processor.CheckpointCount,
            SettlementObservations: observer.Observations.Count);
    }

    static async Task<ManagedChangeSourceRejectedObservation> ObserveDurableCheckpointFailureAsync()
    {
        FakeManagedProcessor processor = new();
        processor.AddBatch(EntityDocument("load-a", partitionKey: "tenant-a"));
        RecordingSettlementObserver observer = new();
        var fixture = CreateEntityFixture(processor: processor, observer: observer);
        var handlerInvocations = 0;
        var failureObserved = false;

        try
        {
            await fixture.Source.RunAsync(
                Context(),
                ManagedRequest,
                (_, _, _) =>
                {
                    handlerInvocations++;
                    throw new TestDurableCheckpointException("application checkpoint failed");
                });
        }
        catch (TestDurableCheckpointException)
        {
            failureObserved = true;
        }

        return new(
            ExpectedFailureObserved: failureObserved,
            HandlerInvocations: handlerInvocations,
            ProviderSettlementAttempts: processor.CheckpointAttemptCount,
            ProviderSettlements: processor.CheckpointCount,
            SettlementObservations: observer.Observations.Count);
    }

    static async Task<ManagedChangeSourceCrashBeforeSettlementObservation> ObserveCrashBeforeSettlementAsync()
    {
        var document = EntityDocument("load-a", partitionKey: "tenant-a", etag: "revision-a");
        FakeManagedProcessor crashingProcessor = new()
        {
            CheckpointFailure = new TestProviderCheckpointException("provider checkpoint failed")
        };
        crashingProcessor.AddBatch(document, feedRangeJson: "range/owned", continuationToken: "position/10");
        RecordingSettlementObserver firstObserver = new();
        var first = CreateEntityFixture(processor: crashingProcessor, observer: firstObserver);
        MaterializationChangeDelivery? firstDelivery = null;
        DurableCheckpointHandler durableHandler = new();
        List<MaterializationProgressMutationDisposition> dispositions = [];
        var failureObserved = false;

        try
        {
            await first.Source.RunAsync(
                Context(),
                ManagedRequest,
                async (context, progress, page) =>
                {
                    firstDelivery = Assert.Single(page.Deliveries);
                    var result = await durableHandler.ApplyAsync(context, progress, page);
                    dispositions.Add(result.Disposition);
                    return result;
                });
        }
        catch (TestProviderCheckpointException)
        {
            failureObserved = true;
        }

        FakeManagedProcessor replayProcessor = new();
        replayProcessor.AddBatch(document, feedRangeJson: "range/owned", continuationToken: "position/10");
        RecordingSettlementObserver replayObserver = new();
        var replay = CreateEntityFixture(
            processor: replayProcessor,
            observer: replayObserver,
            processorName: "processor/other-deployment-seed",
            instanceName: "worker-b");
        MaterializationChangeDelivery? replayDelivery = null;

        await replay.Source.RunAsync(
            Context(),
            ManagedRequest,
            async (context, progress, page) =>
            {
                replayDelivery = Assert.Single(page.Deliveries);
                var result = await durableHandler.ApplyAsync(context, progress, page);
                dispositions.Add(result.Disposition);
                return result;
            });

        return new(
            ExpectedFailureObserved: failureObserved,
            InitialDelivery: Assert.IsType<MaterializationChangeDelivery>(firstDelivery).Id,
            ReplayedDelivery: Assert.IsType<MaterializationChangeDelivery>(replayDelivery).Id,
            InitialChange: firstDelivery!.Change.Id,
            ReplayedChange: replayDelivery!.Change.Id,
            ApplicationDispositions: [.. dispositions],
            InitialProviderSettlementAttempts: crashingProcessor.CheckpointAttemptCount,
            InitialProviderSettlements: crashingProcessor.CheckpointCount,
            InitialSettlementObservations: firstObserver.Observations.Count,
            ReplayProviderSettlements: replayProcessor.CheckpointCount,
            ReplaySettlementObservations: replayObserver.Observations.Count);
    }

    [Fact]
    public async Task CancellationBeforeCallback_DoesNotInvokeHandlerOrProviderCheckpoint()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        FakeManagedProcessor processor = new();
        processor.AddBatch(EntityDocument("load-a", partitionKey: "tenant-a"));
        var fixture = CreateEntityFixture(processor: processor);
        var handlerCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Source.RunAsync(
            Context(cancellation.Token),
            ManagedRequest,
            (_, progress, page) =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Success(progress, page, "checkpoint/unexpected"));
            }));

        Assert.Equal(0, handlerCalls);
        Assert.Equal(0, processor.CheckpointAttemptCount);
    }

    static async Task<ManagedChangeSourceCancellationObservation> ObserveCancellationAsync()
    {
        using CancellationTokenSource cancellation = new();
        FakeManagedProcessor processor = new();
        processor.AddBatch(EntityDocument("load-a", partitionKey: "tenant-a"));
        RecordingSettlementObserver observer = new();
        var fixture = CreateEntityFixture(processor: processor, observer: observer);
        var handlerInvocations = 0;
        var cancellationObserved = false;

        try
        {
            await fixture.Source.RunAsync(
                Context(cancellation.Token),
                ManagedRequest,
                (_, progress, page) =>
                {
                    handlerInvocations++;
                    var proof = Success(progress, page, "checkpoint/durable-before-cancel");
                    cancellation.Cancel();
                    return ValueTask.FromResult(proof);
                });
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        return new(
            CancellationObserved: cancellationObserved,
            HandlerInvocations: handlerInvocations,
            ProviderSettlementAttempts: processor.CheckpointAttemptCount,
            ProviderSettlements: processor.CheckpointCount,
            SettlementObservations: observer.Observations.Count);
    }

    [Fact]
    public async Task LeaseCallbackCancellationAfterDurableProof_DoesNotAdvanceProviderCheckpoint()
    {
        using CancellationTokenSource leaseCancellation = new();
        FakeManagedProcessor processor = new();
        processor.AddBatch(
            document: EntityDocument("load-a", partitionKey: "tenant-a"),
            callbackCancellationToken: leaseCancellation.Token);
        RecordingSettlementObserver observer = new();
        var fixture = CreateEntityFixture(processor: processor, observer: observer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                var proof = Success(progress, page, "checkpoint/durable-before-lease-loss");
                leaseCancellation.Cancel();
                return ValueTask.FromResult(proof);
            }));

        Assert.Equal(0, processor.CheckpointAttemptCount);
        Assert.Empty(observer.Observations);
    }

    [Fact]
    public async Task CancellationDuringSuccessfulProviderCheckpoint_RecordsCompletedSettlement()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<bool> checkpointEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> completeCheckpoint = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeManagedProcessor processor = new()
        {
            CheckpointOperation = async () =>
            {
                checkpointEntered.TrySetResult(true);
                await completeCheckpoint.Task;
            }
        };
        processor.AddBatch(EntityDocument("load-a", partitionKey: "tenant-a"));
        RecordingSettlementObserver observer = new();
        var fixture = CreateEntityFixture(processor: processor, observer: observer);
        var handlerCalls = 0;

        var run = fixture.Source.RunAsync(
            Context(cancellation.Token),
            ManagedRequest,
            (_, progress, page) =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Success(
                    progress: progress,
                    page: page,
                    checkpointId: "checkpoint/durable-before-in-flight-cancel"));
            });
        await checkpointEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        completeCheckpoint.TrySetResult(true);

        await run;

        Assert.Equal(1, handlerCalls);
        Assert.Equal(1, processor.CheckpointAttemptCount);
        Assert.Equal(1, processor.CheckpointCount);
        Assert.Single(observer.Observations);
    }

    static async Task<ManagedChangeSourceLeaseTransferObservation> ObserveLeaseTransferAsync()
    {
        var document = EntityDocument("load-a", partitionKey: "tenant-a", etag: "revision-a");
        using CancellationTokenSource leaseLoss = new();
        FakeManagedProcessor initialProcessor = new();
        initialProcessor.AddBatch(
            document: document,
            feedRangeJson: "range/initial-owner",
            continuationToken: "position/initial-owner",
            callbackCancellationToken: leaseLoss.Token);
        RecordingSettlementObserver initialObserver = new();
        var initialFixture = CreateEntityFixture(
            processor: initialProcessor,
            observer: initialObserver,
            processorName: "processor/shared-deployment",
            instanceName: "worker/initial-owner");
        MaterializationChangeDelivery? initial = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialFixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                initial = Assert.Single(page.Deliveries);
                var proof = Success(progress, page, "checkpoint/initial-owner");
                leaseLoss.Cancel();
                return ValueTask.FromResult(proof);
            }));

        FakeManagedProcessor transferredProcessor = new()
        {
            ContinueAfterCallbackCancellation = true
        };
        transferredProcessor.AddBatch(
            document: document,
            feedRangeJson: "range/lost-lease",
            continuationToken: "position/lost-lease",
            callbackCancellationToken: new CancellationToken(canceled: true));
        transferredProcessor.AddBatch(
            document: document,
            feedRangeJson: "range/transferred",
            continuationToken: "position/transferred");
        RecordingSettlementObserver transferredObserver = new();
        var transferredFixture = CreateEntityFixture(
            processor: transferredProcessor,
            observer: transferredObserver,
            processorName: "processor/shared-deployment",
            instanceName: "worker/transferred-owner");
        MaterializationChangeDelivery? transferred = null;
        var handlerInvocations = 0;

        await transferredFixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                handlerInvocations++;
                transferred = Assert.Single(page.Deliveries);
                return ValueTask.FromResult(Success(progress, page, "checkpoint/transferred"));
            });

        var initialDelivery = Assert.IsType<MaterializationChangeDelivery>(initial);
        var transferredDelivery = Assert.IsType<MaterializationChangeDelivery>(transferred);
        return new(
            InitialDelivery: initialDelivery.Id,
            TransferredDelivery: transferredDelivery.Id,
            InitialChange: initialDelivery.Change.Id,
            TransferredChange: transferredDelivery.Change.Id,
            InitialProviderSettlements: initialProcessor.CheckpointCount,
            InitialSettlementObservations: initialObserver.Observations.Count,
            TransferredHandlerInvocations: handlerInvocations,
            TransferredProviderSettlements: transferredProcessor.CheckpointCount,
            TransferredSettlementObservations: transferredObserver.Observations.Count);
    }

    [Fact]
    public async Task ManagedPosition_RoundTripsExactProviderRangeAndContinuation()
    {
        const string FeedRangeJson = "{\"min\":\"AA==\",\"max\":\"BB==\"}";
        const string ContinuationToken = "provider/opaque?token=1&owner=former";
        FakeManagedProcessor processor = new();
        processor.AddBatch(
            EntityDocument("load-a", partitionKey: "tenant-a"),
            feedRangeJson: FeedRangeJson,
            continuationToken: ContinuationToken);
        var fixture = CreateEntityFixture(processor: processor);
        MaterializationSourcePosition? position = null;

        await fixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                position = page.ThroughPosition;
                return ValueTask.FromResult(Success(progress, page, "checkpoint/round-trip"));
            });

        var boundary = fixture.Source.DecodeProviderBoundary(
            Assert.IsType<MaterializationSourcePosition>(position));
        Assert.Equal(FeedRangeJson, boundary.FeedRangeJson);
        Assert.Equal(ContinuationToken, boundary.ContinuationToken);
    }

    [Fact]
    public async Task LeaseOwnershipAndFeedRangeChanges_DoNotAlterLogicalRevisionIdentity()
    {
        var document = EntityDocument("load-a", partitionKey: "tenant-a", etag: "revision-a");
        var first = await ReadSingleDeliveryAsync(
            document: document,
            feedRangeJson: "range/parent",
            continuationToken: "position/parent",
            processorName: "processor/one",
            instanceName: "worker/one");
        var transferred = await ReadSingleDeliveryAsync(
            document: document,
            feedRangeJson: "range/parent",
            continuationToken: "position/parent",
            processorName: "processor/two",
            instanceName: "worker/two");
        var split = await ReadSingleDeliveryAsync(
            document: document,
            feedRangeJson: "range/child-after-split",
            continuationToken: "position/child",
            processorName: "processor/three",
            instanceName: "worker/three");
        var changedPolicy = await ReadSingleDeliveryAsync(
            document: document,
            feedRangeJson: "range/parent",
            continuationToken: "position/parent",
            processorName: "processor/one",
            instanceName: "worker/one",
            pollInterval: TimeSpan.FromSeconds(2));
        var changedLeaseStore = await ReadSingleDeliveryAsync(
            document: document,
            feedRangeJson: "range/parent",
            continuationToken: "position/parent",
            processorName: "processor/one",
            instanceName: "worker/one",
            leaseStoreIdentity: "tests/lease-store/alternate");

        Assert.Equal(first.Id, transferred.Id);
        Assert.Equal(first.Change.Id, transferred.Change.Id);
        Assert.Equal(first.Id, split.Id);
        Assert.Equal(first.Change.Id, split.Change.Id);
        Assert.Equal(first.Id, changedPolicy.Id);
        Assert.Equal(first.Change.Id, changedPolicy.Change.Id);
        Assert.Equal(first.Id, changedLeaseStore.Id);
        Assert.Equal(first.Change.Id, changedLeaseStore.Change.Id);
        Assert.Contains("logical-partition/sha256/", first.Change.Scope.Partition.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", first.Change.Scope.Partition.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiPartitionCallback_RequiresEveryDurableProofBeforeOneProviderCheckpoint()
    {
        var documents = ImmutableArray.Create(
            EntityDocument("load-a", partitionKey: "tenant-a", etag: "revision-a"),
            EntityDocument("load-b", partitionKey: "tenant-b", etag: "revision-b"));
        FakeManagedProcessor failedProcessor = new();
        failedProcessor.AddBatch(documents, feedRangeJson: "range/shared", continuationToken: "position/20");
        RecordingSettlementObserver failedObserver = new();
        var failed = CreateEntityFixture(processor: failedProcessor, observer: failedObserver);
        List<(MaterializationProgressKey Progress, MaterializationChangePage Page)> firstAttempt = [];
        List<MaterializationProgressMutationDisposition> firstDispositions = [];
        DurableCheckpointHandler durableHandler = new();

        await Assert.ThrowsAsync<TestHandlerException>(() => failed.Source.RunAsync(
            Context(),
            ManagedRequest,
            async (context, progress, page) =>
            {
                firstAttempt.Add((progress, page));
                if (Assert.Single(page.Deliveries).Change.SubjectIdentity == "load-b")
                {
                    throw new TestHandlerException("partition-b failed");
                }

                var result = await durableHandler.ApplyAsync(context, progress, page);
                firstDispositions.Add(result.Disposition);
                return result;
            }));

        Assert.Equal(2, firstAttempt.Count);
        Assert.Equal([MaterializationProgressMutationDisposition.Applied], firstDispositions);
        Assert.Equal(0, failedProcessor.CheckpointCount);
        Assert.Empty(failedObserver.Observations);

        FakeManagedProcessor replayProcessor = new();
        replayProcessor.AddBatch(documents, feedRangeJson: "range/shared", continuationToken: "position/20");
        RecordingSettlementObserver replayObserver = new();
        var replay = CreateEntityFixture(processor: replayProcessor, observer: replayObserver);
        List<(MaterializationProgressKey Progress, MaterializationChangePage Page)> secondAttempt = [];
        List<MaterializationProgressMutationDisposition> secondDispositions = [];

        await replay.Source.RunAsync(
            Context(),
            ManagedRequest,
            async (context, progress, page) =>
            {
                secondAttempt.Add((progress, page));
                var result = await durableHandler.ApplyAsync(context, progress, page);
                secondDispositions.Add(result.Disposition);
                return result;
            });

        Assert.Equal(2, secondAttempt.Count);
        Assert.Equal(
            [
                MaterializationProgressMutationDisposition.Replayed,
                MaterializationProgressMutationDisposition.Applied
            ],
            secondDispositions);
        Assert.Equal(1, replayProcessor.CheckpointCount);
        Assert.Equal(2, replayObserver.Observations.Count);
        Assert.Equal(
            2,
            replayObserver.Observations
                .Select(static observation => observation.Settlement.Id)
                .Distinct()
                .Count());
        Assert.Equal(
            firstAttempt.SelectMany(static attempt => attempt.Page.Deliveries).Select(static delivery => delivery.Id.Value).Order(),
            secondAttempt.SelectMany(static attempt => attempt.Page.Deliveries).Select(static delivery => delivery.Id.Value).Order());
    }

    [Fact]
    public async Task FeedRangeSplitAfterFailedSettlement_PreservesDeliveryIdentityAndPersistsNewBoundary()
    {
        var document = EntityDocument("load-a", partitionKey: "tenant-a", etag: "revision-a");
        FakeManagedProcessor parentProcessor = new()
        {
            CheckpointFailure = new TestProviderCheckpointException("parent range checkpoint failed")
        };
        parentProcessor.AddBatch(
            document: document,
            feedRangeJson: "range/parent",
            continuationToken: "position/parent");
        var parent = CreateEntityFixture(processor: parentProcessor);
        DurableCheckpointHandler durableHandler = new();
        MaterializationChangeDelivery? parentDelivery = null;
        MaterializationSourcePosition? parentPosition = null;
        List<MaterializationProgressMutationDisposition> dispositions = [];

        await Assert.ThrowsAsync<TestProviderCheckpointException>(() => parent.Source.RunAsync(
            Context(),
            ManagedRequest,
            async (context, progress, page) =>
            {
                parentDelivery = Assert.Single(page.Deliveries);
                parentPosition = page.ThroughPosition;
                var result = await durableHandler.ApplyAsync(context, progress, page);
                dispositions.Add(result.Disposition);
                return result;
            }));

        FakeManagedProcessor childProcessor = new();
        childProcessor.AddBatch(
            document: document,
            feedRangeJson: "range/child-after-split",
            continuationToken: "position/child");
        var child = CreateEntityFixture(processor: childProcessor);
        MaterializationChangeDelivery? childDelivery = null;
        MaterializationSourcePosition? childPosition = null;

        await child.Source.RunAsync(
            Context(),
            ManagedRequest,
            async (context, progress, page) =>
            {
                childDelivery = Assert.Single(page.Deliveries);
                childPosition = page.ThroughPosition;
                var result = await durableHandler.ApplyAsync(context, progress, page);
                dispositions.Add(result.Disposition);
                return result;
            });

        Assert.Equal(
            [
                MaterializationProgressMutationDisposition.Applied,
                MaterializationProgressMutationDisposition.Applied
            ],
            dispositions);
        Assert.Equal(parentDelivery!.Id, childDelivery!.Id);
        Assert.Equal(parentDelivery.Change.Id, childDelivery.Change.Id);
        Assert.NotEqual(parentPosition, childPosition);
        Assert.Equal(1, parentProcessor.CheckpointAttemptCount);
        Assert.Equal(0, parentProcessor.CheckpointCount);
        Assert.Equal(1, childProcessor.CheckpointCount);
    }

    static async Task<ManagedChangeSourceDuplicateReplayObservation> ObserveDuplicateReplayAsync()
    {
        var document = EntityDocument("load-a", partitionKey: "tenant-a", etag: "revision-a");
        FakeManagedProcessor processor = new();
        processor.AddBatch(document, feedRangeJson: "range/a", continuationToken: "position/10");
        processor.AddBatch(document, feedRangeJson: "range/a", continuationToken: "position/10");
        RecordingSettlementObserver observer = new();
        var fixture = CreateEntityFixture(processor: processor, observer: observer);
        List<MaterializationProgressMutationDisposition> dispositions = [];
        List<MaterializationDeliveryId> deliveries = [];
        List<MaterializationChangeId> changes = [];

        await fixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                var delivery = Assert.Single(page.Deliveries);
                deliveries.Add(delivery.Id);
                changes.Add(delivery.Change.Id);
                var disposition = processor.CheckpointCount == 0
                    ? MaterializationProgressMutationDisposition.Applied
                    : MaterializationProgressMutationDisposition.Replayed;
                dispositions.Add(disposition);
                return ValueTask.FromResult(Success(
                    progress: progress,
                    page: page,
                    checkpointId: "checkpoint/stable",
                    disposition: disposition));
            });

        return new(
            ApplicationDispositions: [.. dispositions],
            Deliveries: [.. deliveries],
            Changes: [.. changes],
            ProviderSettlements: processor.CheckpointCount,
            SettlementIds: [.. observer.Observations.Select(static observation => observation.Settlement.Id)]);
    }

    static async Task<ManagedChangeSourceCrashAfterSettlementObservation> ObserveCrashAfterSettlementAsync()
    {
        FakeManagedProcessor processor = new();
        processor.AddBatch(EntityDocument("load-a", partitionKey: "tenant-a"));
        ThrowingSettlementObserver observer = new();
        var fixture = CreateEntityFixture(processor: processor, observer: observer);
        var handlerCalls = 0;
        var runCompleted = false;

        await fixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Success(progress, page, "checkpoint/observer-failure"));
            });

        await fixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Success(progress, page, "checkpoint/unexpected-redelivery"));
            });
        runCompleted = true;

        return new(
            RunCompleted: runCompleted,
            HandlerInvocations: handlerCalls,
            ProviderSettlements: processor.CheckpointCount,
            PostSettlementObservationAttempts: observer.Calls);
    }

    [Fact]
    public async Task FullyFilteredCallback_DurablyAdvancesOneFeedRangeProgressPage()
    {
        FakeManagedProcessor processor = new();
        processor.AddBatch(
            EntityDocument("other-shape", partitionKey: "tenant-a", observationType: "Other"),
            feedRangeJson: "range/filtered",
            continuationToken: "position/filtered");
        RecordingSettlementObserver observer = new();
        var fixture = CreateEntityFixture(processor: processor, observer: observer);
        List<(MaterializationProgressKey Progress, MaterializationChangePage Page)> calls = [];

        await fixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                calls.Add((progress, page));
                return ValueTask.FromResult(Success(progress, page, "checkpoint/filtered"));
            });

        var call = Assert.Single(calls);
        Assert.Empty(call.Page.Deliveries);
        Assert.Equal(MaterializationChangePageState.Progressed, call.Page.State);
        Assert.Contains("filtered-provider-range", call.Progress.Scope.Partition.Value, StringComparison.Ordinal);
        Assert.Equal(1, processor.CheckpointCount);
        Assert.Single(observer.Observations);
    }

    [Fact]
    public async Task ObserveLag_ProjectsEstimatedAndUnavailableProviderWorkWithoutSdkState()
    {
        FakeManagedProcessor processor = new();
        processor.Lag.Add(new(42, "tests/cosmos-lag/estimated"));
        processor.Lag.Add(new(null, "tests/cosmos-lag/unavailable"));
        var fixture = CreateEntityFixture(processor: processor);

        var observations = await CollectAsync(fixture.Source.ObserveLagAsync(Context(), ManagedRequest));

        Assert.Equal(2, observations.Count);
        Assert.All(observations, observation =>
        {
            Assert.Equal(ManagedRequest, observation.Request);
            Assert.Equal(fixture.Reader.Descriptor.Source, observation.Source);
            Assert.Null(observation.Scope);
            Assert.DoesNotContain("Microsoft.Azure.Cosmos", observation.GetType().AssemblyQualifiedName, StringComparison.Ordinal);
        });
        Assert.Equal(MaterializationChangeLagEstimateState.Estimated, observations[0].EstimateState);
        Assert.Equal(42, observations[0].EstimatedPendingProviderWork);
        Assert.Equal(MaterializationChangeLagEstimateState.Unavailable, observations[1].EstimateState);
        Assert.Null(observations[1].EstimatedPendingProviderWork);
    }

    [Fact]
    public void CapabilityProfile_TruthfullyDeclaresLatestVersionUpsertsAndExplicitSettlement()
    {
        var fixture = CreateEntityFixture(processor: new());

        var delivery = Assert.Single(fixture.Source.Descriptor.CapabilityProfile.Evidence, static evidence =>
            evidence.Capability == MaterializationCapabilityKind.SourceChangeDelivery);
        Assert.Equal(CapabilityRealizationKind.Constrained, delivery.Realization);
        Assert.Equal(3, delivery.Guarantees.Length);
        Assert.Contains(MaterializationGuaranteeKind.StableOrdering, delivery.Guarantees);
        Assert.Contains(MaterializationGuaranteeKind.AtLeastOnceDelivery, delivery.Guarantees);
        Assert.Contains(MaterializationGuaranteeKind.LatestVersionUpsertDelivery, delivery.Guarantees);
        Assert.DoesNotContain(MaterializationGuaranteeKind.CompleteMutationDelivery, delivery.Guarantees);
        Assert.DoesNotContain(MaterializationGuaranteeKind.BeforeImage, delivery.Guarantees);
        Assert.DoesNotContain(MaterializationGuaranteeKind.BaselinePlusCatchUp, delivery.Guarantees);
        Assert.Empty(delivery.OperatingLimits);
        Assert.Contains(
            string.Concat(
                "cosmos-managed-processor-namespace/",
                Uri.EscapeDataString(fixture.Source.ProcessorNamespace)),
            delivery.SourceReferences);
        Assert.Contains("cosmos-managed-initial-position/0", delivery.SourceReferences);
        Assert.Contains("cosmos-managed-initial-time/none", delivery.SourceReferences);

        var settlement = Assert.Single(fixture.Source.Descriptor.CapabilityProfile.Evidence, static evidence =>
            evidence.Capability == MaterializationCapabilityKind.SourceSettlement);
        Assert.Single(settlement.Guarantees);
        Assert.Contains(MaterializationGuaranteeKind.ExplicitSettlement, settlement.Guarantees);
        Assert.Empty(settlement.OperatingLimits);
    }

    [Fact]
    public async Task ProcessorDeploymentIdentity_IsBindingAndRequestSpecificButExcludesEphemeralOwnership()
    {
        var firstEntity = CreateEntityFixture(
            processor: new(),
            processorName: "processor/shared-seed",
            instanceName: "worker/a");
        var transferredEntity = CreateEntityFixture(
            processor: new(),
            processorName: "processor/shared-seed",
            instanceName: "worker/b");
        var outbox = CreateOutboxFixture(
            processor: new(),
            streamName: "stream/loads",
            processorName: "processor/shared-seed",
            instanceName: "worker/c");
        var changedSeed = CreateEntityFixture(
            processor: new(),
            processorName: "processor/changed-seed",
            instanceName: "worker/a");
        var changedPolicy = CreateEntityFixture(
            processor: new(),
            processorName: "processor/shared-seed",
            instanceName: "worker/a",
            pollInterval: TimeSpan.FromSeconds(2));
        var changedLeaseStore = CreateEntityFixture(
            processor: new(),
            processorName: "processor/shared-seed",
            instanceName: "worker/a",
            leaseStoreIdentity: "tests/lease-store/alternate");
        var changedInitialBoundary = CreateEntityFixture(
            processor: new(),
            processorName: "processor/shared-seed",
            instanceName: "worker/a",
            initialPosition: CosmosManagedMaterializationInitialPosition.Current);
        MaterializationManagedChangeRequest changedMaterialization = new(
            new("tests/other-materialization"),
            ManagedRequest.DefinitionFingerprint,
            ManagedRequest.Generation);
        MaterializationManagedChangeRequest changedDefinition = new(
            ManagedRequest.Materialization,
            new ExecutionDefinitionFingerprint(
                "sha256",
                "execution-definition/v1",
                "fedcba9876543210"),
            ManagedRequest.Generation);
        MaterializationManagedChangeRequest changedGeneration = new(
            ManagedRequest.Materialization,
            ManagedRequest.DefinitionFingerprint,
            new("generation-2"));

        Assert.Equal(
            firstEntity.Source.ProcessorNamespace,
            transferredEntity.Source.ProcessorNamespace);
        Assert.NotEqual(firstEntity.Source.ProcessorNamespace, outbox.Source.ProcessorNamespace);
        Assert.NotEqual(firstEntity.Source.ProcessorNamespace, changedSeed.Source.ProcessorNamespace);
        Assert.Equal(firstEntity.Source.ProcessorNamespace, changedPolicy.Source.ProcessorNamespace);
        Assert.NotEqual(firstEntity.Source.ProcessorNamespace, changedLeaseStore.Source.ProcessorNamespace);
        Assert.NotEqual(firstEntity.Source.ProcessorNamespace, changedInitialBoundary.Source.ProcessorNamespace);
        Assert.Equal(
            firstEntity.Source.GetEffectiveProcessorName(ManagedRequest),
            transferredEntity.Source.GetEffectiveProcessorName(ManagedRequest));
        Assert.NotEqual(
            firstEntity.Source.GetEffectiveProcessorName(ManagedRequest),
            outbox.Source.GetEffectiveProcessorName(ManagedRequest));
        Assert.NotEqual(
            firstEntity.Source.GetEffectiveProcessorName(ManagedRequest),
            changedLeaseStore.Source.GetEffectiveProcessorName(ManagedRequest));
        Assert.NotEqual(
            firstEntity.Source.GetEffectiveProcessorName(ManagedRequest),
            changedInitialBoundary.Source.GetEffectiveProcessorName(ManagedRequest));
        Assert.NotEqual(
            firstEntity.Source.GetEffectiveProcessorName(ManagedRequest),
            firstEntity.Source.GetEffectiveProcessorName(changedMaterialization));
        Assert.NotEqual(
            firstEntity.Source.GetEffectiveProcessorName(ManagedRequest),
            firstEntity.Source.GetEffectiveProcessorName(changedDefinition));
        Assert.NotEqual(
            firstEntity.Source.GetEffectiveProcessorName(ManagedRequest),
            firstEntity.Source.GetEffectiveProcessorName(changedGeneration));
        Assert.Equal(
            firstEntity.Source.Descriptor.CapabilityProfile.Id,
            transferredEntity.Source.Descriptor.CapabilityProfile.Id);
        Assert.NotEqual(
            firstEntity.Source.Descriptor.CapabilityProfile.Id,
            changedSeed.Source.Descriptor.CapabilityProfile.Id);
        Assert.NotEqual(
            firstEntity.Source.Descriptor.CapabilityProfile.Id,
            changedPolicy.Source.Descriptor.CapabilityProfile.Id);
        Assert.NotEqual(
            firstEntity.Source.Descriptor.CapabilityProfile.Id,
            changedLeaseStore.Source.Descriptor.CapabilityProfile.Id);
        Assert.NotEqual(
            firstEntity.Source.Descriptor.CapabilityProfile.Id,
            changedInitialBoundary.Source.Descriptor.CapabilityProfile.Id);

        FakeManagedProcessor recordingProcessor = new();
        recordingProcessor.AddBatch(EntityDocument("load-a", partitionKey: "tenant-a"));
        var recording = CreateEntityFixture(processor: recordingProcessor);
        await recording.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) => ValueTask.FromResult(Success(
                progress: progress,
                page: page,
                checkpointId: "checkpoint/request-name")));

        Assert.Equal(
            [recording.Source.GetEffectiveProcessorName(ManagedRequest)],
            recordingProcessor.RequestedProcessorNames);
    }

    [Fact]
    public void NonCanonicalPartitionSelector_IsRejectedBeforeManagedProcessorBinding()
    {
        FakeManagedProcessor processor = new();

        var exception = Assert.Throws<ArgumentException>(() => CreateEntityFixture(
            processor: processor,
            fixedPartition: true,
            partitionSourceSelector: "tenantId"));

        Assert.Contains("partitionKey", exception.Message, StringComparison.Ordinal);
        Assert.Empty(processor.RequestedProcessorNames);
    }

    [Theory]
    [InlineData("https://tests.invalid")]
    [InlineData("https://regional.tests.invalid")]
    public void ProductionSource_RejectsLeaseStoreWithMonitoredDatabaseAndContainerNames(
        string leaseAccountEndpoint)
    {
        var fixture = CreateEntityFixture(processor: new());
        using CosmosClient monitoredClient = new(
            accountEndpoint: "https://tests.invalid",
            authKeyOrResourceToken: TestMasterKey,
            clientOptions: new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        using CosmosClient leaseClient = new(
            accountEndpoint: leaseAccountEndpoint,
            authKeyOrResourceToken: TestMasterKey,
            clientOptions: new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var monitoredContainer = monitoredClient.GetContainer(
            databaseId: "operations",
            containerId: "observations");
        var samePhysicalLeaseContainer = leaseClient.GetContainer(
            databaseId: "operations",
            containerId: "observations");
        CosmosManagedMaterializationChangeBinding binding = new(
            kind: CosmosManagedMaterializationDocumentKind.Entity,
            documentKind: CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
            persistedObservationType: EntityShape);
        CosmosManagedMaterializationChangeSourcePolicy policy = new(
            processorName: "processor/loads",
            instanceName: "worker-a");

        var exception = Assert.Throws<ArgumentException>(() => new CosmosManagedMaterializationChangeSource(
            reader: fixture.Reader,
            physicalPlan: PhysicalPlan,
            placement: fixture.Placement,
            monitoredContainer: monitoredContainer,
            leaseContainer: samePhysicalLeaseContainer,
            binding: binding,
            policy: policy,
            authenticationKey: AuthenticationKey));

        Assert.Equal("leaseContainer", exception.ParamName);
    }

    [Fact]
    public async Task StreamFilterAndOutboxProjection_PreserveMessageIdentityMetadataAndPayload()
    {
        FakeManagedProcessor processor = new();
        processor.AddBatch(
            ImmutableArray.Create(
                OutboxDocument(
                    messageId: "message/filtered",
                    streamName: "stream/other",
                    partitionKey: "tenant-a"),
                OutboxDocument(
                    messageId: "message/accepted",
                    streamName: "stream/loads",
                    partitionKey: "tenant-a")),
            feedRangeJson: "range/outbox",
            continuationToken: "position/outbox");
        var fixture = CreateOutboxFixture(processor: processor, streamName: "stream/loads");
        MaterializationChangeDelivery? projected = null;

        await fixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                projected = Assert.Single(page.Deliveries);
                return ValueTask.FromResult(Success(progress, page, "checkpoint/outbox"));
            });

        var delivery = Assert.IsType<MaterializationChangeDelivery>(projected);
        Assert.Equal("message/accepted", delivery.Change.SubjectIdentity);
        Assert.Equal(MaterializationChangeKind.Upsert, delivery.Change.Kind);
        Assert.Null(delivery.Change.Before);
        var observation = Assert.IsType<RelationQuerySourceReadObservation>(delivery.Change.After);
        Assert.Equal("message/accepted", observation.Identity);
        Assert.Equal(OutboxShape, observation.Shape);
        AssertField(observation, "messageId", ObservationValue.FromString("message/accepted"));
        AssertField(observation, "streamName", ObservationValue.FromString("stream/loads"));
        AssertField(observation, "subjectType", ObservationValue.FromString(EntityShape.ShapeId.Value));
        AssertField(observation, "subjectId", ObservationValue.FromString("load-a"));
        AssertField(observation, "subjectVersion", ObservationValue.FromInt64(7));
        AssertField(observation, "correlationId", ObservationValue.FromString("correlation/123"));
        AssertField(observation, "occurredAtUtc", ObservationValue.FromDateTimeOffset(ObservedAtUtc.AddMinutes(-1)));
        AssertField(observation, "traceId", ObservationValue.FromString("trace/123"));
        AssertField(observation, "spanId", ObservationValue.FromString("span/456"));
        AssertField(observation, "etag", ObservationValue.FromString("outbox-revision"));
        AssertField(observation, "payloadName", ObservationValue.FromString("Load A"));
    }

    [Fact]
    public async Task OutboxReader_FiltersPersistedEntityTypeWhileProjectingDistinctMessageShape()
    {
        var fixture = CreateOutboxFixture(processor: new(), streamName: "stream/loads");
        RelationQuerySourceReadRequest request = new(
            physicalPlan: PhysicalPlan,
            stage: new("read/outbox"),
            placementBinding: fixture.Placement.Id,
            source: fixture.Reader.Descriptor.Source,
            shape: OutboxShape,
            identitySelector: fixture.Reader.IdentitySourceSelector,
            fields:
            [
                .. fixture.Placement.Fields.Select(static field => new RelationQuerySourceReadField(
                    input: field.Input,
                    semanticPath: field.SemanticPath,
                    sourceSelector: field.SourceSelector,
                    purpose: RelationQuerySourceReadFieldPurpose.SemanticInput))
            ],
            constraint: new RelationQueryBoundedEnumeration(maximumRows: 10),
            maximumBufferedRows: 10);

        await fixture.Reader.ReadAsync(request);

        var query = Assert.Single(fixture.BaselineFeed.Queries);
        var parameterValues = query.GetQueryParameters()
            .Select(static parameter => parameter.Value)
            .OfType<string>()
            .ToArray();
        Assert.Equal(OutboxShape, fixture.Reader.Shape);
        Assert.Equal(EntityShape, fixture.Reader.PersistedObservationType);
        Assert.Equal("stream/loads", fixture.Reader.PersistedStreamName);
        Assert.Contains(EntityShape.ShapeId.Value, parameterValues);
        Assert.DoesNotContain(OutboxShape.ShapeId.Value, parameterValues);
        Assert.Contains("stream/loads", parameterValues);
        Assert.DoesNotContain("stream/other", parameterValues);
        Assert.Contains("c[\"streamName\"]", query.QueryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAndManagedPositions_AreNotInterchangeable()
    {
        FakeManagedProcessor managedProcessor = new();
        managedProcessor.AddBatch(
            EntityDocument("load-a", partitionKey: "tenant-a"),
            feedRangeJson: "range/a",
            continuationToken: "position/managed");
        var fixture = CreateEntityFixture(processor: managedProcessor);
        MaterializationSourcePosition? managedPosition = null;
        await fixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                managedPosition = page.ThroughPosition;
                return ValueTask.FromResult(Success(progress, page, "checkpoint/managed"));
            });

        using CosmosMaterializationAdmissionIndex admission = new();
        FakePullChangeFeedReader pullReader = new();
        var pullFixture = CreateEntityFixture(processor: new(), fixedPartition: true);
        CosmosMaterializationSource pull = CreatePullSource(
            reader: pullFixture.Reader,
            placement: pullFixture.Placement,
            changeFeedReader: pullReader,
            admissionIndex: admission);
        var pullPosition = await pull.CaptureCurrentPositionAsync(Context(), pull.Scope);

        Assert.Throws<ArgumentException>(() => fixture.Source.DecodeProviderContinuation(pullPosition));
        await Assert.ThrowsAsync<ArgumentException>(() => pull.ReadChangesAsync(
            Context(),
            new MaterializationChangeReadRequest(
                scope: managedPosition!.Scope,
                afterPosition: managedPosition,
                maximumDeliveries: 10,
                maximumBytes: 1_000_000)).AsTask());
        Assert.Single(pullReader.Calls);

        var outbox = CreateOutboxFixture(processor: new(), streamName: "stream/loads");
        Assert.Throws<ArgumentException>(() => CreatePullSource(
            reader: outbox.Reader,
            placement: outbox.Placement,
            changeFeedReader: new FakePullChangeFeedReader(),
            admissionIndex: admission));
    }

    static async Task<MaterializationChangeDelivery> ReadSingleDeliveryAsync(
        CosmosObservationContainerDocument document,
        string feedRangeJson,
        string continuationToken,
        string processorName,
        string instanceName,
        TimeSpan? pollInterval = null,
        string leaseStoreIdentity = DefaultLeaseStoreIdentity)
    {
        FakeManagedProcessor processor = new();
        processor.AddBatch(document, feedRangeJson, continuationToken);
        var fixture = CreateEntityFixture(
            processor: processor,
            processorName: processorName,
            instanceName: instanceName,
            pollInterval: pollInterval,
            leaseStoreIdentity: leaseStoreIdentity);
        MaterializationChangeDelivery? delivery = null;
        await fixture.Source.RunAsync(
            Context(),
            ManagedRequest,
            (_, progress, page) =>
            {
                delivery = Assert.Single(page.Deliveries);
                return ValueTask.FromResult(Success(progress, page, "checkpoint/read-single"));
            });
        return Assert.IsType<MaterializationChangeDelivery>(delivery);
    }

    static ManagedFixture CreateEntityFixture(
        FakeManagedProcessor processor,
        ICosmosManagedMaterializationChangeSourceObserver? observer = null,
        string processorName = "processor/loads",
        string instanceName = "worker-a",
        bool fixedPartition = false,
        TimeSpan? pollInterval = null,
        string leaseStoreIdentity = DefaultLeaseStoreIdentity,
        CosmosManagedMaterializationInitialPosition initialPosition =
            CosmosManagedMaterializationInitialPosition.Beginning,
        DateTimeOffset? initialTimeUtc = null,
        string partitionSourceSelector =
            CosmosRelationQuerySourceReader.ObservationPartitionSourceSelector)
    {
        var namePath = FieldPath.FromField("name");
        RelationQuerySourceFieldBinding name = new(
            input: new("field/name"),
            semanticPath: namePath,
            sourceSelector: CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(namePath));
        return CreateFixture(
            shape: EntityShape,
            processor: processor,
            binding: new(
                kind: CosmosManagedMaterializationDocumentKind.Entity,
                documentKind: CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                persistedObservationType: EntityShape),
            identitySourceSelector: CosmosRelationQuerySourceReader.ObservationIdentitySourceSelector,
            fields: [name],
            fieldSourceSelector: CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector,
            processorName: processorName,
            instanceName: instanceName,
            observer: observer,
            fixedPartition: fixedPartition,
            pollInterval: pollInterval,
            leaseStoreIdentity: leaseStoreIdentity,
            initialPosition: initialPosition,
            initialTimeUtc: initialTimeUtc,
            partitionSourceSelector: partitionSourceSelector);
    }

    static ManagedFixture CreateOutboxFixture(
        FakeManagedProcessor processor,
        string? streamName,
        string processorName = "processor/outbox",
        string instanceName = "worker/outbox")
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["messageId"] = "id",
            ["streamName"] = "streamName",
            ["subjectType"] = "subjectType",
            ["subjectId"] = "subjectId",
            ["subjectVersion"] = "subjectVersion",
            ["correlationId"] = "correlationId",
            ["occurredAtUtc"] = "occurredAtUtc",
            ["traceId"] = "traceId",
            ["spanId"] = "spanId",
            ["etag"] = "_etag",
            ["payloadName"] = "observation.name"
        };
        RelationQueryPlacementFieldSelector selector = path => mappings[path.ToString()];
        var fields = mappings.Keys.Select(field =>
        {
            var path = FieldPath.FromField(field);
            return new RelationQuerySourceFieldBinding(
                input: new(string.Concat("field/", field)),
                semanticPath: path,
                sourceSelector: selector(path));
        }).ToImmutableArray();
        return CreateFixture(
            shape: OutboxShape,
            processor: processor,
            binding: new(
                kind: CosmosManagedMaterializationDocumentKind.Outbox,
                documentKind: CosmosObservationOutboxRepositoryOptions.DefaultOutboxDocumentKind,
                persistedObservationType: EntityShape,
                streamName: streamName),
            identitySourceSelector: "id",
            fields: fields,
            fieldSourceSelector: selector,
            processorName: processorName,
            instanceName: instanceName,
            observer: null,
            entityDocumentKind: CosmosObservationOutboxRepositoryOptions.DefaultOutboxDocumentKind);
    }

    static ManagedFixture CreateFixture(
        QualifiedShapeId shape,
        FakeManagedProcessor processor,
        CosmosManagedMaterializationChangeBinding binding,
        string identitySourceSelector,
        ImmutableArray<RelationQuerySourceFieldBinding> fields,
        RelationQueryPlacementFieldSelector fieldSourceSelector,
        string processorName,
        string instanceName,
        ICosmosManagedMaterializationChangeSourceObserver? observer,
        string? entityDocumentKind = null,
        bool fixedPartition = false,
        TimeSpan? pollInterval = null,
        string leaseStoreIdentity = DefaultLeaseStoreIdentity,
        CosmosManagedMaterializationInitialPosition initialPosition =
            CosmosManagedMaterializationInitialPosition.Beginning,
        DateTimeOffset? initialTimeUtc = null,
        string partitionSourceSelector =
            CosmosRelationQuerySourceReader.ObservationPartitionSourceSelector)
    {
        CosmosRelationQuerySourcePolicy queryPolicy = new(
            partitionSourceSelector: partitionSourceSelector,
            crossPartitionPolicy: fixedPartition
                ? CosmosRelationQueryCrossPartitionPolicy.Prohibit
                : CosmosRelationQueryCrossPartitionPolicy.AllowBoundedQueries,
            fixedPartitionKey: fixedPartition ? new PartitionKey("tenant-a") : null,
            maximumEnumerationRows: 100,
            maximumSdkPageSize: 10,
            readConsistencyLevel: ConsistencyLevel.Strong);
        var limits = queryPolicy.GetEffectivePlacementLimits(CosmosRelationQuerySourceReader.DefaultLimits);
        RelationQuerySourceInstance relationSource = new(
            id: new("source/tests/cosmos-managed"),
            executionDomain: new("domain/tests/cosmos-managed"),
            targetProfile: CosmosRelationQuerySourceReader.TargetProfile,
            limits: limits);
        CapturingBaselineFeed baselineFeed = new();
        CosmosJsonQueryFeedReader queryFeed = new(
            accountEndpoint: new Uri("https://tests.invalid"),
            databaseName: "operations",
            containerName: "observations",
            iteratorFactory: baselineFeed.Create);
        CosmosRelationQuerySourceReader reader = new(
            shape: shape,
            source: relationSource,
            feedReader: queryFeed,
            accountEndpoint: "https://tests.invalid",
            databaseId: "operations",
            containerId: "observations",
            policy: queryPolicy,
            identitySourceSelector: identitySourceSelector,
            fieldSourceSelector: fieldSourceSelector,
            entityDocumentKind: entityDocumentKind,
            clientConsistencyLevel: ConsistencyLevel.Strong,
            persistedObservationType: binding.PersistedObservationType,
            persistedStreamName: binding.StreamName);
        RelationQuerySourcePlacementBinding placement = new(
            id: new("placement/source"),
            input: new("source/items"),
            node: new("node/source"),
            binding: new("binding/source"),
            shape: shape,
            source: relationSource.Id,
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new(shape, identitySourceSelector),
            fields: fields,
            partition: new(partitionSourceSelector));
        CosmosManagedMaterializationChangeSourcePolicy sourcePolicy = new(
            processorName: processorName,
            instanceName: instanceName,
            initialPosition: initialPosition,
            initialTimeUtc: initialTimeUtc,
            pollInterval: pollInterval,
            maximumProviderPageItems: 10,
            maximumLagStateItems: 10);
        CosmosManagedMaterializationChangeSource source = new(
            reader: reader,
            physicalPlan: PhysicalPlan,
            placement: placement,
            binding: binding,
            policy: sourcePolicy,
            processorFactory: effectiveProcessorName =>
            {
                processor.RequestedProcessorNames.Add(effectiveProcessorName);
                return processor;
            },
            authenticationKey: AuthenticationKey,
            observer: observer,
            leaseStoreIdentity: leaseStoreIdentity);
        return new(source, reader, placement, baselineFeed);
    }

    static CosmosMaterializationSource CreatePullSource(
        CosmosRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        ICosmosMaterializationChangeFeedReader changeFeedReader,
        CosmosMaterializationAdmissionIndex admissionIndex)
    {
        CosmosMaterializationSourcePolicy policy = new(
            fullFidelityRetention: TimeSpan.FromHours(12),
            continuousBackupEvidenceReference: "tests/retention",
            previousImageEvidenceReference: "tests/previous-images",
            strongConsistencyEvidenceReference: "tests/strong-consistency",
            maximumScanPageItems: 10,
            maximumScanPageBytes: 1_000_000,
            maximumChangePageItems: 10,
            maximumChangePageBytes: 1_000_000,
            maximumProviderPageItems: 10);
        return new(
            reader: reader,
            physicalPlan: PhysicalPlan,
            placement: placement,
            policy: policy,
            admissionIndex: admissionIndex,
            changeFeedReader: changeFeedReader,
            authenticationKey: AuthenticationKey);
    }

    static CosmosObservationContainerDocument EntityDocument(
        string identity,
        string partitionKey,
        string? observationType = null,
        string etag = "entity-revision") => new(
        Id: string.Concat("entity/", identity),
        PartitionKey: partitionKey,
        DocumentKind: CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
        ObservationType: observationType ?? EntityShape.ShapeId.Value,
        ObservationId: identity,
        ObservationVersion: 1,
        Observation: new(StringComparer.Ordinal)
        {
            ["name"] = ObservationValue.FromString(string.Concat("Name ", identity))
        },
        ETag: etag);

    static CosmosObservationContainerDocument OutboxDocument(
        string messageId,
        string streamName,
        string partitionKey) => new(
        Id: messageId,
        PartitionKey: partitionKey,
        DocumentKind: CosmosObservationOutboxRepositoryOptions.DefaultOutboxDocumentKind,
        ObservationType: EntityShape.ShapeId.Value,
        ObservationId: "load-a",
        ObservationVersion: 7,
        Observation: new(StringComparer.Ordinal)
        {
            ["name"] = ObservationValue.FromString("Load A")
        },
        StreamName: streamName,
        SubjectType: EntityShape.ShapeId.Value,
        SubjectId: "load-a",
        SubjectVersion: 7,
        CorrelationId: "correlation/123",
        OccurredAtUtc: ObservedAtUtc.AddMinutes(-1),
        TraceId: "trace/123",
        SpanId: "span/456",
        ETag: "outbox-revision");

    static MaterializationProgressMutationResult Success(
        MaterializationProgressKey progress,
        MaterializationChangePage page,
        string checkpointId,
        MaterializationProgressMutationDisposition disposition = MaterializationProgressMutationDisposition.Applied,
        MaterializationSourcePosition? position = null)
    {
        var checkpointPosition = position ?? page.ThroughPosition;
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new(checkpointId),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: checkpointPosition,
            appliedDeliveries: [.. page.Deliveries.Select(static delivery => delivery.Id)],
            committedAtUtc: ObservedAtUtc,
            evidenceReference: "tests/application-checkpoint",
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(checkpointPosition));
        MaterializationProgressSnapshot snapshot = new(
            key: progress,
            revision: MaterializationProgressRevision.Initial,
            fence: MaterializationProgressFence.Initial,
            fenceOwner: "worker/application",
            latestChangeCheckpoint: checkpoint);
        return new(disposition, snapshot);
    }

    static void AssertField(
        RelationQuerySourceReadObservation observation,
        string field,
        ObservationValue expected)
    {
        var result = Assert.Single(observation.Fields, item =>
            item.Field.SemanticPath == FieldPath.FromField(field));
        Assert.Equal(RelationQuerySourceReadFieldState.Value, result.State);
        Assert.Equal(expected, result.Value);
    }

    static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        List<T> results = [];
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }

    static OperationContext Context(CancellationToken cancellationToken = default) =>
        OperationContext.Create(
            timeProvider: new FixedTimeProvider(ObservedAtUtc),
            cancellationToken: cancellationToken);

    sealed record ManagedFixture(
        CosmosManagedMaterializationChangeSource Source,
        CosmosRelationQuerySourceReader Reader,
        RelationQuerySourcePlacementBinding Placement,
        CapturingBaselineFeed BaselineFeed);

    sealed class FakeManagedProcessor : ICosmosManagedMaterializationChangeFeedProcessor
    {
        readonly List<ScheduledBatch> batches = [];

        internal List<CosmosManagedMaterializationProviderLag> Lag { get; } = [];

        internal List<string> RequestedProcessorNames { get; } = [];

        internal Exception? CheckpointFailure { get; init; }

        internal Func<Task>? CheckpointOperation { get; init; }

        internal bool ContinueAfterCallbackCancellation { get; init; }

        internal int CheckpointAttemptCount { get; private set; }

        internal int CheckpointCount { get; private set; }

        internal void AddBatch(
            CosmosObservationContainerDocument document,
            string feedRangeJson = "range/default",
            string continuationToken = "position/default",
            CancellationToken callbackCancellationToken = default) =>
            AddBatch(
                documents: [document],
                feedRangeJson: feedRangeJson,
                continuationToken: continuationToken,
                callbackCancellationToken: callbackCancellationToken);

        internal void AddBatch(
            ImmutableArray<CosmosObservationContainerDocument> documents,
            string feedRangeJson,
            string continuationToken,
            CancellationToken callbackCancellationToken = default)
        {
            batches.Add(new(
                owner: this,
                documents: documents,
                feedRangeJson: feedRangeJson,
                continuationToken: continuationToken,
                callbackCancellationToken: callbackCancellationToken));
        }

        public async Task RunAsync(
            Func<CosmosManagedMaterializationProviderBatch, CancellationToken, Task> handler,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(handler);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var scheduled in batches)
            {
                if (scheduled.Settled)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var callbackCancellationToken = scheduled.CallbackCancellationToken.CanBeCanceled
                    ? scheduled.CallbackCancellationToken
                    : cancellationToken;
                try
                {
                    await handler(scheduled.Batch, callbackCancellationToken);
                }
                catch (OperationCanceledException)
                    when (ContinueAfterCallbackCancellation
                        && callbackCancellationToken.IsCancellationRequested
                        && !cancellationToken.IsCancellationRequested)
                {
                    // The Cosmos SDK may transfer a lost lease and continue the processor deployment.
                }
            }
        }

        public async IAsyncEnumerable<CosmosManagedMaterializationProviderLag> ObserveLagAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var lag in Lag)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return lag;
                await Task.Yield();
            }
        }

        async Task CheckpointAsync(ScheduledBatch scheduled)
        {
            CheckpointAttemptCount++;
            if (CheckpointFailure is not null)
            {
                throw CheckpointFailure;
            }
            if (CheckpointOperation is not null)
            {
                await CheckpointOperation();
            }

            scheduled.MarkSettled();
            CheckpointCount++;
        }

        sealed class ScheduledBatch
        {
            internal ScheduledBatch(
                FakeManagedProcessor owner,
                ImmutableArray<CosmosObservationContainerDocument> documents,
                string feedRangeJson,
                string continuationToken,
                CancellationToken callbackCancellationToken)
            {
                CallbackCancellationToken = callbackCancellationToken;
                Batch = new(
                    FeedRangeJson: feedRangeJson,
                    ContinuationToken: continuationToken,
                    Documents: documents,
                    CheckpointAsync: () => owner.CheckpointAsync(this));
            }

            internal CosmosManagedMaterializationProviderBatch Batch { get; }

            internal CancellationToken CallbackCancellationToken { get; }

            internal bool Settled { get; private set; }

            internal void MarkSettled() => Settled = true;
        }
    }

    sealed class RecordingSettlementObserver : ICosmosManagedMaterializationChangeSourceObserver
    {
        internal List<MaterializationChangeSettlementObservation> Observations { get; } = [];

        public void Observe(MaterializationChangeSettlementObservation observation) =>
            Observations.Add(observation);
    }

    sealed class ThrowingSettlementObserver : ICosmosManagedMaterializationChangeSourceObserver
    {
        internal int Calls { get; private set; }

        public void Observe(MaterializationChangeSettlementObservation observation)
        {
            Calls++;
            throw new TestObserverException("observer failed after source settlement");
        }
    }

    sealed class DurableCheckpointHandler
    {
        const string Owner = "worker/durable-application";
        readonly InMemoryMaterializationProgressStore store = new();
        readonly Dictionary<MaterializationProgressKey, DurableCheckpointState> states = [];

        internal async ValueTask<MaterializationProgressMutationResult> ApplyAsync(
            OperationContext context,
            MaterializationProgressKey progress,
            MaterializationChangePage page)
        {
            if (!states.TryGetValue(progress, out var state))
            {
                var stateNumber = states.Count + 1;
                var claim = Assert.IsType<MaterializationProgressSnapshot>((await store.AcquireFenceAsync(
                    context: context,
                    key: progress,
                    mutationId: new(string.Concat("claim/", stateNumber)),
                    expectedRevision: null,
                    owner: Owner)).Snapshot);
                state = new(
                    Claim: claim,
                    StateNumber: stateNumber);
                states.Add(progress, state);
            }

            if (!state.Attempts.TryGetValue(page.ThroughPosition, out var attempt))
            {
                var attemptNumber = state.Attempts.Count + 1;
                attempt = new(
                    ExpectedRevision: state.CurrentRevision,
                    CheckpointId: new(string.Concat("checkpoint/", state.StateNumber, "/", attemptNumber)),
                    MutationId: new(string.Concat("checkpoint-mutation/", state.StateNumber, "/", attemptNumber)));
                state.Attempts.Add(page.ThroughPosition, attempt);
            }

            MaterializationApplicationCheckpoint checkpoint = new(
                id: attempt.CheckpointId,
                kind: MaterializationCheckpointKind.ChangeProgress,
                continuation: null,
                completion: null,
                position: page.ThroughPosition,
                appliedDeliveries: [.. page.Deliveries.Select(static delivery => delivery.Id)],
                committedAtUtc: ObservedAtUtc,
                evidenceReference: "tests/durable-application-checkpoint",
                channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(page.ThroughPosition));
            var result = await store.SaveCheckpointAsync(
                context: context,
                key: progress,
                mutationId: attempt.MutationId,
                expectedRevision: attempt.ExpectedRevision,
                owner: Owner,
                fence: state.Claim.Fence,
                checkpoint: checkpoint);
            if (result.Disposition is MaterializationProgressMutationDisposition.Applied
                or MaterializationProgressMutationDisposition.Replayed)
            {
                state.CurrentRevision = Assert.IsType<MaterializationProgressSnapshot>(result.Snapshot).Revision;
            }

            return result;
        }

        sealed class DurableCheckpointState(MaterializationProgressSnapshot Claim, int StateNumber)
        {
            internal MaterializationProgressSnapshot Claim { get; } = Claim;

            internal int StateNumber { get; } = StateNumber;

            internal MaterializationProgressRevision CurrentRevision { get; set; } = Claim.Revision;

            internal Dictionary<MaterializationSourcePosition, DurableCheckpointAttempt> Attempts { get; } = [];
        }

        sealed record DurableCheckpointAttempt(
            MaterializationProgressRevision ExpectedRevision,
            MaterializationCheckpointId CheckpointId,
            MaterializationProgressMutationId MutationId);
    }

    sealed class CapturingBaselineFeed
    {
        internal List<QueryDefinition> Queries { get; } = [];

        internal FeedIterator<JsonElement> Create(
            FeedRange? feedRange,
            QueryDefinition query,
            string? continuationToken,
            QueryRequestOptions options)
        {
            Assert.Null(feedRange);
            Assert.Null(continuationToken);
            Assert.NotNull(options);
            Queries.Add(query);
            return new EmptyJsonFeedIterator();
        }
    }

    sealed class EmptyJsonFeedIterator : FeedIterator<JsonElement>
    {
        bool read;

        public override bool HasMoreResults => !read;

        public override Task<FeedResponse<JsonElement>> ReadNextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read)
            {
                throw new InvalidOperationException("The empty managed test feed was already read.");
            }

            read = true;
            return Task.FromResult<FeedResponse<JsonElement>>(new EmptyJsonFeedResponse());
        }
    }

    sealed class EmptyJsonFeedResponse : FeedResponse<JsonElement>
    {
        public override string ContinuationToken => string.Empty;

        public override int Count => 0;

        public override string IndexMetrics => string.Empty;

        public override string QueryAdvice => string.Empty;

        public override Headers Headers { get; } = new();

        public override IEnumerable<JsonElement> Resource => [];

        public override HttpStatusCode StatusCode => HttpStatusCode.OK;

        public override CosmosDiagnostics Diagnostics => null!;

        public override double RequestCharge => 0;

        public override string ActivityId => "tests/empty-managed-baseline";

        public override string ETag => string.Empty;

        public override IEnumerator<JsonElement> GetEnumerator() =>
            Enumerable.Empty<JsonElement>().GetEnumerator();
    }

    sealed class FakePullChangeFeedReader : ICosmosMaterializationChangeFeedReader
    {
        internal List<CosmosMaterializationChangeFeedStart> Calls { get; } = [];

        public ValueTask<CosmosMaterializationProviderChangePage> ReadPageAsync(
            CosmosMaterializationChangeFeedStart start,
            FeedRange? feedRange,
            int pageSizeHint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(start);
            return ValueTask.FromResult(new CosmosMaterializationProviderChangePage(
                changes: [],
                continuationToken: "position/pull",
                statusCode: HttpStatusCode.NotModified,
                requestCharge: 1,
                providerEvidenceReference: "tests/pull-position"));
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    sealed class TestHandlerException(string message) : Exception(message);

    sealed class TestProviderCheckpointException(string message) : Exception(message);

    sealed class TestDurableCheckpointException(string message) : Exception(message);

    sealed class TestObserverException(string message) : Exception(message);
}
