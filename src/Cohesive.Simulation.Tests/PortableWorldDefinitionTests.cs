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
    [InlineData("count", "simulation.world.document.contentInvalid")]
    public void InvalidPortableWorlds_ProduceStructuredDiagnostics(string scenario, string expectedCode)
    {
        var json = WorldDefinitionJsonSerializer.Serialize(World(["customers", "orders"]));
        var invalid = scenario switch
        {
            "fingerprint" => Mutate(json, root =>
                root["fingerprint"]!["value"] = new string('0', 64)),
            "schema" => Mutate(json, root =>
                root["schemaVersion"] = "cohesive-simulation-world/v999"),
            "unknown" => Mutate(json, root => root["unexpected"] = true),
            "order" => Mutate(json, ReversePopulations),
            "count" => Mutate(json, root =>
                root["definition"]!["populations"]![0]!["count"] = -1),
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

    static WorldDefinition World(IReadOnlyList<string> order)
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

    public sealed record PortableCustomer(string Name, int Age);

    public sealed record PortableOrder(int Number);
}
