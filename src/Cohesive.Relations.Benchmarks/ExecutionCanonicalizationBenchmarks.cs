using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Compares immutable execution JSON traversal with the previous mutable-tree materialization.</summary>
[MemoryDiagnoser]
public class ExecutionCanonicalizationBenchmarks
{
    ExecutionDefinitionDocument document = null!;
    readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        JsonNode definition = new JsonObject { ["z"] = 12.5, ["a"] = "example", ["enabled"] = true };
        if (Shape == "nested")
            for (var level = 0; level < 24; level++)
                definition = new JsonObject { ["child"] = definition, ["values"] = new JsonArray(1, 2, 3) };
        if (Shape is "collection" or "large")
        {
            var rows = new JsonArray();
            for (var index = 0; index < (Shape == "large" ? 4096 : 128); index++)
                rows.Add(definition.DeepClone());
            definition = new JsonObject { ["rows"] = rows };
        }
        using var json = JsonDocument.Parse(definition.ToJsonString());
        document = ExecutionDefinitionDocument.Create(new("benchmark"), new("definition/1"), new("revision/1"),
            json.RootElement, new(new("benchmark", "1"), new("benchmark", new(["definition"])), DocumentOrigin.Compiled));
        if (!MutableTreeReference().AsSpan().SequenceEqual(ImmutableDocument()))
            throw new InvalidOperationException("Canonical bytes differ.");
    }

    [Benchmark(Baseline = true)]
    public byte[] MutableTreeReference() => CanonicalJsonWriter.GetCanonicalSequenceBytes(new JsonObject
    {
        ["schemaVersion"] = document.Metadata.SchemaVersion.Value,
        ["kind"] = document.Kind.Value,
        ["definition"] = JsonNode.Parse(document.Definition.GetRawText()),
        ["extensions"] = new JsonArray()
    }, options, CanonicalJsonNumberSemantics.ExactDecimalRational);

    [Benchmark]
    public byte[] ImmutableDocument() => ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document);
}
