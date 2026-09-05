using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Simulation;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Relations;
using Cohesive.Simulation.Storage;
using Cohesive.Simulation.Worlds;
using Cohesive.Simulation.Xunit;
using Cohesive.Storage;
using SimulationDsl = Cohesive.Simulation.Simulation;

namespace Cohesive.Examples.SimulationAdoption;

public sealed class SimulationAdoptionExamples
{
    [Fact]
    public async Task OneDefinitionSupportsTestsArtifactsRepositorySeedingAndBrowserFixtures()
    {
        var demo = CreateFreightDemo();

        PropertyCaseRunResult carrierCases = demo.Carriers.Compile().CheckProperty(
            seed: 42,
            property: static carrier => !string.IsNullOrWhiteSpace(carrier.Name));
        PropertyCaseAssert.Passed(carrierCases);

        var plan = demo.World.Compile();
        WorldArtifactManifest artifact = RelationshipWorldArtifact.FromWorld(plan, rootSeed: 42);
        var carrierRepository = RepositoryFor<DemoCarrier>(demo.Shapes);
        var loadRepository = RepositoryFor<DemoLoad>(demo.Shapes);
        var repositorySink = new RepositoryWorldProvisioningSink(
            destinationId: "demo/in-memory",
            operationContext: OperationContext.Create(),
            bindings:
            [
                new("carriers", carrierRepository),
                new("loads", loadRepository)
            ]);

        WorldProvisioningResult seeded = await RelationshipWorldProvisioner.ProvisionAsync(
            artifact,
            repositorySink,
            new(batchSize: 2));

        WorldExemplarDefinition exemplar = artifact.GetExemplar("load-for-browser");
        var loadPopulation = plan.GetPopulation(exemplar.PopulationId);
        EntityId loadId = WorldEntitySequenceIdentityConvention.Create(
            loadPopulation.Population.Scope,
            exemplar.SequenceIndex);
        EntitySnapshot? storedLoad = await loadRepository.TryGet(
            OperationContext.Create(),
            loadId.Value,
            EntityReadOptions.Full);

        Assert.Equal(4, seeded.ItemCount);
        Assert.NotNull(storedLoad);
        var carrierId = storedLoad.Entity.Observation.Value.Fields!["CarrierId"].String;
        Assert.NotNull(carrierId);
        Assert.Contains(
            carrierId,
            plan.GetPopulation("carriers")
                .Population
                .Generate(seed: 42)
                .Select(static carrier => carrier.EntityId.Value));

        await using MemoryStream output = new();
        var artifactSink = new WorldJsonLinesSink("playwright/freight", output);
        await RelationshipWorldProvisioner.ProvisionAsync(artifact, artifactSink, new(batchSize: 2));
        output.Position = 0;
        WorldJsonLinesVerificationResult verified =
            await RelationshipWorldJsonLinesVerifier.VerifyAsync(artifact, output);

        string jsonLines = Encoding.UTF8.GetString(output.ToArray());
        using JsonDocument browserFixture = JsonDocument.Parse(
            jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.Contains("load-for-browser", StringComparison.Ordinal)));

        Assert.Equal(4, verified.ItemCount);
        Assert.Equal(loadId.Value, browserFixture.RootElement.GetProperty("entityId").GetString());
    }

    static FreightDemo CreateFreightDemo()
    {
        ClrShapeGraphBuildResult shapes = new ClrShapeGraphBuilder()
            .AddShape<DemoCarrier>(ShapeRoles.Entity)
            .AddShape<DemoLoad>(ShapeRoles.Entity)
            .AddEntityReference<DemoLoad, DemoCarrier>(load => load.CarrierId)
            .BuildResult(new GraphId("freight-demo/v1"));
        RelationshipDefinition loadCarrier = Relationship
            .From<DemoLoad>(shapes)
            .Reference(load => load.CarrierId)
            .To(shapes.GetShape<DemoCarrier>());
        RelationshipCatalogDocument relationships = RelationshipCatalogDocument.FromCatalog(
            new([loadCarrier]));
        PocoGenerationDefinition<DemoCarrier> carriers = SimulationDsl.Define<DemoCarrier>(
            shapes,
            carrier => carrier.Member(value => value.Name, Gen.Categorical(
                Gen.Weighted("Northwind", weight: 1d),
                Gen.Weighted("Contoso", weight: 1d))));
        PocoGenerationDefinition<DemoLoad> loads = SimulationDsl.Define<DemoLoad>(
            shapes,
            load => load.Member(value => value.Number, Gen.Int32(minimum: 1, maximum: 10_000)));
        RelationshipWorldDefinition world = SimulationRelations.DefineWorld(
            id: "world/freight-demo",
            revision: "r1",
            relationshipCatalog: relationships,
            configure: world => world
                .Population("carriers", count: 2, carriers)
                .Population("loads", count: 2, loads)
                .Relationship("loads", loadCarrier.Id, "carriers")
                .Exemplar("load-for-browser", "loads", sequenceIndex: 1));
        return new(shapes, carriers, world);
    }

    static InMemoryEntityOutboxRepository RepositoryFor<T>(ClrShapeGraphBuildResult shapes)
    {
        var shape = shapes.GetShape<T>();
        var entity = new EntityDefinition(
            new(typeof(T).Name),
            new EntityShapeGraphBinding(
                shape.QualifiedId,
                ShapeGraphDocument.FromGraph(shape.Graph)));
        return new(entity, EntityPartitionKeyPolicy.ObservationId);
    }

    sealed record FreightDemo(
        ClrShapeGraphBuildResult Shapes,
        PocoGenerationDefinition<DemoCarrier> Carriers,
        RelationshipWorldDefinition World);

    [ShapeDefinition("Carrier", ShapeRoles.Entity)]
    sealed record DemoCarrier(string Name);

    [ShapeDefinition("Load", ShapeRoles.Entity)]
    sealed record DemoLoad(int Number, string CarrierId);
}
