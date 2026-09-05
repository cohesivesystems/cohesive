using System.Text.Json.Nodes;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Tests;

public sealed class WorldArtifactManifestTests
{
    [Fact]
    public void Manifest_RoundTripsExactWorldRunAndDiscoveryEvidence()
    {
        var manifest = WorldArtifactManifest.FromWorld(
            World(["orders", "customers"], ["order-for-ui", "customer-for-ui"]).Compile(),
            rootSeed: long.MinValue);

        var json = WorldArtifactManifestJsonSerializer.Serialize(manifest);
        var restored = WorldArtifactManifestJsonSerializer.Deserialize(json);

        Assert.Equal(WorldArtifactManifest.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.StartsWith("csimartifact1_", restored.ArtifactId.Value, StringComparison.Ordinal);
        Assert.Equal(long.MinValue, restored.RootSeed);
        Assert.Equal(ReferenceGenerationInterpreter.Identity, restored.Interpreter);
        Assert.Equal(ReferenceGenerationInterpreter.EntropyAlgorithm, restored.EntropyAlgorithm);
        Assert.Equal(["customers", "orders"], restored.Populations.Select(static item => item.Id));
        Assert.Equal(["customer-for-ui", "order-for-ui"], restored.Exemplars.Select(static item => item.Id));
        Assert.Equal(
            new WorldExemplarDefinition("customer-for-ui", "customers", sequenceIndex: 1),
            restored.GetExemplar("customer-for-ui"));
        Assert.Equal(
            manifest.GetCoreWorld().Compile().GenerateExemplar("customer-for-ui", seed: manifest.RootSeed),
            restored.GetCoreWorld().Compile().GenerateExemplar("customer-for-ui", seed: restored.RootSeed));
        Assert.Equal(json, WorldArtifactManifestJsonSerializer.Serialize(restored));
        Assert.Contains("\"rootSeed\":\"-9223372036854775808\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EquivalentDeclarationOrders_ProduceOneTargetIndependentArtifactIdentity()
    {
        var first = WorldArtifactManifest.FromWorld(
            World(["customers", "orders"], ["customer-for-ui", "order-for-ui"]).Compile(),
            rootSeed: 42);
        var reordered = WorldArtifactManifest.FromWorld(
            World(["orders", "customers"], ["order-for-ui", "customer-for-ui"]).Compile(),
            rootSeed: 42);

        Assert.Equal(first.ArtifactId, reordered.ArtifactId);
        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.Equal(
            WorldArtifactManifestJsonSerializer.Serialize(first),
            WorldArtifactManifestJsonSerializer.Serialize(reordered));
    }

    [Fact]
    public void RootSeed_ParticipatesInArtifactIdentity()
    {
        var world = World(["customers", "orders"]);

        var first = WorldArtifactManifest.FromWorld(world.Compile(), rootSeed: 41);
        var second = WorldArtifactManifest.FromWorld(world.Compile(), rootSeed: 42);

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.ArtifactId, second.ArtifactId);
    }

    [Theory]
    [InlineData("root-seed", "simulation.worldArtifact.manifest.contentInvalid")]
    [InlineData("fingerprint", "simulation.worldArtifact.manifest.contentInvalid")]
    [InlineData("artifact-id", "simulation.worldArtifact.manifest.contentInvalid")]
    [InlineData("population", "simulation.worldArtifact.manifest.contentInvalid")]
    [InlineData("world-revision", "simulation.worldArtifact.manifest.contentInvalid")]
    [InlineData("unknown", "simulation.worldArtifact.manifest.contentInvalid")]
    [InlineData("population-order", "simulation.worldArtifact.manifest.wireNonCanonical")]
    [InlineData("exemplar-order", "simulation.worldArtifact.manifest.wireNonCanonical")]
    public void InvalidOrNoncanonicalManifests_ProduceStructuredDiagnostics(
        string scenario,
        string expectedCode)
    {
        var manifest = WorldArtifactManifest.FromWorld(
            World(["customers", "orders"], ["customer-for-ui", "order-for-ui"]).Compile(),
            rootSeed: 42);
        var json = WorldArtifactManifestJsonSerializer.Serialize(manifest);
        var invalid = scenario switch
        {
            "root-seed" => Mutate(json, root => root["rootSeed"] = "43"),
            "fingerprint" => Mutate(json, root =>
                root["fingerprint"]!["value"] = new string('0', 64)),
            "artifact-id" => Mutate(json, root => root["artifactId"] = "csimartifact1_wrong"),
            "population" => Mutate(json, root => root["populations"]![0]!["count"] = 999),
            "world-revision" => Mutate(json, root => root["world"]!["document"]!["definition"]!["revision"] = "r2"),
            "unknown" => Mutate(json, root => root["unexpected"] = true),
            "population-order" => Mutate(json, root => Reverse(root, "populations")),
            "exemplar-order" => Mutate(json, root => Reverse(root, "exemplars")),
            _ => throw new InvalidOperationException($"Unknown invalid-manifest scenario '{scenario}'.")
        };

        var validation = WorldArtifactManifestJsonSerializer.TryDeserialize(invalid, out var restored);

        Assert.Null(restored);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void ManifestCreation_DoesNotMaterializeDeclaredPopulationMembers()
    {
        var generation = Simulation.Define<ManifestCustomer>(customer => customer
            .Member(value => value.Name, Gen.Constant("Ada")));
        var world = Simulation.DefineWorld("world/large-manifest", "r1", builder => builder
            .Population("customers", count: int.MaxValue, generation));

        var manifest = WorldArtifactManifest.FromWorld(world.Compile(), rootSeed: 42);
        var json = WorldArtifactManifestJsonSerializer.Serialize(manifest);

        Assert.Equal(int.MaxValue, Assert.Single(manifest.Populations).Count);
        Assert.True(json.Length < 100_000, $"Manifest unexpectedly contained '{json.Length}' characters.");
    }

    [Fact]
    public void ExemplarLookup_ReportsMissingStableIdentity()
    {
        var manifest = WorldArtifactManifest.FromWorld(
            World(["customers"], ["customer-for-ui"]).Compile(),
            rootSeed: 42);

        Assert.False(manifest.TryGetExemplar("missing", out var missing));
        Assert.Null(missing);
        var exception = Assert.Throws<KeyNotFoundException>(() => manifest.GetExemplar("missing"));
        Assert.Contains(manifest.ArtifactId.Value, exception.Message, StringComparison.Ordinal);
    }

    static WorldDefinition World(
        IReadOnlyList<string> populationOrder,
        IReadOnlyList<string>? exemplarOrder = null)
    {
        var customers = Simulation.Define<ManifestCustomer>(customer => customer
            .Member(value => value.Name, Gen.Constant("Ada")));
        var orders = Simulation.Define<ManifestOrder>(order => order
            .Member(value => value.Number, Gen.Int32(minimum: 1, maximum: 1_000)));
        return Simulation.DefineWorld("world/artifact", "r1", builder =>
        {
            foreach (var population in populationOrder)
            {
                switch (population)
                {
                    case "customers":
                        builder.Population("customers", count: 3, customers);
                        break;
                    case "orders":
                        builder.Population("orders", count: 5, orders);
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
                        builder.Exemplar(exemplar, "customers", sequenceIndex: 1);
                        break;
                    case "order-for-ui":
                        builder.Exemplar(exemplar, "orders", sequenceIndex: 4);
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
                   ?? throw new InvalidOperationException("World-artifact manifest JSON did not contain an object.");
        mutate(root);
        return root.ToJsonString();
    }

    static void Reverse(JsonObject root, string propertyName)
    {
        var values = root[propertyName]?.AsArray()
                     ?? throw new InvalidOperationException($"Manifest JSON has no '{propertyName}' array.");
        var reversed = values
            .Select(static value => value?.DeepClone())
            .Reverse()
            .ToArray();
        values.Clear();
        foreach (var value in reversed)
        {
            values.Add(value);
        }
    }

    public sealed record ManifestCustomer(string Name);

    public sealed record ManifestOrder(int Number);
}
