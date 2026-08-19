using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.TestFixtures;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed partial class MaterializationRebuildExecutorTests
{
    [Fact]
    public async Task ProductionRelationsRuntime_RebuildAndIncrementalHydrationRemainEquivalent()
    {
        var fixture = CreateFixture();
        var scenario = FederatedLoadConformanceData.CreatePhysicalScenario(
            fixture.Semantic,
            rootCount: 3,
            distinctCustomerCount: 2,
            distinctEquipmentCount: 2);
        var observations = scenario.SuppliedLoads.Observations[..2];
        var shard = fixture.Plan.Shards[0];
        var page = new MaterializationSourcePage(
            scope: shard.Scope,
            readFingerprint: MaterializationSourceReadFingerprinter.Compute(shard.Read),
            read: new(
                state: RelationQuerySourceReadState.Complete,
                observations: observations,
                evidenceReference: "tests/relations-runtime/rebuild"),
            state: MaterializationSourcePageState.Exhausted);
        var rebuild = new RelationQueryMaterializationRebuildHydrator(
            plan: fixture.Semantic.Plan,
            physicalPlan: fixture.Semantic.PhysicalPlan,
            realization: fixture.Semantic.Realization,
            suppliedRoot: fixture.Root.Input.Id,
            output: fixture.Plan.Materialization.Definition.Relation.Output,
            sourceReaders: scenario.Readers);

        var rebuilt = await rebuild.HydrateAsync(
            OperationContext.Create(),
            new(
                evaluation: new("tests/relations-runtime/rebuild"),
                shard: shard,
                page: page));

        var incremental = new RelationQueryMaterializationImpactRuntime(
            impactPlan: fixture.Plan.ImpactPlan,
            definition: fixture.Plan.Materialization.Definition,
            physicalPlan: fixture.Semantic.PhysicalPlan,
            realization: fixture.Semantic.Realization,
            sourceReaders: scenario.Readers);
        ImmutableArray<MaterializationAffectedRoot> roots =
        [
            .. observations.Select(observation => new MaterializationAffectedRoot(
                input: fixture.Root.Input.Id,
                identity: observation.Identity,
                state: MaterializationRootState.Present,
                observation: observation)),
            new(
                input: fixture.Root.Input.Id,
                identity: "load-99",
                state: MaterializationRootState.Absent,
                observation: null)
        ];
        var hydrated = await incremental.HydrateAsync(
            OperationContext.Create(),
            new(
                evaluation: new("tests/relations-runtime/incremental"),
                logicalPartition: shard.Scope.LogicalPartition,
                roots: roots));

        Assert.Equal(rebuilt.Rows.Length, hydrated.Length - 1);
        for (var index = 0; index < rebuilt.Rows.Length; index++)
        {
            Assert.Equal(rebuilt.Rows[index].Identity, hydrated[index].Row?.Identity);
            Assert.Equal(rebuilt.Rows[index].Value, hydrated[index].Row?.Value);
        }
        Assert.Same(roots[2], hydrated[2].Root);
        Assert.Null(hydrated[2].Row);
    }

    [Fact]
    public async Task ProductionRelationsRuntime_NonDirectRouteRequiresExplicitPhysicalResolver()
    {
        var fixture = CreateFixture();
        var route = fixture.Plan.ImpactPlan.Routes.First(static candidate =>
            candidate.Strategy is not MaterializationDirectRootImpactStrategy);
        var feed = fixture.Plan.ChangeFeeds.First(candidate => candidate.Scope.Input == route.ChangeInput);
        var change = new MaterializationChangeEnvelope(
            id: new("tests/change/inverse"),
            subjectIdentity: "contributor-1",
            scope: feed.Scope,
            shape: feed.Scope.Shape,
            position: null,
            kind: MaterializationChangeKind.Delete,
            before: null,
            after: null,
            occurredAtUtc: Epoch,
            observedAtUtc: Epoch);
        var runtime = new RelationQueryMaterializationImpactRuntime(
            impactPlan: fixture.Plan.ImpactPlan,
            definition: fixture.Plan.Materialization.Definition,
            physicalPlan: fixture.Semantic.PhysicalPlan,
            realization: fixture.Semantic.Realization,
            sourceReaders: fixture.Readers);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResolveRootsAsync(
                OperationContext.Create(),
                new(
                    plan: fixture.Plan.ImpactPlan,
                    route: route,
                    change: change,
                    generation: new("tests/generation")))
            .AsTask());

        Assert.Contains("explicit physical affected-root resolver", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImpactHydrationEvaluation_IsStableForReplayAndFencedByGeneration()
    {
        var fixture = CreateFixture();
        var feed = RootFeed(fixture);
        var root = Observation(feed, "load/evaluation-fence");
        var page = new MaterializationChangePage(
            deliveries:
            [
                Delivery(
                    feed: feed,
                    ordinal: 1,
                    kind: MaterializationChangeKind.Upsert,
                    before: null,
                    after: root)
            ],
            throughPosition: Position(feed, "position/evaluation-fence"),
            state: MaterializationChangePageState.CaughtUp);
        CapturingHydrationRuntime runtime = new(
            fixture.Plan.ImpactPlan.Fingerprint,
            fixture.Plan.Materialization.Definition.Relation.Output.Shape);
        MaterializationImpactPlanInterpreter interpreter = new(
            fixture.Plan.ImpactPlan,
            fixture.Plan.Materialization.Definition,
            runtime);

        await interpreter.InterpretAsync(OperationContext.Create(), feed, new("generation/a"), page);
        await interpreter.InterpretAsync(OperationContext.Create(), feed, new("generation/a"), page);
        await interpreter.InterpretAsync(OperationContext.Create(), feed, new("generation/b"), page);

        Assert.Equal(runtime.Evaluations[0], runtime.Evaluations[1]);
        Assert.NotEqual(runtime.Evaluations[0], runtime.Evaluations[2]);
    }

    sealed class CapturingHydrationRuntime(
        MaterializationImpactPlanFingerprint impactPlan,
        QualifiedShapeId outputShape) : IMaterializationImpactRuntime
    {
        public MaterializationImpactPlanFingerprint ImpactPlan { get; } = impactPlan;

        public List<RelationQueryEvaluationId> Evaluations { get; } = [];

        public ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
            OperationContext context,
            MaterializationImpactRootResolutionRequest request) =>
            throw new InvalidOperationException("The evaluation-fence fixture uses one direct root.");

        public ValueTask<ImmutableArray<MaterializationRootProjection>> HydrateAsync(
            OperationContext context,
            MaterializationImpactHydrationRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            Evaluations.Add(request.Evaluation);
            return ValueTask.FromResult(request.Roots
                .Select(root => new MaterializationRootProjection(
                    root,
                    new RelationQueryOutputRow(
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
