using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed partial class MaterializationRebuildExecutorTests
{
    [Fact]
    public async Task Synchronization_ReplayingSourceHistoryEqualsAFreshRebuildOfTheFinalState()
    {
        var incremental = await CreateSynchronizationHarnessAsync();
        var rootFeed = RootFeed(incremental.Rebuild);
        var oldIdentity = incremental.Rebuild.ScanReaders[new("shard-a")].FirstObservationIdentity;
        var retainedIdentity = incremental.Rebuild.ScanReaders[new("shard-b")].FirstObservationIdentity;
        const string newIdentity = "load/created-after-baseline";
        var oldRoot = Observation(rootFeed, oldIdentity);
        var retainedRoot = Observation(rootFeed, retainedIdentity);
        var createdRoot = Observation(rootFeed, newIdentity);
        ImmutableArray<MaterializationChangeDelivery> history =
        [
            Delivery(rootFeed, ordinal: 1, MaterializationChangeKind.Delete, before: oldRoot, after: null),
            Delivery(rootFeed, ordinal: 2, MaterializationChangeKind.Update, before: retainedRoot, after: retainedRoot),
            Delivery(rootFeed, ordinal: 3, MaterializationChangeKind.Create, before: null, after: createdRoot)
        ];
        var incrementalRuntime = new ExactProjectionImpactRuntime(
            impactPlan: incremental.Rebuild.Plan.ImpactPlan.Fingerprint,
            rootInput: incremental.Rebuild.Root.Input.Id,
            rootShape: incremental.Rebuild.Root.Shape,
            outputShape: incremental.Rebuild.Plan.Materialization.Definition.Relation.Output.Shape);

        var replayed = await Executor(
                incremental,
                rootFeed,
                Source(incremental.Rebuild, rootFeed, history),
                incrementalRuntime,
                new InMemoryMaterializationSynchronizationWorkStore())
            .RunFeedAsync(
                OperationContext.Create(),
                incremental.Attempt,
                rootFeed.Id,
                Invocation("rebuild-equivalence"),
                Worker("rebuild-equivalence"));

        var fresh = CreateFixture();
        Assert.Equal(oldIdentity, fresh.ScanReaders[new("shard-a")].FirstObservationIdentity);
        fresh.ScanReaders[new("shard-a")].ReplaceFirstObservationIdentity(newIdentity);
        var freshAttempt = Attempt("attempt-fresh-final-state");
        var initialized = await fresh.Executor.BeginAttemptAsync(OperationContext.Create(), freshAttempt);
        foreach (var shard in fresh.Plan.Shards)
        {
            var result = await fresh.Executor.RunShardAsync(OperationContext.Create(), freshAttempt, shard.Id);
            Assert.Equal(MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired, result.Disposition);
        }

        var incrementalValues = VisibleValues(await Items(incremental.Rebuild.Target, incremental.Generation));
        var rebuiltValues = VisibleValues(await Items(fresh.Target, initialized.Generation));

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, replayed.Disposition);
        Assert.Equal(rebuiltValues.Keys, incrementalValues.Keys);
        foreach (var item in rebuiltValues)
            Assert.Equal(item.Value, incrementalValues[item.Key]);
    }

    [Fact]
    public async Task Synchronization_RelationshipMoveDeletesTheOldRootAndUpsertsTheNewRoot()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var feed = ContributorFeed(harness.Rebuild);
        var oldIdentity = harness.Rebuild.ScanReaders[new("shard-a")].FirstObservationIdentity;
        const string newIdentity = "load/moved-relationship-root";
        var runtime = new ExactProjectionImpactRuntime(
            impactPlan: harness.Rebuild.Plan.ImpactPlan.Fingerprint,
            rootInput: harness.Rebuild.Root.Input.Id,
            rootShape: harness.Rebuild.Root.Shape,
            outputShape: harness.Rebuild.Plan.Materialization.Definition.Relation.Output.Shape);
        runtime.ResolvedRoots["change/1"] =
        [
            (oldIdentity, MaterializationRootState.Absent),
            (newIdentity, MaterializationRootState.Present)
        ];
        var contributor = Observation(feed, "relationship/contributor");
        var delivery = Delivery(
            feed,
            ordinal: 1,
            MaterializationChangeKind.Update,
            before: contributor,
            after: contributor);

        var result = await Executor(
                harness,
                feed,
                Source(harness.Rebuild, feed, [delivery]),
                runtime,
                new InMemoryMaterializationSynchronizationWorkStore())
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("relationship-move"),
                Worker("relationship-move"));
        var items = await Items(harness.Rebuild.Target, harness.Generation);
        var oldItem = Assert.Single(items, item => item.ItemId == MaterializationItemIdentity.FromRootIdentity(oldIdentity));
        var newItem = Assert.Single(items, item => item.ItemId == MaterializationItemIdentity.FromRootIdentity(newIdentity));

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, result.Disposition);
        Assert.Equal(2, result.MutationsApplied);
        Assert.Null(oldItem.Value);
        Assert.Equal(newIdentity, newItem.Value!.Value.GetProperty("id").GetString());
    }

    static SortedDictionary<string, ObservationValue> VisibleValues(
        ImmutableArray<InMemoryMaterializationTargetItemSnapshot> items)
    {
        SortedDictionary<string, ObservationValue> values = new(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Value is { } value)
                values.Add(item.ItemId.Value, value);
        }
        return values;
    }

    sealed class ExactProjectionImpactRuntime(
        MaterializationImpactPlanFingerprint impactPlan,
        RelationQueryInputId rootInput,
        QualifiedShapeId rootShape,
        QualifiedShapeId outputShape) : IMaterializationImpactRuntime
    {
        public MaterializationImpactPlanFingerprint ImpactPlan { get; } = impactPlan;

        public Dictionary<string, ImmutableArray<(string Identity, MaterializationRootState State)>> ResolvedRoots
            { get; } = new(StringComparer.Ordinal);

        public ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
            OperationContext context,
            MaterializationImpactRootResolutionRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ResolvedRoots[request.Change.Id.Value]
                .Select(root => new MaterializationAffectedRoot(
                    input: rootInput,
                    identity: root.Identity,
                    state: root.State,
                    observation: root.State == MaterializationRootState.Present
                        ? new(root.Identity, rootShape, fields: [])
                        : null))
                .ToImmutableArray());
        }

        public ValueTask<ImmutableArray<MaterializationRootProjection>> HydrateAsync(
            OperationContext context,
            MaterializationImpactHydrationRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            return ValueTask.FromResult(request.Roots
                .Select(root => new MaterializationRootProjection(
                    root,
                    root.State == MaterializationRootState.Absent
                        ? null
                        : new RelationQueryOutputRow(
                            shape: outputShape,
                            value: ObservationValue.FromObject(
                                new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                                {
                                    ["id"] = ObservationValue.FromString(root.Identity)
                                }),
                            identity: ObservationValue.FromString(root.Identity),
                            root: null,
                            inputOccurrences: [],
                            unresolvedGaps: [])))
                .ToImmutableArray());
        }
    }
}
