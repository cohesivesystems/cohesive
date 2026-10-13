using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures exact canonical integer encoding including owned UTF-8 output.</summary>
[MemoryDiagnoser]
public class CanonicalIntegerNormalizationBenchmarks
{
    JsonElement input;

    [Params("flat", "nested", "collection", "large", "fractional")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        var json = "9223372036854775807";
        if (Shape == "nested")
            for (var i = 0; i < 24; i++) json = "{\"value\":" + json + "}";
        if (Shape is "collection" or "large")
            json = JsonSerializer.Serialize(Enumerable.Range(0, Shape == "large" ? 4096 : 128)
                .Select(i => i % 2 == 0 ? long.MinValue + i : long.MaxValue - i));
        if (Shape == "fractional")
            json = "[1.2300,-0.00e99,1e21,18446744073709551615,1e999]";
        using var parsed = JsonDocument.Parse(json);
        input = parsed.RootElement.Clone();
        for (var i = 0; i < 16; i++) _ = Encode();
    }

    [Benchmark]
    public byte[] Encode() => CanonicalJsonWriter.GetCanonicalBytes(input,
        static _ => CanonicalJsonArrayOrdering.Sequence, CanonicalJsonNumberSemantics.ExactDecimalRational);
}
