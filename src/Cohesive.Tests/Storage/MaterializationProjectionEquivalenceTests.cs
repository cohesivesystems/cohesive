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
    public void RootProjection_RejectsOutputForAuthoritativelyAbsentRoot()
    {
        var fixture = CreateFixture();
        var root = new MaterializationAffectedRoot(
            input: fixture.Root.Input.Id,
            identity: "deleted-root",
            state: MaterializationRootState.Absent,
            observation: null);
        var row = ProjectionRow(
            shape: fixture.Plan.Materialization.Definition.Relation.Output.Shape,
            identity: root.Identity,
            marker: "must-not-exist");

        Assert.Throws<ArgumentException>(() => new MaterializationRootProjection(root, row));
    }

    [Fact]
    public async Task ImpactInterpreter_RejectsHydrationThatSubstitutesRootState()
    {
        var fixture = CreateFixture();
        var feed = RootFeed(fixture);
        var deleted = Observation(feed, identity: "deleted-root");
        var page = new MaterializationChangePage(
            deliveries:
            [
                Delivery(
                    feed: feed,
                    ordinal: 1,
                    kind: MaterializationChangeKind.Delete,
                    before: deleted,
                    after: null)
            ],
            throughPosition: Position(feed, value: "delivery/1"),
            state: MaterializationChangePageState.CaughtUp);
        var runtime = new ProjectionRuntime(
            fixture.Plan.ImpactPlan.Fingerprint,
            request:
            request =>
            {
                var requested = Assert.Single(request.Roots);
                var substituted = new MaterializationAffectedRoot(
                    input: requested.Input,
                    identity: requested.Identity,
                    state: MaterializationRootState.Present,
                    observation: deleted);
                return
                [
                    new MaterializationRootProjection(
                        substituted,
                        ProjectionRow(
                            shape: fixture.Plan.Materialization.Definition.Relation.Output.Shape,
                            identity: substituted.Identity,
                            marker: "resurrected"))
                ];
            });
        var interpreter = new MaterializationImpactPlanInterpreter(
            fixture.Plan.ImpactPlan,
            fixture.Plan.Materialization.Definition,
            runtime);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await interpreter.InterpretAsync(
                OperationContext.Create(),
                feed,
                new MaterializationGenerationId("generation/projection-state"),
                page));
    }

    [Fact]
    public async Task ImpactInterpreter_RejectsHydrationWithAnotherOutputShape()
    {
        var fixture = CreateFixture();
        var feed = RootFeed(fixture);
        var created = Observation(feed, identity: "created-root");
        var page = new MaterializationChangePage(
            deliveries:
            [
                Delivery(
                    feed: feed,
                    ordinal: 1,
                    kind: MaterializationChangeKind.Create,
                    before: null,
                    after: created)
            ],
            throughPosition: Position(feed, value: "delivery/1"),
            state: MaterializationChangePageState.CaughtUp);
        var wrongShape = new QualifiedShapeId(new("tests/projection"), new("WrongOutput"));
        var runtime = new ProjectionRuntime(
            fixture.Plan.ImpactPlan.Fingerprint,
            request:
            request =>
            {
                var requested = Assert.Single(request.Roots);
                return
                [
                    new MaterializationRootProjection(
                        requested,
                        ProjectionRow(
                            shape: wrongShape,
                            identity: requested.Identity,
                            marker: "wrong-shape"))
                ];
            });
        var interpreter = new MaterializationImpactPlanInterpreter(
            fixture.Plan.ImpactPlan,
            fixture.Plan.Materialization.Definition,
            runtime);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await interpreter.InterpretAsync(
                OperationContext.Create(),
                feed,
                new MaterializationGenerationId("generation/projection-shape"),
                page));
    }

    [Fact]
    public async Task ImpactInterpreter_ReportsTypedCompiledRootBoundFailure()
    {
        var fixture = CreateFixture();
        var feed = ContributorFeed(fixture);
        var changed = Observation(feed, identity: "contributor-root");
        var page = new MaterializationChangePage(
            deliveries:
            [
                Delivery(
                    feed: feed,
                    ordinal: 1,
                    kind: MaterializationChangeKind.Upsert,
                    before: null,
                    after: changed)
            ],
            throughPosition: Position(feed, value: "delivery/1"),
            state: MaterializationChangePageState.CaughtUp);
        var route = fixture.Plan.ImpactPlan.Routes.Single(candidate => candidate.ChangeInput == feed.Scope.Input);
        var runtime = new BoundExceededRuntime(
            impactPlan: fixture.Plan.ImpactPlan.Fingerprint,
            rootInput: fixture.Root.Input.Id,
            rootShape: fixture.Root.Shape,
            count: checked((int)route.MaximumAffectedRoots + 1));
        var interpreter = new MaterializationImpactPlanInterpreter(
            fixture.Plan.ImpactPlan,
            fixture.Plan.Materialization.Definition,
            runtime);

        var exception = await Assert.ThrowsAsync<MaterializationAffectedRootBoundExceededException>(async () =>
            await interpreter.InterpretAsync(
                OperationContext.Create(),
                feed,
                new MaterializationGenerationId("generation/projection-bound"),
                page));

        Assert.Equal(route.ChangeInput, exception.ChangeInput);
        Assert.Equal(route.MaximumAffectedRoots, exception.MaximumAffectedRoots);
        Assert.Equal(route.MaximumAffectedRoots + 1, exception.ActualAffectedRoots);
    }

    [Fact]
    public async Task BaselineAndIncrementalUpdateDeleteUseOneRootItemIdentity()
    {
        var harness = await CreateSynchronizationHarnessAsync();
        var baselineItems = await Items(harness.Rebuild.Target, harness.Generation);
        var baseline = baselineItems[0];
        var rootIdentity = baseline.Value!.Value.GetProperty("id").GetString()!;
        var canonicalItem = MaterializationItemIdentity.FromRootIdentity(rootIdentity);
        var feed = RootFeed(harness.Rebuild);
        var runtime = ImpactRuntime(harness.Rebuild);
        var workStore = new InMemoryMaterializationSynchronizationWorkStore();
        var root = Observation(feed, identity: rootIdentity);
        var update = Delivery(
            feed: feed,
            ordinal: 1,
            kind: MaterializationChangeKind.Update,
            before: root,
            after: root);
        runtime.OutputMarkers[rootIdentity] = "incremental";

        var updated = await Executor(
                harness,
                feed,
                Source(harness.Rebuild, feed, [update]),
                runtime,
                workStore)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("baseline-equivalence/update"),
                Worker("baseline-equivalence"));
        var updatedItems = await Items(harness.Rebuild.Target, harness.Generation);
        var updatedItem = Assert.Single(updatedItems, item => item.ItemId == baseline.ItemId);

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, updated.Disposition);
        Assert.Equal(canonicalItem, baseline.ItemId);
        Assert.Equal(baselineItems.Length, updatedItems.Length);
        Assert.Equal("incremental", updatedItem.Value!.Value.GetProperty("marker").GetString());

        var delete = Delivery(
            feed: feed,
            ordinal: 2,
            kind: MaterializationChangeKind.Delete,
            before: root,
            after: null);
        var deleted = await Executor(
                harness,
                feed,
                Source(harness.Rebuild, feed, [update, delete]),
                runtime,
                workStore)
            .RunFeedAsync(
                OperationContext.Create(),
                harness.Attempt,
                feed.Id,
                Invocation("baseline-equivalence/delete"),
                Worker("baseline-equivalence"));
        var deletedItems = await Items(harness.Rebuild.Target, harness.Generation);
        var deletedItem = Assert.Single(deletedItems, item => item.ItemId == baseline.ItemId);

        Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, deleted.Disposition);
        Assert.Equal(baselineItems.Length, deletedItems.Length);
        Assert.Equal(canonicalItem, deletedItem.ItemId);
        Assert.Null(deletedItem.Value);
    }

    static RelationQueryOutputRow ProjectionRow(
        QualifiedShapeId shape,
        string identity,
        string marker) =>
        new(
            shape: shape,
            value: ObservationValue.FromObject(
                new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["id"] = ObservationValue.FromString(identity),
                    ["marker"] = ObservationValue.FromString(marker)
                }),
            identity: ObservationValue.FromString(identity),
            root: null,
            inputOccurrences: [],
            unresolvedGaps: []);

    sealed class ProjectionRuntime(
        MaterializationImpactPlanFingerprint impactPlan,
        Func<MaterializationImpactHydrationRequest, ImmutableArray<MaterializationRootProjection>> request)
        : IMaterializationImpactRuntime
    {
        public MaterializationImpactPlanFingerprint ImpactPlan { get; } = impactPlan;

        public ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
            OperationContext context,
            MaterializationImpactRootResolutionRequest request) =>
            throw new InvalidOperationException("Direct-root projection tests do not perform inverse impact resolution.");

        public ValueTask<ImmutableArray<MaterializationRootProjection>> HydrateAsync(
            OperationContext context,
            MaterializationImpactHydrationRequest hydration)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(hydration);
            context.ThrowIfCancellationRequested();
            return ValueTask.FromResult(request(hydration));
        }
    }

    sealed class BoundExceededRuntime(
        MaterializationImpactPlanFingerprint impactPlan,
        RelationQueryInputId rootInput,
        QualifiedShapeId rootShape,
        int count) : IMaterializationImpactRuntime
    {
        public MaterializationImpactPlanFingerprint ImpactPlan { get; } = impactPlan;

        public ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
            OperationContext context,
            MaterializationImpactRootResolutionRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                Enumerable.Range(start: 0, count: count)
                    .Select(index =>
                    {
                        var identity = $"affected-root/{index}";
                        return new MaterializationAffectedRoot(
                            input: rootInput,
                            identity: identity,
                            state: MaterializationRootState.Present,
                            observation: new(
                                identity: identity,
                                shape: rootShape,
                                fields: []));
                    })
                    .ToImmutableArray());
        }

        public ValueTask<ImmutableArray<MaterializationRootProjection>> HydrateAsync(
            OperationContext context,
            MaterializationImpactHydrationRequest request) =>
            throw new InvalidOperationException("A compiled-bound failure must occur before hydration.");
    }
}
