using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Materialize;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class FreightOrderMaterializationInverseExecutorTests
{
    static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task LocationChange_UsesExactOwnedStopOccurrenceRoutesInCanonicalOrder()
    {
        var fixture = Fixture();
        var routes = fixture.Plan.Routes.Where(candidate =>
                candidate.ChangeShape == FreightOrderMaterializationModel.LocationShapeId)
            .ToArray();
        Assert.Equal(2, routes.Length);

        foreach (var route in routes)
        {
            var strategy = Assert.IsType<MaterializationInverseTraversalImpactStrategy>(route.Strategy);
            var step = Assert.Single(strategy.Steps);
            Assert.Equal(
                MaterializationInverseImpactOperationKind.CollectionElementPredicateLookup,
                step.Operation);
            var occurrence = Assert.IsType<MaterializationCollectionOccurrenceReference>(
                step.CollectionOccurrence);
            var requests = new List<MaterializationImpactObservationReadRequest>();
            var executor = new MaterializationImpactRootExecutor(
                plan: fixture.Plan,
                definition: fixture.Semantics.Definition,
                reader: (context, request) =>
                {
                    requests.Add(request);
                    return ValueTask.FromResult(Complete([
                        Observation("order-b", FreightOrderMaterializationModel.OrderShapeId),
                        Observation("order-a", FreightOrderMaterializationModel.OrderShapeId)
                    ]));
                });
            var location = Observation("location-a", route.ChangeShape);

            var roots = await executor.ResolveRootsAsync(
                context: OperationContext.Create(),
                request: Request(fixture, route, location, location));

            Assert.Equal(2, requests.Count);
            var read = requests[0];
            Assert.Equal(MaterializationImpactObservationReadKind.RelationshipPredicateLookup, read.Kind);
            Assert.Equal(step.ReferenceSourceInput, read.Input);
            Assert.Equal("location-a", Assert.Single(read.Keys));
            Assert.Equal(step.RelationshipInput, read.RelationshipInput);
            Assert.Equal(
                RelationshipReference(fixture.Semantics, step.RelationshipInput),
                read.RelationshipReference);
            Assert.Equal(occurrence, read.CollectionOccurrence);
            Assert.Equal(route.MaximumAffectedRoots, read.MaximumRows);
            Assert.Equal(["order-a", "order-b"], roots.Select(static root => root.Identity).ToArray());
            Assert.All(roots, static root => Assert.Equal(MaterializationRootState.Present, root.State));
            Assert.Equal(MaterializationImpactObservationReadKind.IdentityLookup, requests[1].Kind);
            Assert.Equal(
                ["order-a", "order-b"],
                requests[1].Keys.Cast<string>().ToArray());
        }
    }

    [Fact]
    public async Task CollectionOccurrenceLookup_FailsClosedOnPartialEvidence()
    {
        var fixture = Fixture();
        var route = fixture.Plan.Routes.First(candidate =>
            candidate.ChangeShape == FreightOrderMaterializationModel.LocationShapeId);
        var requests = new List<MaterializationImpactObservationReadRequest>();
        var executor = new MaterializationImpactRootExecutor(
            plan: fixture.Plan,
            definition: fixture.Semantics.Definition,
            reader: (context, request) =>
            {
                requests.Add(request);
                return ValueTask.FromResult(new RelationQuerySourceReadResult(
                    state: RelationQuerySourceReadState.Partial,
                    observations: [],
                    evidenceReference: "tests/freight-impact/partial-enumeration"));
            });
        var location = Observation(identity: "location-a", shape: route.ChangeShape);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ResolveRootsAsync(
                context: OperationContext.Create(),
                request: Request(fixture, route, location, location))
            .AsTask());

        Assert.Contains("complete evidence", exception.Message, StringComparison.Ordinal);
        var read = Assert.Single(requests);
        Assert.Equal(MaterializationImpactObservationReadKind.RelationshipPredicateLookup, read.Kind);
        Assert.NotNull(read.CollectionOccurrence);
    }

    [Fact]
    public async Task PartialProviderEvidence_FailsClosedBeforeCurrentRootHydration()
    {
        var fixture = Fixture();
        var route = fixture.Plan.Routes.Single(candidate =>
            candidate.ChangeShape == FreightOrderMaterializationModel.CustomerAccountShapeId);
        var requests = new List<MaterializationImpactObservationReadRequest>();
        var executor = new MaterializationImpactRootExecutor(
            plan: fixture.Plan,
            definition: fixture.Semantics.Definition,
            reader: (context, request) =>
            {
                requests.Add(request);
                return ValueTask.FromResult(new RelationQuerySourceReadResult(
                    state: RelationQuerySourceReadState.Partial,
                    observations: [],
                    evidenceReference: "tests/freight-inverse/partial"));
            });
        var customer = Observation(identity: "customer-a", shape: route.ChangeShape);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ResolveRootsAsync(
                context: OperationContext.Create(),
                request: Request(fixture, route, customer, customer))
            .AsTask());

        Assert.Contains("complete evidence", exception.Message, StringComparison.Ordinal);
        Assert.Single(requests);
        Assert.Equal(MaterializationImpactObservationReadKind.RelationshipPredicateLookup, requests[0].Kind);
    }

    static FixtureData Fixture()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        var provider = Program.CreateProviderPlan(
            FreightOrderMaterializationReplicaDialects.Get("postgres"),
            semantics);
        return new(semantics, provider, provider.ImpactPlan);
    }

    static MaterializationImpactRootResolutionRequest Request(
        FixtureData fixture,
        MaterializationImpactRoute route,
        RelationQuerySourceReadObservation before,
        RelationQuerySourceReadObservation after)
    {
        var placement = fixture.Provider.ImpactPlacement.Bindings.Single(candidate =>
            candidate.Input == route.ChangeInput);
        var scope = new MaterializationSourceScope(
            physicalPlan: fixture.Provider.ImpactPhysicalPlan.Fingerprint,
            placement: placement,
            logicalPartition: FreightOrderRebuildPlanCompiler.LogicalPartition("acme"),
            partition: new($"tests/freight-inverse/{Uri.EscapeDataString(route.ChangeInput.Value)}"),
            orderingScope: new($"tests/freight-inverse/{Uri.EscapeDataString(route.ChangeInput.Value)}/order"));
        return new(
            plan: fixture.Plan,
            route: route,
            change: new(
                id: new($"tests/freight-inverse/{Uri.EscapeDataString(route.ChangeInput.Value)}/change"),
                subjectIdentity: after.Identity,
                scope: scope,
                shape: route.ChangeShape,
                position: null,
                kind: MaterializationChangeKind.Update,
                before: before,
                after: after,
                occurredAtUtc: Epoch,
                observedAtUtc: Epoch),
            generation: new("tests/freight-inverse/generation"));
    }

    static FieldPath RelationshipReference(
        FreightOrderMaterializationSemantics semantics,
        RelationQueryInputId relationshipInput) =>
        semantics.Plan.InputContract.Traversals.Single(candidate =>
            candidate.Input.Id == relationshipInput).Definition.SourceReference;

    static RelationQuerySourceReadResult Complete(
        IEnumerable<RelationQuerySourceReadObservation> observations) => new(
        state: RelationQuerySourceReadState.Complete,
        observations: [.. observations],
        evidenceReference: "tests/freight-inverse/complete");

    static RelationQuerySourceReadObservation Observation(
        string identity,
        QualifiedShapeId shape,
        RelationQueryInputId? input = null,
        FieldPath? reference = null,
        string? value = null)
    {
        ImmutableArray<RelationQuerySourceReadFieldResult> fields = input is { } fieldInput
            && reference is { } fieldReference
            && value is { } fieldValue
                ?
                [
                    new(
                        field: new(
                            input: fieldInput,
                            semanticPath: fieldReference,
                            sourceSelector: "tests/freight-inverse/reference",
                            purpose: RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation),
                        state: RelationQuerySourceReadFieldState.Value,
                        value: ObservationValue.FromString(fieldValue))
                ]
                : [];
        return new(identity: identity, shape: shape, fields: fields);
    }

    sealed record FixtureData(
        FreightOrderMaterializationSemantics Semantics,
        Program.ProviderPlan Provider,
        MaterializationImpactPlan Plan);
}
