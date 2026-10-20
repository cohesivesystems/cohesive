using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

[MemoryDiagnoser]
public class ObservationJsonWritingBenchmarks
{
    [Params("small", "owned", "ordinal")] public string Storage { get; set; } = "small";
    [Params(1, 128)] public int Nodes { get; set; }
    [Params(1, 32)] public int Depth { get; set; }
    readonly ArrayBufferWriter<byte> output = new(1024 * 1024);
    Utf8JsonWriter writer = null!;
    ObservationValue value;

    [GlobalSetup]
    public void Setup()
    {
        Dictionary<string, ObservationValue> fields = new(StringComparer.Ordinal)
        { ["z"] = ObservationValue.FromInt64(1), ["a"] = ObservationValue.FromInt64(2) };
        ObservationValue leaf;
        if (Storage == "small")
        {
            using var json = JsonDocument.Parse("{\"z\":1,\"a\":2}");
            leaf = ObservationValue.FromJsonElement(json.RootElement);
        }
        else if (Storage == "owned") leaf = ObservationValue.FromObject(fields);
        else
        {
            Shape shape = new(new("write"), [new(new("a"), new ScalarTypeRef(ScalarTypeKind.Int64)), new(new("z"), new ScalarTypeRef(ScalarTypeKind.Int64))]);
            ShapeGraph graph = new(new("write-graph"), [shape]);
            GraphShapeId qualified = new(graph, shape.Id);
            var layout = ObservationLayout.Create(qualified, ["z", "a"]);
            leaf = Observation.Create(qualified, layout, new[] { fields["z"], fields["a"] }.AsSpan()).Value;
        }
        for (var depth = 1; depth < Depth; depth++)
            leaf = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["child"] = leaf });
        value = ObservationValue.FromArray([.. Enumerable.Repeat(leaf, Nodes)]);
        writer = new(output);
        Write();
    }

    [Benchmark]
    public int Write()
    {
        output.Clear(); writer.Reset(output);
        value.WriteTo(writer); writer.Flush();
        return output.WrittenCount;
    }

    [GlobalCleanup]
    public void Cleanup() => writer.Dispose();
}
