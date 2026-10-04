using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Compares typed strict document canonicalization with the previous mutable node expansion.</summary>
[MemoryDiagnoser]
public class TypedDocumentCanonicalizationBenchmarks
{
    Document value = null!;
    readonly JsonSerializerOptions options = StrictDocumentJson.CreateOptions();

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        JsonNode content = new JsonObject { ["z"] = 1.2300m, ["a"] = "<>&🙂", ["enabled"] = true };
        if (Shape == "nested")
            for (var index = 0; index < 24; index++)
                content = new JsonObject { ["child"] = content, ["values"] = new JsonArray(3, 1, 2) };
        if (Shape is "collection" or "large")
        {
            var rows = new JsonArray();
            for (var index = 0; index < (Shape == "large" ? 4096 : 128); index++)
                rows.Add(content.DeepClone());
            content = new JsonObject { ["rows"] = rows };
        }
        using var json = JsonDocument.Parse(content.ToJsonString());
        value = new Document("example", json.RootElement.Clone());
        options.MakeReadOnly(populateMissingResolver: true);
        if (!MutableNodeReference().AsSpan().SequenceEqual(ImmutableDocument()))
            throw new InvalidOperationException("Canonical bytes differ.");
    }

    [Benchmark(Baseline = true)]
    public byte[] MutableNodeReference() => CanonicalJsonWriter.GetCanonicalSequenceBytes(
        JsonSerializer.SerializeToNode(value, typeof(Document), options)!, options,
        CanonicalJsonNumberSemantics.ExactDecimalRational);

    [Benchmark]
    public byte[] ImmutableDocument() => StrictDocumentJson.GetCanonicalBytes(value, options);

    sealed record Document(string Name, JsonElement Content);
}
