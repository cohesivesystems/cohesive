using System.Text;
using System.Text.Json;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;

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
                .Population("customers", count: 3, generation))
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

        Assert.Equal(firstOutput.ToArray(), secondOutput.ToArray());
        var lines = Encoding.UTF8.GetString(firstOutput.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            using var document = JsonDocument.Parse(lines[index]);
            var root = document.RootElement;
            Assert.Equal(WorldJsonLinesSink.Format, root.GetProperty("format").GetString());
            Assert.Equal(first.RunId.Value, root.GetProperty("runId").GetString());
            Assert.Equal("-9223372036854775808", root.GetProperty("rootSeed").GetString());
            Assert.Equal("customers", root.GetProperty("populationId").GetString());
            Assert.Equal(index, root.GetProperty("sequenceIndex").GetInt64());

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
