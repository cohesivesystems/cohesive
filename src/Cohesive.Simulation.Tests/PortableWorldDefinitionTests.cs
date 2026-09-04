using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Tests;

public sealed class PortableWorldDefinitionTests
{
    [Fact]
    public void AuthoredWorld_RoundTripsForScriptsWithoutChangingPopulationGeneration()
    {
        var authored = World(["orders", "customers"]);
        var json = WorldDefinitionJsonSerializer.Serialize(authored);

        var restoredDocument = WorldDefinitionJsonSerializer.Deserialize(json);
        var restored = restoredDocument.Compile();
        var authoredPlan = authored.Compile();

        Assert.Equal(WorldDefinitionDocument.CurrentSchemaVersion, restoredDocument.SchemaVersion);
        Assert.Equal(["customers", "orders"], restored.Populations.Select(static item => item.Definition.Id));
        Assert.All(
            restored.Populations,
            static population => Assert.Equal(
                WorldEntityIdentityPolicy.PopulationSequence,
                population.Definition.EntityIdentity));
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("World-definition JSON did not contain an object.");
        Assert.Equal(
            nameof(WorldEntityIdentitySource.PopulationSequence),
            root["definition"]!["populations"]![0]!["entityIdentity"]!["source"]!.GetValue<string>());
        Assert.Equal(authoredPlan.Fingerprint, restored.Fingerprint);
        Assert.Equal(json, WorldDefinitionJsonSerializer.Serialize(restoredDocument));
        foreach (var authoredPopulation in authoredPlan.Populations)
        {
            var restoredPopulation = restored.GetPopulation(authoredPopulation.Definition.Id);
            Assert.Equal(authoredPopulation.Scope, restoredPopulation.Scope);
            Assert.Equal(
                authoredPopulation.Generate(seed: 42).Select(static item => item.Observation),
                restoredPopulation.Generate(seed: 42).Select(static item => item.Observation));
        }
    }

    [Fact]
    public void EquivalentPopulationDeclarationOrders_ProduceOneCanonicalDocument()
    {
        var first = World(["customers", "orders"]);
        var reordered = World(["orders", "customers"]);

        var firstJson = WorldDefinitionJsonSerializer.Serialize(first);
        var reorderedJson = WorldDefinitionJsonSerializer.Serialize(reordered);

        Assert.Equal(firstJson, reorderedJson);
        Assert.Equal(first.Compile().Fingerprint, reordered.Compile().Fingerprint);
    }

    [Fact]
    public void PortableWorld_RoundTripsNamedExemplarsAndExactResolution()
    {
        var authored = World(
            ["customers", "orders"],
            ["order-for-ui", "customer-for-ui"]);
        var json = WorldDefinitionJsonSerializer.Serialize(authored);

        var restored = WorldDefinitionJsonSerializer.Deserialize(json).Compile();

        Assert.Equal(
            ["customer-for-ui", "order-for-ui"],
            restored.Exemplars.Select(static exemplar => exemplar.Id));
        Assert.Equal(
            authored.Compile().GenerateExemplar("customer-for-ui", seed: 42),
            restored.GenerateExemplar("customer-for-ui", seed: 42));
        Assert.Equal(json, WorldDefinitionJsonSerializer.Serialize(restored.Definition));
    }

    [Fact]
    public void EquivalentExemplarDeclarationOrders_ProduceOneCanonicalDocument()
    {
        var first = World(
            ["customers", "orders"],
            ["customer-for-ui", "order-for-ui"]);
        var reordered = World(
            ["customers", "orders"],
            ["order-for-ui", "customer-for-ui"]);

        Assert.Equal(
            WorldDefinitionJsonSerializer.Serialize(first),
            WorldDefinitionJsonSerializer.Serialize(reordered));
        Assert.Equal(first.Compile().Fingerprint, reordered.Compile().Fingerprint);
    }

    [Fact]
    public void IndentedWorld_RestoresToTheSameCanonicalCompactDocument()
    {
        var document = WorldDefinitionDocument.FromDefinition(World(["customers", "orders"]));
        var indented = WorldDefinitionJsonSerializer.Serialize(
            document,
            PortableDocumentJsonFormatting.Indented);

        var restored = WorldDefinitionJsonSerializer.Deserialize(indented);

        Assert.Equal(
            WorldDefinitionJsonSerializer.Serialize(document),
            WorldDefinitionJsonSerializer.Serialize(restored));
    }

    [Theory]
    [InlineData("fingerprint", "simulation.world.document.contentInvalid")]
    [InlineData("schema", "simulation.world.document.contentInvalid")]
    [InlineData("unknown", "simulation.world.document.contentInvalid")]
    [InlineData("order", "simulation.world.document.wireNonCanonical")]
    [InlineData("exemplar-order", "simulation.world.document.wireNonCanonical")]
    [InlineData("count", "simulation.world.document.contentInvalid")]
    [InlineData("identity", "simulation.world.document.contentInvalid")]
    public void InvalidPortableWorlds_ProduceStructuredDiagnostics(string scenario, string expectedCode)
    {
        var json = WorldDefinitionJsonSerializer.Serialize(World(
            ["customers", "orders"],
            ["customer-for-ui", "order-for-ui"]));
        var invalid = scenario switch
        {
            "fingerprint" => Mutate(json, root =>
                root["fingerprint"]!["value"] = new string('0', 64)),
            "schema" => Mutate(json, root =>
                root["schemaVersion"] = "cohesive-simulation-world/v999"),
            "unknown" => Mutate(json, root => root["unexpected"] = true),
            "order" => Mutate(json, ReversePopulations),
            "exemplar-order" => Mutate(json, ReverseExemplars),
            "count" => Mutate(json, root =>
                root["definition"]!["populations"]![0]!["count"] = -1),
            "identity" => Mutate(json, root =>
                root["definition"]!["populations"]![0]!["entityIdentity"]!["source"] =
                    nameof(WorldEntityIdentitySource.UniqueObservationField)),
            _ => throw new InvalidOperationException($"Unknown invalid-world scenario '{scenario}'.")
        };

        var result = WorldDefinitionJsonSerializer.TryDeserialize(invalid, out var document);

        Assert.Null(document);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void WorldFingerprint_IncludesPopulationCountAndNestedGenerationRevision()
    {
        var baseline = World(["customers", "orders"]);
        var changedCount = new WorldDefinition(
            baseline.Id,
            baseline.Revision,
            [
                new("customers", 4, baseline.Populations.Single(item => item.Id == "customers").Generation),
                baseline.Populations.Single(item => item.Id == "orders")
            ]);
        var customer = baseline.Populations.Single(item => item.Id == "customers");
        var changedGeneration = new GenerationDefinition(
            customer.Generation.Id,
            "another-revision",
            customer.Generation.ShapeGraph,
            customer.Generation.Root);
        var changedRevision = new WorldDefinition(
            baseline.Id,
            baseline.Revision,
            [
                new("customers", customer.Count, changedGeneration),
                baseline.Populations.Single(item => item.Id == "orders")
            ]);

        Assert.NotEqual(baseline.Compile().Fingerprint, changedCount.Compile().Fingerprint);
        Assert.NotEqual(baseline.Compile().Fingerprint, changedRevision.Compile().Fingerprint);
    }

    [Fact]
    public void WorldFingerprint_IncludesExemplarIdentityAndCoordinates()
    {
        var baseline = World(["customers", "orders"], ["customer-for-ui"]);
        var moved = new WorldDefinition(
            baseline.Id,
            baseline.Revision,
            baseline.Populations,
            [new("customer-for-ui", "customers", sequenceIndex: 2)]);
        var renamed = new WorldDefinition(
            baseline.Id,
            baseline.Revision,
            baseline.Populations,
            [new("renamed-customer", "customers", sequenceIndex: 1)]);

        Assert.NotEqual(baseline.Compile().Fingerprint, moved.Compile().Fingerprint);
        Assert.NotEqual(baseline.Compile().Fingerprint, renamed.Compile().Fingerprint);
    }

    static WorldDefinition World(
        IReadOnlyList<string> order,
        IReadOnlyList<string>? exemplarOrder = null)
    {
        var customers = Simulation.Define<PortableCustomer>(customer => customer
            .Member(value => value.Name, Gen.Constant("Ada"))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
        var orders = Simulation.Define<PortableOrder>(order => order
            .Member(value => value.Number, Gen.Int32(minimum: 1, maximum: 1_000)));
        return Simulation.DefineWorld("world/portable", "r1", world =>
        {
            foreach (var population in order)
            {
                switch (population)
                {
                    case "customers":
                        world.Population("customers", count: 3, customers);
                        break;
                    case "orders":
                        world.Population("orders", count: 5, orders);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown population '{population}'.");
                }
            }

            foreach (var exemplar in exemplarOrder ?? [])
            {
                switch (exemplar)
                {
                    case "customer-for-ui":
                        world.Exemplar(exemplar, "customers", sequenceIndex: 1);
                        break;
                    case "order-for-ui":
                        world.Exemplar(exemplar, "orders", sequenceIndex: 4);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown exemplar '{exemplar}'.");
                }
            }
        });
    }

    static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("World-definition JSON did not contain an object.");
        mutate(root);
        return root.ToJsonString();
    }

    static void ReversePopulations(JsonObject root)
    {
        var populations = root["definition"]?["populations"]?.AsArray()
                          ?? throw new InvalidOperationException("World-definition JSON has no populations array.");
        var reversed = populations
            .Select(static population => population?.DeepClone())
            .Reverse()
            .ToArray();
        populations.Clear();
        foreach (var population in reversed)
            populations.Add(population);
    }

    static void ReverseExemplars(JsonObject root)
    {
        var exemplars = root["definition"]?["exemplars"]?.AsArray()
                        ?? throw new InvalidOperationException("World-definition JSON has no exemplars array.");
        var reversed = exemplars
            .Select(static exemplar => exemplar?.DeepClone())
            .Reverse()
            .ToArray();
        exemplars.Clear();
        foreach (var exemplar in reversed)
            exemplars.Add(exemplar);
    }

    public sealed record PortableCustomer(string Name, int Age);

    public sealed record PortableOrder(int Number);
}
