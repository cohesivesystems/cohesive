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
    public async Task StopMove_ExtractsBeforeAndAfterOrderReferencesBeforeReadingCurrentRoots()
    {
        var fixture = Fixture();
        var route = fixture.Plan.Routes.First(candidate =>
            candidate.ChangeShape == FreightOrderMaterializationModel.OrderStopShapeId
            && candidate.Strategy is MaterializationInverseTraversalImpactStrategy inverse
            && inverse.Steps.Any(static step =>
                step.Operation == MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction));
        var strategy = Assert.IsType<MaterializationInverseTraversalImpactStrategy>(route.Strategy);
        var extraction = strategy.Steps.Single(static step =>
            step.Operation == MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction);
        var reference = RelationshipReference(fixture.Semantics, extraction.RelationshipInput);
        var requests = new List<MaterializationImpactObservationReadRequest>();
        var executor = new MaterializationInverseTraversalExecutor(
            plan: fixture.Plan,
            definition: fixture.Semantics.Definition,
            reader: (context, request) =>
            {
                requests.Add(request);
                return ValueTask.FromResult(Complete(
                    request.Keys.Select(identity => Observation(
                        identity: identity,
                        shape: FreightOrderMaterializationModel.OrderShapeId))));
            });
        var before = Observation(
            identity: "stop-moved",
            shape: route.ChangeShape,
            input: extraction.RelationshipInput,
            reference: reference,
            value: "order-a");
        var after = Observation(
            identity: "stop-moved",
            shape: route.ChangeShape,
            input: extraction.RelationshipInput,
            reference: reference,
            value: "order-b");

        var roots = await executor.ResolveRootsAsync(
            context: OperationContext.Create(),
            request: Request(fixture, route, before, after));

        var read = Assert.Single(requests);
        Assert.Equal(MaterializationImpactObservationReadKind.IdentityLookup, read.Kind);
        Assert.Equal(["order-a", "order-b"], read.Keys.ToArray());
        Assert.Equal(["order-a", "order-b"], roots.Select(static root => root.Identity).ToArray());
        Assert.All(roots, static root => Assert.Equal(MaterializationRootState.Present, root.State));
    }

    [Fact]
    public async Task LocationChange_UsesStopPredicateThenOrderReferenceThenCurrentRootRead()
    {
        var fixture = Fixture();
        var route = fixture.Plan.Routes.First(candidate =>
            candidate.ChangeShape == FreightOrderMaterializationModel.LocationShapeId);
        var strategy = Assert.IsType<MaterializationInverseTraversalImpactStrategy>(route.Strategy);
        var predicate = strategy.Steps.Single(static step =>
            step.Operation == MaterializationInverseImpactOperationKind.PredicateLookup);
        var extraction = strategy.Steps.Single(static step =>
            step.Operation == MaterializationInverseImpactOperationKind.CurrentRelationshipReferenceExtraction);
        var orderReference = RelationshipReference(fixture.Semantics, extraction.RelationshipInput);
        var requests = new List<MaterializationImpactObservationReadRequest>();
        var executor = new MaterializationInverseTraversalExecutor(
            plan: fixture.Plan,
            definition: fixture.Semantics.Definition,
            reader: (context, request) =>
            {
                requests.Add(request);
                if (request.Kind == MaterializationImpactObservationReadKind.RelationshipPredicateLookup)
                {
                    return ValueTask.FromResult(Complete([
                        Observation(
                            identity: "stop-at-location",
                            shape: FreightOrderMaterializationModel.OrderStopShapeId,
                            input: extraction.RelationshipInput,
                            reference: orderReference,
                            value: "order-at-location")
                    ]));
                }
                return ValueTask.FromResult(Complete([
                    Observation(
                        identity: "order-at-location",
                        shape: FreightOrderMaterializationModel.OrderShapeId)
                ]));
            });
        var location = Observation(identity: "location-a", shape: route.ChangeShape);

        var roots = await executor.ResolveRootsAsync(
            context: OperationContext.Create(),
            request: Request(fixture, route, location, location));

        Assert.Collection(
            requests,
            read =>
            {
                Assert.Equal(MaterializationImpactObservationReadKind.RelationshipPredicateLookup, read.Kind);
                Assert.Equal(predicate.ReferenceSourceInput, read.Input);
                Assert.Equal(["location-a"], read.Keys.ToArray());
            },
            read =>
            {
                Assert.Equal(MaterializationImpactObservationReadKind.IdentityLookup, read.Kind);
                Assert.Equal(fixture.Semantics.Root.Input.Id, read.Input);
                Assert.Equal(["order-at-location"], read.Keys.ToArray());
            });
        var root = Assert.Single(roots);
        Assert.Equal("order-at-location", root.Identity);
        Assert.Equal(MaterializationRootState.Present, root.State);
    }

    [Fact]
    public async Task PartialProviderEvidence_FailsClosedBeforeCurrentRootHydration()
    {
        var fixture = Fixture();
        var route = fixture.Plan.Routes.Single(candidate =>
            candidate.ChangeShape == FreightOrderMaterializationModel.CustomerAccountShapeId);
        var requests = new List<MaterializationImpactObservationReadRequest>();
        var executor = new MaterializationInverseTraversalExecutor(
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
        var provider = Program.CreateProviderPlan(Program.ProviderKind.Postgres, semantics);
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
