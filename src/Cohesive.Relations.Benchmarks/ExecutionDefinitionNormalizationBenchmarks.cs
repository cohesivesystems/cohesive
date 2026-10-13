using System.Buffers;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures owned normalization separately from authoring and fingerprinting.</summary>
[MemoryDiagnoser]
public class ExecutionDefinitionNormalizationBenchmarks
{
    JsonElement input;
    Func<JsonElement, JsonElement> normalize = null!;
    Action<JsonElement> validate = null!;
    Action<Utf8JsonWriter, JsonElement> write = null!;

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        normalize = typeof(ExecutionDefinitionFingerprinter).GetMethod("NormalizeDefinition", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<JsonElement, JsonElement>>();
        validate = typeof(ExecutionDefinitionFingerprinter).GetMethod("ValidateDefinitionProperties", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<JsonElement>>();
        write = typeof(CanonicalJsonWriter).GetMethod("WriteCanonicalSequence", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<Utf8JsonWriter, JsonElement>>();
        const string Row = "{\"z\":\"λ/\\\"<>&\",\"values\":[1.00,-0.0,1e21,null],\"a\":true}";
        var json = Row;
        if (Shape == "nested")
            for (var i = 0; i < 24; i++) json = "{\"child\":" + json + "}";
        if (Shape is "collection" or "large")
            json = "{\"rows\":[" + string.Join(",", Enumerable.Repeat(Row, Shape == "large" ? 4096 : 128)) + "]}";
        using var parsed = JsonDocument.Parse(json);
        input = parsed.RootElement.Clone();
        for (var i = 0; i < 16; i++) _ = Normalize();
        if (Previous().GetRawText() != Normalize().GetRawText())
            throw new InvalidOperationException("Normalization changed canonical bytes.");
    }

    [Benchmark(Baseline = true)]
    public JsonElement Previous()
    {
        validate(input);
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(writer, input);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    [Benchmark]
    public JsonElement Normalize() => normalize(input);
}
