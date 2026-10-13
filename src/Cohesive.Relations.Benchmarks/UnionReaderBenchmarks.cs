using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Plain typed decoding of warm union collections; no validation or fixture construction is measured.</summary>
[MemoryDiagnoser]
public class UnionReaderBenchmarks
{
    TypeRef type = null!;
    ShapeGraph graph = null!;
    byte[] json = null!;

    [Params(1, 32, 128)]
    public int Count { get; set; }
    [Params("first", "last")]
    public string Case { get; set; } = "first";

    [GlobalSetup]
    public void Setup()
    {
        var named = new NamedTypeRef(new("union"));
        var payload = new ObjectTypeRef([new("value", new ScalarTypeRef(ScalarTypeKind.Int64))]);
        var union = new TypeDefinition.Union(named.TypeId, new UnionDiscriminator("kind"),
            [.. Enumerable.Range(0, 128).Select(i => new UnionCase($"case{i}", payload, $"code{i}"))]);
        graph = new(new("reader-benchmark"), [], [union]);
        type = new ArrayTypeRef(named);
        var code = Case == "first" ? "code0" : "code127";
        json = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat($"{{\"kind\":\"{code}\",\"value\":1}}", Count)) + "]");
        for (var i = 0; i < 64; i++) _ = Decode();
    }

    [Benchmark]
    public ObservationValue Decode()
    {
        var reader = new Utf8JsonReader(json);
        reader.Read();
        return ObservationJsonReader.ReadTypedValue(ref reader, type, graph);
    }
}
