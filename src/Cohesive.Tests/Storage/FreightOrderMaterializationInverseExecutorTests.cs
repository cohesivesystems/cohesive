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
    public async Task LocationChange_EnumeratesCompleteBoundedRootSetInCanonicalOrder()
    {
        var fixture = Fixture();
        var route = fixture.Plan.Routes.First(candidate =>
            candidate.ChangeShape == FreightOrderMaterializationModel.LocationShapeId);
        var strategy = Assert.IsType<MaterializationBoundedGlobalImpactStrategy>(route.Strategy);
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

        var read = Assert.Single(requests);
        Assert.Equal(MaterializationImpactObservationReadKind.BoundedEnumeration, read.Kind);
        Assert.Equal(strategy.RootInput, read.Input);
        Assert.Empty(read.Keys);
        Assert.Equal(route.MaximumAffectedRoots, read.MaximumRows);
        Assert.Equal(["order-a", "order-b"], roots.Select(static root => root.Identity).ToArray());
        Assert.All(roots, static root => Assert.Equal(MaterializationRootState.Present, root.State));
    }

    [Fact]
    public async Task BoundedGlobalInvalidation_FailsClosedOnPartialEnumeration()
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
        Assert.Equal(MaterializationImpactObservationReadKind.BoundedEnumeration, read.Kind);
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
