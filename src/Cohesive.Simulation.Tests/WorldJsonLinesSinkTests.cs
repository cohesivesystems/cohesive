using System.Text;
using System.Text.Json;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Tests;

public sealed class WorldJsonLinesSinkTests
{
    [Fact]
    public async Task JsonLines_AreDeterministicJavaScriptSafeAndCarryExactReplayProvenance()
    {
        var generation = Simulation.Define<JsonLineCustomer>(customer => customer
            .Member(value => value.Name, Gen.Constant("Ada"))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
        var world = Simulation.DefineWorld("world/json-lines", "r1", builder => builder
                .Population("customers", count: 3, generation)
                .Exemplar("primary-customer", "customers", sequenceIndex: 0)
                .Exemplar("customer-for-ui", "customers", sequenceIndex: 2)
                .Exemplar("secondary-ui-alias", "customers", sequenceIndex: 2))
            .Compile();
        using MemoryStream firstOutput = new();
        using MemoryStream secondOutput = new();

        var first = await WorldProvisioner.ProvisionAsync(
            world,
            rootSeed: long.MinValue,
            new WorldJsonLinesSink("artifact/demo.jsonl", firstOutput),
            new(batchSize: 2));
        await WorldProvisioner.ProvisionAsync(
            world,
            rootSeed: long.MinValue,
            new WorldJsonLinesSink("artifact/demo.jsonl", secondOutput),
            new(batchSize: 2));
        var retainedManifest = WorldArtifactManifestJsonSerializer.Deserialize(
            WorldArtifactManifestJsonSerializer.Serialize(first.Artifact));
        using MemoryStream verificationInput = new(firstOutput.ToArray(), writable: false);
        var verification = await WorldJsonLinesVerifier.VerifyAsync(retainedManifest, verificationInput);

        Assert.Equal(firstOutput.ToArray(), secondOutput.ToArray());
        Assert.Equal(retainedManifest.ArtifactId, verification.ArtifactId);
        Assert.Equal(3, verification.ItemCount);
        var lines = Encoding.UTF8.GetString(firstOutput.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            using var document = JsonDocument.Parse(lines[index]);
            var root = document.RootElement;
            Assert.Equal(WorldJsonLinesSink.Format, root.GetProperty("format").GetString());
            Assert.Equal(first.RunId.Value, root.GetProperty("runId").GetString());
            Assert.Equal(
                WorldArtifactManifest.CurrentSchemaVersion,
                root.GetProperty("artifactManifestSchema").GetString());
            Assert.Equal(first.ArtifactId.Value, root.GetProperty("artifactId").GetString());
            Assert.Equal(
                retainedManifest.Fingerprint.Algorithm,
                root.GetProperty("artifactManifestFingerprintAlgorithm").GetString());
            Assert.Equal(
                retainedManifest.Fingerprint.Canonicalization,
                root.GetProperty("artifactManifestFingerprintCanonicalization").GetString());
            Assert.Equal(
                retainedManifest.Fingerprint.Value,
                root.GetProperty("artifactManifestFingerprint").GetString());
            Assert.Equal("-9223372036854775808", root.GetProperty("rootSeed").GetString());
            Assert.Equal("customers", root.GetProperty("populationId").GetString());
            Assert.Equal(3, root.GetProperty("populationCount").GetInt32());
            Assert.Equal(2, root.GetProperty("batchSize").GetInt32());
            Assert.Equal(index, root.GetProperty("sequenceIndex").GetInt64());
            Assert.Equal(
                WorldEntitySequenceIdentityConvention.Create(
                    world.GetPopulation("customers").Scope,
                    index).Value,
                root.GetProperty("entityId").GetString());
            Assert.Equal(
                index switch
                {
                    0 => ["primary-customer"],
                    1 => [],
                    2 => ["customer-for-ui", "secondary-ui-alias"],
                    _ => throw new InvalidOperationException()
                },
                root.GetProperty("exemplars")
                    .EnumerateArray()
                    .Select(static item => item.GetString()));

            var replay = GenerationReplayEvidence.ParseToken(root.GetProperty("replayToken").GetString()!);
            var generated = ReferenceGenerationInterpreter.Replay(
                world.GetPopulation("customers").GenerationPlan,
                replay);
            Assert.Equal(index, generated.Replay.SequenceIndex);
            Assert.Equal(
                generated.Observation.ToCanonicalJson(),
                root.GetProperty("observation").GetRawText());
        }
    }

    [Fact]
    public void Constructor_RequiresWritableCallerOwnedStream()
    {
        using MemoryStream input = new([], writable: false);

        Assert.Throws<ArgumentException>(() => new WorldJsonLinesSink("artifact/read-only", input));
    }

    public sealed record JsonLineCustomer(string Name, int Age);
}
