using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

[MemoryDiagnoser]
public class AdoptionValueSerializationBenchmarks
{
    [Params("flat-16", "nested-4x16", "array-4096")]
    public string Workload { get; set; } = "flat-16";
    readonly JsonSerializerOptions plain = StrictDocumentJson.CreateOptions();
    readonly JsonSerializerOptions tagged = TaggedOptions();
    StoredValue value = null!;

    [GlobalSetup]
    public void Setup()
    {
        var flat = ObservationValue.FromObject(Enumerable.Range(0, 16).ToDictionary(i => "field" + i,
            i => i % 2 == 0 ? ObservationValue.FromInt64(i) : ObservationValue.FromString("value/" + i)));
        value = new(Workload switch
        {
            "flat-16" => flat,
            "nested-4x16" => ObservationValue.FromObject(Enumerable.Range(0, 4).ToDictionary(i => "child" + i, _ => flat)),
            _ => ObservationValue.FromArray([.. Enumerable.Range(0, 4096).Select(i => ObservationValue.FromInt64(i))])
        });
        // Cold metadata initialization is outside measurements; each operation retains its canonical byte array.
        PlainJson();
        TaggedJson();
    }

    [Benchmark(Baseline = true)]
    public byte[] PlainJson() => StrictDocumentJson.GetCanonicalBytes(value, plain);
    [Benchmark]
    public byte[] TaggedJson() => StrictDocumentJson.GetCanonicalBytes(value, tagged);

    static JsonSerializerOptions TaggedOptions()
    {
        var options = StrictDocumentJson.CreateOptions();
        options.Converters.Add(PortableValueJsonConverter.TaggedObservationValues);
        return options;
    }

    public sealed record StoredValue(ObservationValue Value);
}

[MemoryDiagnoser]
public class AdoptionBinaryMaterializationBenchmarks
{
    [Params(32, 65536)]
    public int Bytes { get; set; }
    Observation observation = null!;
    ObservationMaterializer<BinaryRecord> direct = null!;
    ObservationMaterializer<BinaryRecord> json = null!;

    [GlobalSetup]
    public void Setup()
    {
        Shape shape = new(new("binary"), [new(new("Value"), new ScalarTypeRef(ScalarTypeKind.Bytes))]);
        GraphShapeId id = new(new ShapeGraph(new("adoption/bytes"), [shape]), shape.Id);
        observation = Observation.Create(id, new Dictionary<string, ObservationValue> { ["Value"] = ObservationValue.FromBytes(new byte[Bytes]) });
        direct = ObservationMaterializer.For<BinaryRecord>(id).Compile();
        // Previously the default path threw. Compare with the explicit JSON workaround that can handle bytes.
        json = ObservationMaterializer.For<BinaryRecord>(id).Map("Value", record => record.Value,
            value => JsonSerializer.Deserialize<byte[]>(value.GetRawText(ObservationBytesJsonEncoding.Base64String))!).Compile();
        Direct();
        JsonWorkaround();
    }

    [Benchmark(Baseline = true)]
    public BinaryRecord JsonWorkaround() => json.Materialize(observation);
    [Benchmark]
    public BinaryRecord Direct() => direct.Materialize(observation);

    public sealed record BinaryRecord(byte[] Value);
}
