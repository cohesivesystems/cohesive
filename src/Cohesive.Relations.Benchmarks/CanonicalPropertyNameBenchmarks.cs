using System.Buffers;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures bounded property-name reuse, including wide objects without reusable names.</summary>
[MemoryDiagnoser]
public class CanonicalPropertyNameBenchmarks
{
    JsonElement input;
    Action<Utf8JsonWriter, JsonElement> write = null!;

    [Params("flat", "repeated", "unique", "escaped")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        write = typeof(CanonicalJsonWriter).GetMethod("WriteCanonicalSequence", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<Utf8JsonWriter, JsonElement>>();
        input = Shape switch
        {
            "flat" => JsonSerializer.SerializeToElement(new { b = 2, a = 1 }),
            "unique" => JsonSerializer.SerializeToElement(Enumerable.Range(0, 4096)
                .ToDictionary(index => $"field{index:D4}", index => index)),
            "repeated" => JsonSerializer.SerializeToElement(new { rows = Enumerable.Repeat(new { z = 1, name = "row", a = true }, 4096) }),
            "escaped" => JsonSerializer.SerializeToElement(new { rows = Enumerable.Repeat(new Dictionary<string, int>
                { ["é"] = 1, ["a/b"] = 2, ["quote\""] = 3 }, 4096) }),
            _ => throw new InvalidOperationException(Shape)
        };
        for (var index = 0; index < 16; index++) _ = Canonicalize();
    }

    [Benchmark]
    public byte[] Canonicalize()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(writer, input);
        return buffer.WrittenSpan.ToArray();
    }
}
