using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed partial class MaterializationRebuildExecutorTests
{
    [Fact]
    public async Task Synchronization_DirectCreateUpdateAndDeleteRetainOneStableItemWithMonotonicVersions()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var runtime = ImpactRuntime(harness.Rebuild);
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var created = Observation(feed, identity: "sync-root");
        var create = Delivery(feed, ordinal: 1, MaterializationChangeKind.Create, before: null, after: created);
        runtime.OutputMarkers[created.Identity] = "created";

        var createResult = await Executor(harness, feed, Source(harness.Rebuild, feed, [create]), runtime, workStore)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("direct-create"),
                Worker("primary"));
        var createdItem = Assert.Single(
            await Items(harness.Rebuild.Target, harness.Generation),
            static item => item.Value?.GetProperty("id").GetString() == "sync-root");

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, createResult.Disposition);
        Assert.Equal("2", createdItem.Version.Value);
        Assert.Equal("created", createdItem.Value!.Value.GetProperty("marker").GetString());

        var updated = Observation(feed, identity: "sync-root");
        var update = Delivery(feed, ordinal: 2, MaterializationChangeKind.Update, before: created, after: updated);
        runtime.OutputMarkers[created.Identity] = "updated";
        var updateResult = await Executor(
                harness,
                feed,
                Source(harness.Rebuild, feed, [create, update]),
                runtime,
                workStore)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("direct-update"),
                Worker("primary"));
        var updatedItem = Assert.Single(
            await Items(harness.Rebuild.Target, harness.Generation),
            static item => item.Value?.GetProperty("id").GetString() == "sync-root");

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, updateResult.Disposition);
        Assert.Equal(createdItem.ItemId, updatedItem.ItemId);
        Assert.Equal("3", updatedItem.Version.Value);
        Assert.Equal("updated", updatedItem.Value!.Value.GetProperty("marker").GetString());

        var delete = Delivery(feed, ordinal: 3, MaterializationChangeKind.Delete, before: updated, after: null);
        var deleteResult = await Executor(
                harness,
                feed,
                Source(harness.Rebuild, feed, [create, update, delete]),
                runtime,
                workStore)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("direct-delete"),
                Worker("primary"));
        var deletedItem = Assert.Single(
            await Items(harness.Rebuild.Target, harness.Generation),
            item => item.ItemId == createdItem.ItemId);

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, deleteResult.Disposition);
        Assert.Equal("4", deletedItem.Version.Value);
        Assert.Null(deletedItem.Value);
    }

    [Fact]
    public async Task Synchronization_ContributorFanOutCoalescesOverlappingRootsBeforeOneHydration()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = ContributorFeed(harness.Rebuild);
        var runtime = ImpactRuntime(harness.Rebuild);
        runtime.ResolvedRoots["change/1"] = ["fanout-a", "fanout-b"];
        runtime.ResolvedRoots["change/2"] = ["fanout-b", "fanout-c"];
        var deliveries = ImmutableArray.Create(
            Delivery(feed, ordinal: 1, MaterializationChangeKind.Upsert, before: null, after: Observation(feed, "contributor-1")),
            Delivery(feed, ordinal: 2, MaterializationChangeKind.Upsert, before: null, after: Observation(feed, "contributor-2")));

        var result = await Executor(
                harness,
                feed,
                Source(harness.Rebuild, feed, deliveries),
                runtime,
                new InMemoryMaterializationSynchronizationWorkStore())
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("fanout"),
                Worker("primary"));

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, result.Disposition);
        Assert.Equal(3, result.MutationsApplied);
        Assert.Equal(["change/1", "change/2"], runtime.ResolutionChanges);
        var hydration = Assert.Single(runtime.HydrationRoots);
        Assert.Equal(["fanout-a", "fanout-b", "fanout-c"], hydration.ToArray());
        var items = await Items(harness.Rebuild.Target, harness.Generation);
        Assert.All(
            new[] { "fanout-a", "fanout-b", "fanout-c" },
            identity => Assert.Contains(items, item => item.Value?.GetProperty("id").GetString() == identity));
    }

    [Fact]
    public async Task Synchronization_CrashAfterTargetBeforeCheckpointReplaysDurablePendingWork()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var runtime = ImpactRuntime(harness.Rebuild);
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var delivery = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Create,
            before: null,
            after: Observation(feed, "crash-root"));
        var source = Source(harness.Rebuild, feed, [delivery]);
        var throwingProgress = new ThrowOnceCheckpointProgressStore(harness.Rebuild.Resolved.ProgressStore);
        var first = Executor(harness, feed, source, runtime, workStore, throwingProgress);

        await Assert.ThrowsAsync<InjectedSynchronizationCrashException>(async () =>
            await first.RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("crash-retry"),
                Worker("primary")));
        var pending = await workStore.LoadAsync(OperationContext.Create(), first.GetWorkKey(harness.Attempt));
        var afterCrash = Assert.Single(
            await Items(harness.Rebuild.Target, harness.Generation),
            static item => item.Value?.GetProperty("id").GetString() == "crash-root");

        Assert.NotNull(pending?.PendingWork);
        Assert.Equal("2", afterCrash.Version.Value);

        var resumed = await Executor(harness, feed, source, runtime, workStore, throwingProgress)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("crash-retry"),
                Worker("primary"));
        var afterResume = Assert.Single(
            await Items(harness.Rebuild.Target, harness.Generation),
            item => item.ItemId == afterCrash.ItemId);
        var completed = await workStore.LoadAsync(
            OperationContext.Create(),
            first.GetWorkKey(harness.Attempt));

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, resumed.Disposition);
        Assert.Equal(0, resumed.PagesRead);
        Assert.Equal(afterCrash.MutationId, afterResume.MutationId);
        Assert.Equal("2", afterResume.Version.Value);
        Assert.Null(completed?.PendingWork);
        Assert.Equal(resumed.Progress!.LatestChangeCheckpoint!.Id, resumed.Evidence!.LatestChangeCheckpoint);
    }

    [Fact]
    public async Task Synchronization_EffectFreeProgressPageRecoversItsOrderingBoundaryAfterCheckpointCrash()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var through = Position(feed, "scripted/effect-free-progress");
        var source = new ScriptedChangeSource(
            harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source,
            read: (ordinal, request) => ordinal == 1
                ? new([], through, MaterializationChangePageState.Progressed)
                : new([], request.AfterPosition, MaterializationChangePageState.CaughtUp));
        var throwingProgress = new ThrowOnceCheckpointProgressStore(harness.Rebuild.Resolved.ProgressStore);
        var executor = Executor(
            harness,
            feed,
            source,
            ImpactRuntime(harness.Rebuild),
            workStore,
            throwingProgress);
        var invocation = Invocation("effect-free-crash-retry");

        await Assert.ThrowsAsync<InjectedSynchronizationCrashException>(async () =>
            await executor.RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                invocation,
                Worker("primary")));
        var pending = await workStore.LoadAsync(OperationContext.Create(), executor.GetWorkKey(harness.Attempt));

        Assert.NotNull(pending?.PendingWork);
        Assert.Null(pending!.PendingWork!.Version);
        Assert.Empty(pending.PendingWork.Mutations);
        Assert.Equal(1, source.ReadCalls);

        var resumed = await executor.RunFeedAsync(
            OperationContext.Create(),
            harness.Attempt,
            feed.Id,
            invocation,
            Worker("primary"));
        var completed = await workStore.LoadAsync(OperationContext.Create(), executor.GetWorkKey(harness.Attempt));

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, resumed.Disposition);
        Assert.Equal(0, resumed.MutationsApplied);
        Assert.Equal(2, source.ReadCalls);
        Assert.Equal(through, resumed.Progress!.LatestChangeCheckpoint!.Position);
        Assert.Equal(resumed.Progress.LatestChangeCheckpoint.Id, resumed.Progress.LatestSettlement!.Checkpoint);
        Assert.Null(completed!.PendingWork);
    }

    [Fact]
    public async Task Synchronization_StaleCheckpointFenceDoesNotAdvanceAndExactRetryRecoversPendingWork()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var runtime = ImpactRuntime(harness.Rebuild);
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var delivery = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Create,
            before: null,
            after: Observation(feed, "fenced-root"));
        var source = Source(harness.Rebuild, feed, [delivery]);
        var progress = new RejectOnceCheckpointProgressStore(harness.Rebuild.Resolved.ProgressStore);
        var executor = Executor(harness, feed, source, runtime, workStore, progress);
        var invocation = Invocation("stale-fence-retry");

        var fenced = await executor.RunFeedAsync(
            OperationContext.Create(),
            harness.Attempt,
            feed.Id,
            invocation,
            Worker("primary"));
        var pending = await workStore.LoadAsync(OperationContext.Create(), executor.GetWorkKey(harness.Attempt));

        Assert.Equal(MaterializationCatchUpFeedDisposition.Fenced, fenced.Disposition);
        Assert.NotNull(pending?.PendingWork);
        Assert.DoesNotContain(
            delivery.Id,
            fenced.Progress!.LatestChangeCheckpoint!.AppliedDeliveries);

        var recovered = await Executor(harness, feed, source, runtime, workStore)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                invocation,
                Worker("primary"));
        var completed = await workStore.LoadAsync(OperationContext.Create(), executor.GetWorkKey(harness.Attempt));

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, recovered.Disposition);
        Assert.Equal(0, recovered.PagesRead);
        Assert.Contains(delivery.Id, recovered.Progress!.LatestChangeCheckpoint!.AppliedDeliveries);
        Assert.Null(completed!.PendingWork);
    }

    [Fact]
    public async Task Synchronization_CheckpointBeforeSettlementFailureIsDrainedBeforeNextRead()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var runtime = ImpactRuntime(harness.Rebuild);
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var delivery = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Create,
            before: null,
            after: Observation(feed, "settlement-root"));
        var scripted = new ScriptedChangeSource(
            harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source,
            read: (ordinal, request) => ordinal == 1
                ? new([delivery], Position(feed, "scripted/1"), MaterializationChangePageState.CaughtUp)
                : new([], request.AfterPosition, MaterializationChangePageState.CaughtUp),
            rejectSettlementOrdinal: 2);
        var executor = Executor(harness, feed, scripted, runtime, workStore);

        var failed = await executor.RunFeedAsync(
            OperationContext.Create(),
            harness.Attempt,
            feed.Id,
            Invocation("settlement-failure"),
            Worker("primary"));
        var checkpoint = failed.Progress!.LatestChangeCheckpoint;
        var eventCount = scripted.Events.Count;
        var resumed = await executor.RunFeedAsync(
            OperationContext.Create(),
            harness.Attempt,
            feed.Id,
            Invocation("settlement-resume"),
            Worker("primary"));

        Assert.Equal(MaterializationCatchUpFeedDisposition.TargetOrSettlementFailed, failed.Disposition);
        Assert.Equal(Position(feed, "scripted/1"), checkpoint!.Position);
        Assert.NotEqual(checkpoint.Id, failed.Progress.LatestSettlement?.Checkpoint);
        Assert.Equal("settle", scripted.Events[eventCount]);
        Assert.Equal("read", scripted.Events[eventCount + 1]);
        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, resumed.Disposition);
        Assert.Equal(checkpoint.Id, resumed.Progress!.LatestSettlement!.Checkpoint);
    }

    [Fact]
    public async Task Synchronization_RepeatedEmptyCaughtUpReadReusesCheckpointWithoutMutations()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var runtime = ImpactRuntime(harness.Rebuild);
        var source = new ScriptedChangeSource(
            harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source,
            read: static (_, request) => new([], request.AfterPosition, MaterializationChangePageState.CaughtUp));
        var executor = Executor(
            harness,
            feed,
            source,
            runtime,
            new InMemoryMaterializationSynchronizationWorkStore());

        var first = await executor.RunFeedAsync(
            OperationContext.Create(),
            harness.Attempt,
            feed.Id,
            Invocation("empty-caught-up/1"),
            Worker("primary"));
        var replayed = await executor.RunFeedAsync(
            OperationContext.Create(),
            harness.Attempt,
            feed.Id,
            Invocation("empty-caught-up/2"),
            Worker("primary"));

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, first.Disposition);
        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, replayed.Disposition);
        Assert.Equal(0, first.MutationsApplied);
        Assert.Equal(0, replayed.MutationsApplied);
        Assert.Equal(first.Progress!.LatestChangeCheckpoint, replayed.Progress!.LatestChangeCheckpoint);
        Assert.Equal(first.Evidence!.LatestChangeCheckpoint, replayed.Evidence!.LatestChangeCheckpoint);
        Assert.Equal(2, source.ReadCalls);
    }

    [Fact]
    public async Task Synchronization_EmptyProgressedPageCheckpointsThroughPositionWithinPageBudget()
    {
        var harness = await CreateSynchronizationHarnessAsync(
            maximumPageItems: ReadItems,
            maximumPagesPerShard: 1);
        var feed = RootFeed(harness.Rebuild);
        var through = Position(feed, "scripted/progressed");
        var source = new ScriptedChangeSource(
            harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source,
            read: (_, _) => new([], through, MaterializationChangePageState.Progressed));

        var result = await Executor(
                harness,
                feed,
                source,
                ImpactRuntime(harness.Rebuild),
                new InMemoryMaterializationSynchronizationWorkStore())
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("progressed"),
                Worker("primary"));

        Assert.Equal(MaterializationCatchUpFeedDisposition.WorkRemaining, result.Disposition);
        Assert.Equal(1, result.PagesRead);
        Assert.Equal(0, result.MutationsApplied);
        Assert.Equal(through, result.Progress!.LatestChangeCheckpoint!.Position);
        Assert.Equal(result.Progress.LatestChangeCheckpoint.Id, result.Progress.LatestSettlement!.Checkpoint);
    }

    [Fact]
    public async Task Synchronization_ImpactFailureNeverAdvancesTheApplicationCheckpoint()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var runtime = ImpactRuntime(harness.Rebuild);
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var settledSource = new ScriptedChangeSource(
            harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source,
            read: static (_, request) => new([], request.AfterPosition, MaterializationChangePageState.CaughtUp));
        var settledExecutor = Executor(harness, feed, settledSource, runtime, workStore);
        var settled = await settledExecutor.RunFeedAsync(
            OperationContext.Create(),
            harness.Attempt,
            feed.Id,
            Invocation("impact-failure/settle"),
            Worker("primary"));
        var retainedCheckpoint = settled.Progress!.LatestChangeCheckpoint;
        var retainedSettlement = settled.Progress.LatestSettlement;

        runtime.ThrowOnHydration = true;
        var delivery = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Create,
            before: null,
            after: Observation(feed, "failed-root"));
        var failed = await Executor(
                harness,
                feed,
                Source(harness.Rebuild, feed, [delivery]),
                runtime,
                workStore)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("impact-failure"),
                Worker("primary"));

        Assert.Equal(MaterializationCatchUpFeedDisposition.SourceOrImpactFailed, failed.Disposition);
        Assert.Equal(retainedCheckpoint, failed.Progress!.LatestChangeCheckpoint);
        Assert.Equal(retainedSettlement, failed.Progress.LatestSettlement);
        Assert.DoesNotContain(
            await Items(harness.Rebuild.Target, harness.Generation),
            static item => item.Value?.GetProperty("id").GetString() == "failed-root");
    }

    [Fact]
    public async Task Synchronization_MoreAvailableAtFinitePageBudgetReturnsWorkRemainingWithDurableProgress()
    {
        var harness = await CreateSynchronizationHarnessAsync(
            maximumPageItems: ReadItems,
            maximumPagesPerShard: 1);
        var feed = RootFeed(harness.Rebuild);
        var delivery = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Create,
            before: null,
            after: Observation(feed, "bounded-root"));
        var through = Position(feed, "scripted/more-available");
        var source = new ScriptedChangeSource(
            harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source,
            read: (_, _) => new([delivery], through, MaterializationChangePageState.MoreAvailable));

        var result = await Executor(
                harness,
                feed,
                source,
                ImpactRuntime(harness.Rebuild),
                new InMemoryMaterializationSynchronizationWorkStore())
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("work-remaining"),
                Worker("primary"));

        Assert.Equal(MaterializationCatchUpFeedDisposition.WorkRemaining, result.Disposition);
        Assert.Equal(1, result.PagesRead);
        Assert.Equal(1, result.MutationsApplied);
        Assert.Equal(through, result.Progress!.LatestChangeCheckpoint!.Position);
        Assert.Equal(result.Progress.LatestChangeCheckpoint.Id, result.Progress.LatestSettlement!.Checkpoint);
    }

    [Theory]
    [InlineData(MaterializationChangePageState.MoreAvailable)]
    [InlineData(MaterializationChangePageState.Progressed)]
    public async Task Synchronization_NonCaughtUpPageRetainingRequestedPositionFailsWithoutCheckpointAdvance(
        MaterializationChangePageState state)
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var before = Assert.IsType<MaterializationProgressSnapshot>(
            await harness.Rebuild.Resolved.ProgressStore.LoadAsync(
                OperationContext.Create(),
                MaterializationRebuildExecutor.ProgressKey(
                    harness.Rebuild.Plan,
                    harness.Generation,
                    feed.Scope)));
        var delivery = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Create,
            before: null,
            after: Observation(feed, "retained-position-root"));
        var source = new ScriptedChangeSource(
            harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source,
            read: (_, request) => new(
                deliveries: state == MaterializationChangePageState.MoreAvailable ? [delivery] : [],
                throughPosition: request.AfterPosition,
                state));

        var result = await Executor(
                harness,
                feed,
                source,
                ImpactRuntime(harness.Rebuild),
                new InMemoryMaterializationSynchronizationWorkStore())
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation($"retained-position-{state}"),
                Worker("primary"));

        Assert.Equal(MaterializationCatchUpFeedDisposition.SourceOrImpactFailed, result.Disposition);
        Assert.Equal(0, result.PagesRead);
        Assert.Equal(before.LatestChangeCheckpoint, result.Progress!.LatestChangeCheckpoint);
        Assert.DoesNotContain(delivery.Id, result.Progress.LatestChangeCheckpoint!.AppliedDeliveries);
        Assert.DoesNotContain(
            await Items(harness.Rebuild.Target, harness.Generation),
            static item => item.Value?.GetProperty("id").GetString() == "retained-position-root");
    }

    [Fact]
    public async Task Synchronization_TransactionAlignedPageAbovePreferredPlusTransactionEnvelopeIsBoundaryExceeded()
    {
        var harness = await CreateSynchronizationHarnessAsync(
            maximumPageItems: 1,
            transactionAlignedChangeDelivery: true);
        var feed = RootFeed(harness.Rebuild);
        var deliveries = Enumerable.Range(start: 1, count: 4)
            .Select(ordinal => Delivery(
                feed,
                ordinal,
                MaterializationChangeKind.Create,
                before: null,
                after: Observation(feed, $"transaction-root-{ordinal}")))
            .ToImmutableArray();
        var source = new ScriptedChangeSource(
            harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source,
            read: (_, _) => new(
                deliveries,
                throughPosition: Position(feed, "transaction-aligned/4"),
                state: MaterializationChangePageState.CaughtUp));
        var before = Assert.IsType<MaterializationProgressSnapshot>(
            await harness.Rebuild.Resolved.ProgressStore.LoadAsync(
                OperationContext.Create(),
                MaterializationRebuildExecutor.ProgressKey(
                    harness.Rebuild.Plan,
                    harness.Generation,
                    feed.Scope)));

        var result = await Executor(
                harness,
                feed,
                source,
                ImpactRuntime(harness.Rebuild),
                new InMemoryMaterializationSynchronizationWorkStore())
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("transaction-aligned-envelope"),
                Worker("primary"));

        Assert.Equal(MaterializationCatchUpFeedDisposition.BoundaryExceeded, result.Disposition);
        Assert.Equal(0, result.PagesRead);
        Assert.Equal(before.LatestChangeCheckpoint, result.Progress!.LatestChangeCheckpoint);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == MaterializationSynchronizationDiagnosticCodes.OperatingBoundaryExceeded);
    }

    [Fact]
    public async Task Synchronization_ActiveGenerationContinuesFromSameCheckpointAndGeneration()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var runtime = ImpactRuntime(harness.Rebuild);
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var create = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Create,
            before: null,
            after: Observation(feed, "active-root"));
        var first = await Executor(
                harness,
                feed,
                Source(harness.Rebuild, feed, [create]),
                runtime,
                workStore)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("active-before-promotion"),
                Worker("primary"));
        var checkpointBeforePromotion = first.Progress!.LatestChangeCheckpoint;

        await PromoteAsync(harness.Rebuild.Target, harness.Generation);
        var active = await harness.Rebuild.Target.InspectGenerationAsync(
            OperationContext.Create(),
            harness.Generation);
        var update = Delivery(
            feed,
            ordinal: 2,
            MaterializationChangeKind.Update,
            before: create.Change.After,
            after: Observation(feed, "active-root"));
        runtime.OutputMarkers["active-root"] = "post-promotion";
        var maintained = await Executor(
                harness,
                feed,
                Source(harness.Rebuild, feed, [create, update]),
                runtime,
                workStore)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("active-maintenance"),
                Worker("primary"));
        var item = Assert.Single(
            await Items(harness.Rebuild.Target, harness.Generation),
            static candidate => candidate.Value?.GetProperty("id").GetString() == "active-root");

        Assert.Equal(MaterializationGenerationState.Active, active!.State);
        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, maintained.Disposition);
        Assert.Equal(harness.Generation, maintained.Generation);
        Assert.NotEqual(checkpointBeforePromotion!.Id, maintained.Progress!.LatestChangeCheckpoint!.Id);
        Assert.Equal("3", item.Version.Value);
        Assert.Equal("post-promotion", item.Value!.Value.GetProperty("marker").GetString());
    }

    [Fact]
    public async Task Synchronization_ConvergeVisitsEveryFeedWhenAnEarlierFeedHasWorkRemaining()
    {
        var harness = await CreateSynchronizationHarnessAsync(
            maximumPageItems: ReadItems,
            maximumPagesPerShard: 1);
        Dictionary<MaterializationChangeFeedId, ScriptedChangeSource> observed = [];
        Dictionary<MaterializationChangeFeedId, IMaterializationPullChangeSource> sources = [];
        var firstFeed = harness.Rebuild.Plan.ChangeFeeds[0];
        foreach (var feed in harness.Rebuild.Plan.ChangeFeeds)
        {
            var scripted = new ScriptedChangeSource(
                harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source,
                read: feed.Id == firstFeed.Id
                    ? (_, _) => new(
                        deliveries: [],
                        throughPosition: Position(feed, "converge/work-remaining"),
                        state: MaterializationChangePageState.Progressed)
                    : static (_, request) => new(
                        deliveries: [],
                        throughPosition: request.AfterPosition,
                        state: MaterializationChangePageState.CaughtUp));
            observed.Add(feed.Id, scripted);
            sources.Add(feed.Id, scripted);
        }

        var result = await Executor(
                harness,
                sources,
                ImpactRuntime(harness.Rebuild),
                new InMemoryMaterializationSynchronizationWorkStore())
            .ConvergeAsync(
                OperationContext.Create(),
                harness.Attempt,
                Invocation("converge-work-remaining"),
                Worker("converge"));

        Assert.Equal(MaterializationSynchronizationRunDisposition.WorkRemaining, result.Disposition);
        Assert.Null(result.Receipt);
        Assert.Equal(
            harness.Rebuild.Plan.ChangeFeeds.Select(static feed => feed.Id),
            result.Feeds.Select(static feed => feed.Feed));
        Assert.Equal(MaterializationCatchUpFeedDisposition.WorkRemaining, result.Feeds[0].Disposition);
        Assert.All(result.Feeds[1..], static feed =>
            Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, feed.Disposition));
        Assert.All(
            harness.Rebuild.Plan.ChangeFeeds,
            feed => Assert.Equal(1, observed[feed.Id].ReadCalls));
    }

    [Fact]
    public async Task Synchronization_LaterWorkerFencesAnOverlappingPriorWorker()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var source = new OverlappingWorkerChangeSource(
            harness.Rebuild.Resolved.GetChangeFeed(feed.Id).Source);
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var executor = Executor(
            harness,
            feed,
            source,
            ImpactRuntime(harness.Rebuild),
            workStore);
        var invocation = Invocation("overlapping-workers");
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var priorTask = executor.RunFeedAsync(
            OperationContext.Create(new FixedTimeProvider(now)),
            harness.Attempt,
            feed.Id,
            invocation,
            Worker("prior"));
        await source.WaitForFirstReadAsync();

        var later = await executor.RunFeedAsync(
            OperationContext.Create(new FixedTimeProvider(now.AddSeconds(1))),
            harness.Attempt,
            feed.Id,
            invocation,
            Worker("later"));
        source.ReleaseFirstRead();
        var prior = await priorTask;
        var retained = await workStore.LoadAsync(
            OperationContext.Create(),
            executor.GetWorkKey(harness.Attempt));

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, later.Disposition);
        Assert.Equal(MaterializationCatchUpFeedDisposition.Fenced, prior.Disposition);
        Assert.Equal(1, prior.PagesRead);
        Assert.Equal(2, source.ReadCalls);
        Assert.Contains(Worker("later").Value, retained!.FenceOwner, StringComparison.Ordinal);
        Assert.Null(retained.PendingWork);
    }

    [Fact]
    public async Task Synchronization_ConcurrentExactCheckpointWithDifferentCommitTimeDoesNotRequireRestart()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var delivery = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Create,
            before: null,
            after: Observation(feed, "checkpoint-race-root"));
        var progressStore = new ReverseFirstTwoCheckpointProgressStore(
            harness.Rebuild.Resolved.ProgressStore);
        var executor = Executor(
            harness,
            feed,
            Source(harness.Rebuild, feed, [delivery]),
            ImpactRuntime(harness.Rebuild),
            new InMemoryMaterializationSynchronizationWorkStore(),
            progressStore);
        var invocation = Invocation("checkpoint-race");
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var priorTask = executor.RunFeedAsync(
            OperationContext.Create(new FixedTimeProvider(now)),
            harness.Attempt,
            feed.Id,
            invocation,
            Worker("checkpoint-race/prior"));
        await progressStore.WaitForFirstCheckpointAsync();

        var later = await executor.RunFeedAsync(
            OperationContext.Create(new FixedTimeProvider(now.AddSeconds(1))),
            harness.Attempt,
            feed.Id,
            invocation,
            Worker("checkpoint-race/later"));
        var prior = await priorTask;

        Assert.Equal(2, progressStore.CheckpointCalls);
        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, later.Disposition);
        Assert.Equal(MaterializationCatchUpFeedDisposition.Fenced, prior.Disposition);
        Assert.NotEqual(MaterializationCatchUpFeedDisposition.RestartRequired, prior.Disposition);
        Assert.Equal(
            now.AddSeconds(1),
            later.Progress!.LatestChangeCheckpoint!.CommittedAtUtc);
        Assert.Equal(
            later.Progress.LatestChangeCheckpoint.Id,
            prior.Progress!.LatestChangeCheckpoint!.Id);
        Assert.Equal(
            later.Progress.LatestChangeCheckpoint.Position,
            prior.Progress.LatestChangeCheckpoint.Position);
        Assert.Equal(
            later.Progress.LatestChangeCheckpoint.AppliedDeliveries,
            prior.Progress.LatestChangeCheckpoint.AppliedDeliveries);
    }

    [Fact]
    public void Synchronization_CompletionIdentityIsStableOnlyWithinExactWorkerAuthority()
    {
        var fixture = CreateFixture();
        var feed = RootFeed(fixture);
        MaterializationPreparedSynchronizationWork prepared = new(
            preparationId: new("tests/preparation/completion-authority"),
            page: new(
                feed: feed.Id,
                checkpoint: new("tests/checkpoint/completion-authority"),
                throughPosition: Position(feed, "completion-authority"),
                appliedDeliveries: [],
                state: MaterializationChangePageState.CaughtUp,
                readStartedAtUtc: Epoch,
                readCompletedAtUtc: Epoch),
            version: null,
            mutations: []);

        var first = MaterializationSynchronizationIdentities.Completion(
            prepared,
            owner: "tests/worker/first",
            fence: new("1"));
        var exactRetry = MaterializationSynchronizationIdentities.Completion(
            prepared,
            owner: "tests/worker/first",
            fence: new("1"));
        var successorOwner = MaterializationSynchronizationIdentities.Completion(
            prepared,
            owner: "tests/worker/successor",
            fence: new("1"));
        var successorFence = MaterializationSynchronizationIdentities.Completion(
            prepared,
            owner: "tests/worker/first",
            fence: new("2"));

        Assert.Equal(first, exactRetry);
        Assert.NotEqual(first, successorOwner);
        Assert.NotEqual(first, successorFence);
    }

    [Theory]
    [InlineData(
        MaterializationSynchronizationWorkMutationDisposition.NotFound,
        MaterializationCatchUpFeedDisposition.Fenced)]
    [InlineData(
        MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict,
        MaterializationCatchUpFeedDisposition.Fenced)]
    [InlineData(
        MaterializationSynchronizationWorkMutationDisposition.IdentityConflict,
        MaterializationCatchUpFeedDisposition.RestartRequired)]
    public async Task Synchronization_CompletionReconciliationRequiresExactCompletedWork(
        MaterializationSynchronizationWorkMutationDisposition rejection,
        MaterializationCatchUpFeedDisposition expectedDisposition)
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = RootFeed(harness.Rebuild);
        var delivery = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Create,
            before: null,
            after: Observation(feed, "completion-rejection-root"));
        var retainedWork = new InMemoryMaterializationSynchronizationWorkStore();
        var workStore = new RejectFirstCompletionWorkStore(retainedWork, rejection);
        var executor = Executor(
            harness,
            feed,
            Source(harness.Rebuild, feed, [delivery]),
            ImpactRuntime(harness.Rebuild),
            workStore);

        var result = await executor.RunFeedAsync(
            OperationContext.Create(),
            harness.Attempt,
            feed.Id,
            Invocation($"completion-rejection-{rejection}"),
            Worker("primary"));
        var retained = await retainedWork.LoadAsync(
            OperationContext.Create(),
            executor.GetWorkKey(harness.Attempt));

        Assert.Equal(expectedDisposition, result.Disposition);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(
            $"rejected with '{rejection}'",
            StringComparison.Ordinal));
        Assert.NotNull(retained?.PendingWork);
    }

    static async Task<SynchronizationHarness> CreateSynchronizationHarnessAsync(
        int maximumPageItems = 2,
        int maximumPagesPerShard = 10,
        bool transactionAlignedChangeDelivery = false)
    {
        var rebuild = CreateFixture(
            maximumPageItems: maximumPageItems,
            maximumPagesPerShard: maximumPagesPerShard,
            transactionAlignedChangeDelivery: transactionAlignedChangeDelivery);
        var attempt = Attempt("attempt-synchronization");
        var initialized = await rebuild.Executor.BeginAttemptAsync(OperationContext.Create(), attempt);
        foreach (var shard in rebuild.Plan.Shards)
        {
            var result = await rebuild.Executor.RunShardAsync(
                OperationContext.Create(),
                attempt,
                shard.Id);
            Assert.Equal(MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired, result.Disposition);
        }

        Assert.NotNull(await rebuild.Executor.InspectReadinessAsync(OperationContext.Create(), attempt));
        return new(rebuild, attempt, initialized.Generation);
    }

    static MaterializationSynchronizationExecutor Executor(
        SynchronizationHarness harness,
        MaterializationChangeFeedPlan selectedFeed,
        IMaterializationPullChangeSource source,
        IMaterializationImpactRuntime runtime,
        IMaterializationSynchronizationWorkStore workStore,
        IMaterializationProgressStore? progressStore = null) =>
        Executor(
            harness,
            new Dictionary<MaterializationChangeFeedId, IMaterializationPullChangeSource>
            {
                [selectedFeed.Id] = source
            },
            runtime,
            workStore,
            progressStore);

    static MaterializationSynchronizationExecutor Executor(
        SynchronizationHarness harness,
        IReadOnlyDictionary<MaterializationChangeFeedId, IMaterializationPullChangeSource> sources,
        IMaterializationImpactRuntime runtime,
        IMaterializationSynchronizationWorkStore workStore,
        IMaterializationProgressStore? progressStore = null)
    {
        var interpreter = new MaterializationImpactPlanInterpreter(
            harness.Rebuild.Plan.ImpactPlan,
            harness.Rebuild.Plan.Materialization.Definition,
            runtime);
        var resolved = new ResolvedMaterializationRebuildPlan(
            plan: harness.Rebuild.Plan,
            target: harness.Rebuild.Target,
            progressStore: progressStore ?? harness.Rebuild.Resolved.ProgressStore,
            shardBindings: harness.Rebuild.Plan.Shards.Select(shard => harness.Rebuild.Resolved.GetShard(shard.Id)),
            changeFeedBindings: harness.Rebuild.Plan.ChangeFeeds.Select(feed =>
            {
                var retained = harness.Rebuild.Resolved.GetChangeFeed(feed.Id);
                return sources.TryGetValue(feed.Id, out var source)
                    ? new MaterializationChangeFeedBinding(
                        feed: feed,
                        channel: feed.Channel,
                        source: source,
                        interpreter: interpreter)
                    : retained;
            }));
        return new(resolved, workStore);
    }

    static InMemoryMaterializationSource Source(
        RebuildFixture fixture,
        MaterializationChangeFeedPlan feed,
        ImmutableArray<MaterializationChangeDelivery> deliveries) =>
        new(fixture.Resolved.GetChangeFeed(feed.Id).Source.Descriptor, deliveries);

    static MaterializationChangeFeedPlan RootFeed(RebuildFixture fixture) =>
        fixture.Plan.ChangeFeeds.First(feed =>
            feed.Scope.Input == fixture.Root.Input.Id
            && feed.Scope.Partition.Value == "partition-a");

    static MaterializationChangeFeedPlan ContributorFeed(RebuildFixture fixture) =>
        fixture.Plan.ChangeFeeds.First(feed => feed.Scope.Input != fixture.Root.Input.Id);

    static TestSynchronizationImpactRuntime ImpactRuntime(RebuildFixture fixture) =>
        new(
            impactPlan: fixture.Plan.ImpactPlan.Fingerprint,
            rootInput: fixture.Root.Input.Id,
            rootShape: fixture.Root.Shape,
            outputShape: fixture.Plan.Materialization.Definition.Relation.Output.Shape);

    static RelationQuerySourceReadObservation Observation(
        MaterializationChangeFeedPlan feed,
        string identity) =>
        new(identity, feed.Scope.Shape, fields: []);

    static MaterializationSourcePosition Position(MaterializationChangeFeedPlan feed, string value) =>
        new(formatVersion: 1, feed.Scope, value);

    static MaterializationSynchronizationInvocationId Invocation(string suffix) =>
        new($"tests/synchronization-invocation/{suffix}");

    static MaterializationSynchronizationWorkerId Worker(string suffix) =>
        new($"tests/synchronization-worker/{suffix}");

    static MaterializationChangeDelivery Delivery(
        MaterializationChangeFeedPlan feed,
        int ordinal,
        MaterializationChangeKind kind,
        RelationQuerySourceReadObservation? before,
        RelationQuerySourceReadObservation? after)
    {
        var subject = after?.Identity ?? before?.Identity
            ?? throw new ArgumentException("A test change requires one observation image.");
        var observed = Epoch.AddSeconds(ordinal);
        var change = new MaterializationChangeEnvelope(
            id: new($"change/{ordinal}"),
            subjectIdentity: subject,
            scope: feed.Scope,
            shape: feed.Scope.Shape,
            position: Position(feed, $"delivery/{ordinal}"),
            kind,
            before,
            after,
            occurredAtUtc: observed,
            observedAtUtc: observed,
            evidenceReference: $"tests/change/{ordinal}");
        return new(
            id: new($"delivery/{ordinal}"),
            change,
            deliveredAtUtc: observed,
            evidenceReference: $"tests/delivery/{ordinal}");
    }

    static async Task PromoteAsync(
        InMemoryMaterializationTarget target,
        MaterializationGenerationId generation)
    {
        var loading = Assert.IsType<MaterializationGenerationSnapshot>(
            await target.InspectGenerationAsync(OperationContext.Create(), generation));
        var now = DateTimeOffset.UtcNow;
        var sealedResult = await target.SealGenerationAsync(
            OperationContext.Create(),
            new(
                sealId: new("seal/synchronization"),
                generationId: generation,
                expectedRevision: loading.Revision,
                workerFence: loading.LatestWorkerFence,
                sealedAtUtc: now));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, sealedResult.Disposition);
        var validated = await target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                validationId: new("validation/synchronization"),
                generationId: generation,
                expectedRevision: sealedResult.Generation!.Revision,
                expectedSealFingerprint: sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: sealedResult.Receipt.VisibleItemCount,
                validator: "tests/synchronization-validator/v1",
                workerFence: sealedResult.Generation.LatestWorkerFence,
                validatedAtUtc: now.AddSeconds(1)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, validated.Disposition);
        var targetSnapshot = await target.InspectAsync(OperationContext.Create());
        var promoted = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            new(
                promotionId: new("promotion/synchronization"),
                generationId: generation,
                expectedGenerationRevision: validated.Generation!.Revision,
                validationFingerprint: validated.Receipt!.Fingerprint,
                expectedActiveGenerationId: targetSnapshot.ActiveGenerationId,
                expectedTargetRevision: targetSnapshot.Revision,
                generationWorkerFence: validated.Generation.LatestWorkerFence,
                promotionFence: new("1"),
                promotedAtUtc: now.AddSeconds(2)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, promoted.Disposition);
    }

    sealed record SynchronizationHarness(
        RebuildFixture Rebuild,
        MaterializationRebuildAttempt Attempt,
        MaterializationGenerationId Generation);

    sealed class TestSynchronizationImpactRuntime(
        MaterializationImpactPlanFingerprint impactPlan,
        RelationQueryInputId rootInput,
        QualifiedShapeId rootShape,
        QualifiedShapeId outputShape) : IMaterializationImpactRuntime
    {
        public MaterializationImpactPlanFingerprint ImpactPlan { get; } = impactPlan;

        public Dictionary<string, ImmutableArray<string>> ResolvedRoots { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> OutputMarkers { get; } = new(StringComparer.Ordinal);

        public List<string> ResolutionChanges { get; } = [];

        public List<ImmutableArray<string>> HydrationRoots { get; } = [];

        public bool ThrowOnHydration { get; set; }

        public ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
            OperationContext context,
            MaterializationImpactRootResolutionRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            ResolutionChanges.Add(request.Change.Id.Value);
            var identities = ResolvedRoots[request.Change.Id.Value];
            return ValueTask.FromResult(identities.Select(identity => new MaterializationAffectedRoot(
                    input: rootInput,
                    identity,
                    state: MaterializationRootState.Present,
                    observation: new(identity, rootShape, fields: [])))
                .ToImmutableArray());
        }

        public ValueTask<ImmutableArray<MaterializationRootProjection>> HydrateAsync(
            OperationContext context,
            MaterializationImpactHydrationRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            if (ThrowOnHydration)
                throw new InvalidOperationException("Injected synchronization hydration failure.");

            HydrationRoots.Add([.. request.Roots.Select(static root => root.Identity)]);
            return ValueTask.FromResult(request.Roots.Select(root => new MaterializationRootProjection(
                    root,
                    root.State == MaterializationRootState.Absent
                        ? null
                        : new RelationQueryOutputRow(
                            shape: outputShape,
                            value: ObservationValue.FromObject(
                                new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                                {
                                    ["id"] = ObservationValue.FromString(root.Identity),
                                    ["marker"] = ObservationValue.FromString(
                                        OutputMarkers.GetValueOrDefault(root.Identity, "present"))
                                }),
                            identity: ObservationValue.FromString(root.Identity),
                            root: null,
                            inputOccurrences: [],
                            unresolvedGaps: [])))
                .ToImmutableArray());
        }
    }

    sealed class ScriptedChangeSource(
        IMaterializationPullChangeSource inner,
        Func<int, MaterializationChangeReadRequest, MaterializationChangePage> read,
        int? rejectSettlementOrdinal = null)
        : IMaterializationPullChangeSource, IMaterializationSettlingSource
    {
        public MaterializationQuerySourceDescriptor Descriptor => inner.Descriptor;

        public List<string> Events { get; } = [];

        public int ReadCalls { get; private set; }

        public int SettlementCalls { get; private set; }

        public ValueTask<MaterializationSourcePage> ReadPageAsync(
            OperationContext context,
            MaterializationSourcePageRequest request) =>
            inner.ReadPageAsync(context, request);

        public ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
            OperationContext context,
            MaterializationSourceScope scope) =>
            inner.CaptureCurrentPositionAsync(context, scope);

        public ValueTask<MaterializationChangePage> ReadChangesAsync(
            OperationContext context,
            MaterializationChangeReadRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            Events.Add("read");
            ReadCalls++;
            return ValueTask.FromResult(read(ReadCalls, request));
        }

        public ValueTask<MaterializationSourceSettlementResult> SettleAsync(
            OperationContext context,
            MaterializationSourceSettlementRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            Events.Add("settle");
            SettlementCalls++;
            if (SettlementCalls == rejectSettlementOrdinal)
            {
                return ValueTask.FromResult(new MaterializationSourceSettlementResult(
                    MaterializationSourceSettlementDisposition.Rejected,
                    receipt: null,
                    diagnostics:
                    [
                        MaterializationContract.CreateDiagnostic(
                            code: "tests.materialization.settlement.rejected",
                            severity: DiagnosticSeverity.Error,
                            message: "The test source rejected settlement once.",
                            location: "/settlement",
                            stage: "tests-materialization-synchronization",
                            subject: request.Checkpoint.Value,
                            sourceReferences: ["tests/settlement-source"],
                            expected: "acknowledged settlement",
                            observed: "injected rejection")
                    ]));
            }

            MaterializationSourceSettlement receipt = new(
                id: request.Id,
                checkpoint: request.Checkpoint,
                position: request.Position,
                settledAtUtc: context.UtcNow,
                evidenceReference: "tests/scripted-settlement/v1");
            return ValueTask.FromResult(new MaterializationSourceSettlementResult(
                MaterializationSourceSettlementDisposition.Acknowledged,
                receipt));
        }
    }

    sealed class OverlappingWorkerChangeSource(IMaterializationPullChangeSource inner)
        : IMaterializationPullChangeSource, IMaterializationSettlingSource
    {
        readonly TaskCompletionSource firstReadStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource releaseFirstRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int readCalls;

        public MaterializationQuerySourceDescriptor Descriptor => inner.Descriptor;

        public int ReadCalls => Volatile.Read(ref readCalls);

        public ValueTask<MaterializationSourcePage> ReadPageAsync(
            OperationContext context,
            MaterializationSourcePageRequest request) =>
            inner.ReadPageAsync(context, request);

        public ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
            OperationContext context,
            MaterializationSourceScope scope) =>
            inner.CaptureCurrentPositionAsync(context, scope);

        public async ValueTask<MaterializationChangePage> ReadChangesAsync(
            OperationContext context,
            MaterializationChangeReadRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            var ordinal = Interlocked.Increment(ref readCalls);
            if (ordinal == 1)
            {
                firstReadStarted.TrySetResult();
                await releaseFirstRead.Task.WaitAsync(context.CancellationToken);
            }

            return new(
                deliveries: [],
                throughPosition: request.AfterPosition,
                state: MaterializationChangePageState.CaughtUp);
        }

        public ValueTask<MaterializationSourceSettlementResult> SettleAsync(
            OperationContext context,
            MaterializationSourceSettlementRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new MaterializationSourceSettlementResult(
                MaterializationSourceSettlementDisposition.Acknowledged,
                new(
                    id: request.Id,
                    checkpoint: request.Checkpoint,
                    position: request.Position,
                    settledAtUtc: context.UtcNow,
                    evidenceReference: "tests/overlapping-worker-settlement/v1")));
        }

        public Task WaitForFirstReadAsync() => firstReadStarted.Task;

        public void ReleaseFirstRead() => releaseFirstRead.TrySetResult();
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class ThrowOnceCheckpointProgressStore(IMaterializationProgressStore inner)
        : IMaterializationProgressStore
    {
        bool shouldThrow = true;

        public Task<MaterializationProgressSnapshot?> LoadAsync(
            OperationContext context,
            MaterializationProgressKey key) =>
            inner.LoadAsync(context, key);

        public Task<MaterializationProgressMutationResult> AcquireFenceAsync(
            OperationContext context,
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision? expectedRevision,
            string owner) =>
            inner.AcquireFenceAsync(context, key, mutationId, expectedRevision, owner);

        public Task<MaterializationProgressMutationResult> SaveCheckpointAsync(
            OperationContext context,
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationApplicationCheckpoint checkpoint)
        {
            if (shouldThrow)
            {
                shouldThrow = false;
                throw new InjectedSynchronizationCrashException();
            }

            return inner.SaveCheckpointAsync(
                context,
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                checkpoint);
        }

        public Task<MaterializationProgressMutationResult> SaveSettlementAsync(
            OperationContext context,
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationSourceSettlement settlement) =>
            inner.SaveSettlementAsync(
                context,
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                settlement);
    }

    sealed class RejectOnceCheckpointProgressStore(IMaterializationProgressStore inner)
        : IMaterializationProgressStore
    {
        bool shouldReject = true;

        public Task<MaterializationProgressSnapshot?> LoadAsync(
            OperationContext context,
            MaterializationProgressKey key) =>
            inner.LoadAsync(context, key);

        public Task<MaterializationProgressMutationResult> AcquireFenceAsync(
            OperationContext context,
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision? expectedRevision,
            string owner) =>
            inner.AcquireFenceAsync(context, key, mutationId, expectedRevision, owner);

        public async Task<MaterializationProgressMutationResult> SaveCheckpointAsync(
            OperationContext context,
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationApplicationCheckpoint checkpoint)
        {
            if (!shouldReject)
            {
                return await inner.SaveCheckpointAsync(
                    context,
                    key,
                    mutationId,
                    expectedRevision,
                    owner,
                    fence,
                    checkpoint);
            }

            shouldReject = false;
            var current = Assert.IsType<MaterializationProgressSnapshot>(
                await inner.LoadAsync(context, key));
            return new(
                MaterializationProgressMutationDisposition.StaleFence,
                current,
                diagnostics:
                [
                    MaterializationContract.CreateDiagnostic(
                        code: "tests.materialization.progress.staleFence",
                        severity: DiagnosticSeverity.Error,
                        message: "The test progress store rejected one stale worker fence.",
                        location: "/progress/fence",
                        stage: "tests-materialization-synchronization",
                        subject: key.Scope.Partition.Value,
                        sourceReferences: ["tests/progress-store"],
                        expected: "current progress fence",
                        observed: "injected stale fence")
                ]);
        }

        public Task<MaterializationProgressMutationResult> SaveSettlementAsync(
            OperationContext context,
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationSourceSettlement settlement) =>
            inner.SaveSettlementAsync(
                context,
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                settlement);
    }

    sealed class ReverseFirstTwoCheckpointProgressStore(IMaterializationProgressStore inner)
        : IMaterializationProgressStore
    {
        readonly TaskCompletionSource firstCheckpointStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource secondCheckpointCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int checkpointCalls;

        public int CheckpointCalls => Volatile.Read(ref checkpointCalls);

        public Task<MaterializationProgressSnapshot?> LoadAsync(
            OperationContext context,
            MaterializationProgressKey key) =>
            inner.LoadAsync(context, key);

        public Task<MaterializationProgressMutationResult> AcquireFenceAsync(
            OperationContext context,
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision? expectedRevision,
            string owner) =>
            inner.AcquireFenceAsync(context, key, mutationId, expectedRevision, owner);

        public async Task<MaterializationProgressMutationResult> SaveCheckpointAsync(
            OperationContext context,
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationApplicationCheckpoint checkpoint)
        {
            var ordinal = Interlocked.Increment(ref checkpointCalls);
            if (ordinal == 1)
            {
                firstCheckpointStarted.TrySetResult();
                await secondCheckpointCompleted.Task.WaitAsync(context.CancellationToken);
                return await inner.SaveCheckpointAsync(
                    context,
                    key,
                    mutationId,
                    expectedRevision,
                    owner,
                    fence,
                    checkpoint);
            }

            if (ordinal == 2)
            {
                try
                {
                    return await inner.SaveCheckpointAsync(
                        context,
                        key,
                        mutationId,
                        expectedRevision,
                        owner,
                        fence,
                        checkpoint);
                }
                finally
                {
                    secondCheckpointCompleted.TrySetResult();
                }
            }

            return await inner.SaveCheckpointAsync(
                context,
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                checkpoint);
        }

        public Task<MaterializationProgressMutationResult> SaveSettlementAsync(
            OperationContext context,
            MaterializationProgressKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationSourceSettlement settlement) =>
            inner.SaveSettlementAsync(
                context,
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                settlement);

        public Task WaitForFirstCheckpointAsync() => firstCheckpointStarted.Task;
    }

    sealed class RejectFirstCompletionWorkStore(
        IMaterializationSynchronizationWorkStore inner,
        MaterializationSynchronizationWorkMutationDisposition rejection)
        : IMaterializationSynchronizationWorkStore
    {
        bool shouldReject = true;

        public Task<MaterializationSynchronizationWorkSnapshot?> LoadAsync(
            OperationContext context,
            MaterializationSynchronizationWorkKey key) =>
            inner.LoadAsync(context, key);

        public Task<MaterializationSynchronizationWorkMutationResult> AcquireFenceAsync(
            OperationContext context,
            MaterializationSynchronizationWorkKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision? expectedRevision,
            string owner) =>
            inner.AcquireFenceAsync(context, key, mutationId, expectedRevision, owner);

        public Task<MaterializationSynchronizationWorkMutationResult> PrepareAsync(
            OperationContext context,
            MaterializationSynchronizationWorkKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationSynchronizationWorkIntent intent) =>
            inner.PrepareAsync(context, key, mutationId, expectedRevision, owner, fence, intent);

        public async Task<MaterializationSynchronizationWorkMutationResult> CompleteAsync(
            OperationContext context,
            MaterializationSynchronizationWorkKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationProgressMutationId preparationId,
            MaterializationItemVersion? version)
        {
            if (!shouldReject)
            {
                return await inner.CompleteAsync(
                    context,
                    key,
                    mutationId,
                    expectedRevision,
                    owner,
                    fence,
                    preparationId,
                    version);
            }

            shouldReject = false;
            var current = await inner.LoadAsync(context, key);
            MaterializationSynchronizationWorkSnapshot? snapshot = rejection switch
            {
                MaterializationSynchronizationWorkMutationDisposition.NotFound => null,
                MaterializationSynchronizationWorkMutationDisposition.IdentityConflict => new(
                    key: current!.Key,
                    revision: current.Revision,
                    fence: current.Fence,
                    fenceOwner: current.FenceOwner,
                    nextItemVersion: current.PendingWork!.Version!.Value,
                    pendingWork: null,
                    activation: current.Activation),
                _ => current
            };
            return new(
                rejection,
                snapshot,
                diagnostics:
                [
                    MaterializationContract.CreateDiagnostic(
                        code: "tests.materialization.synchronization.completionRejected",
                        severity: DiagnosticSeverity.Error,
                        message: $"The test work store rejected completion with '{rejection}'.",
                        location: "/synchronization/work/completion",
                        stage: "tests-materialization-synchronization",
                        subject: key.Generation.Value,
                        sourceReferences: ["tests/work-store"],
                        expected: "durable exact completion",
                        observed: rejection.ToString())
                ]);
        }

        public Task<MaterializationSynchronizationWorkMutationResult> SaveActivationAsync(
            OperationContext context,
            MaterializationSynchronizationWorkKey key,
            MaterializationProgressMutationId mutationId,
            MaterializationProgressRevision expectedRevision,
            string owner,
            MaterializationProgressFence fence,
            MaterializationGenerationActivationState activation) =>
            inner.SaveActivationAsync(
                context,
                key,
                mutationId,
                expectedRevision,
                owner,
                fence,
                activation);
    }

    sealed class InjectedSynchronizationCrashException()
        : Exception("Injected crash after synchronization target application and before checkpoint persistence.");
}
