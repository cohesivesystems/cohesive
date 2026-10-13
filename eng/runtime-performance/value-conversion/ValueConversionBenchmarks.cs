using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Execution;

[MemoryDiagnoser]
[ShortRunJob]
public class DeclaredProjectionBenchmarks
{
    RawValue value = null!;
    readonly JsonSerializerOptions options = new();

    [Params("Flat", "Nested", "Rows")]
    public string Shape { get; set; } = "Flat";

    [GlobalSetup]
    public void Setup()
    {
        object payload = Shape switch
        {
            "Flat" => Enumerable.Range(0, 128).ToDictionary(i => $"field{i:D3}", i => i),
            "Nested" => Nested(24),
            "Rows" => Enumerable.Range(0, 1024).Select(i => new { number = i, text = "é<>&🙂", nested = new { enabled = true } }).ToArray(),
            _ => throw new InvalidOperationException(Shape)
        };
        value = new(JsonSerializer.SerializeToElement(payload));
        if (!NodeTree().Equals(ImmutableDocument())) throw new InvalidOperationException("Projection differs.");
    }

    static object Nested(int depth) => depth == 0 ? new { number = 1.2300m }
        : new Dictionary<string, object> { ["child"] = Nested(depth - 1), ["enabled"] = true };

    [Benchmark(Baseline = true)]
    public ObservationValue NodeTree() => ObservationValue.FromJsonNode(JsonSerializer.SerializeToNode(value, options));

    [Benchmark]
    public ObservationValue ImmutableDocument() => ObservationValue.FromObject(value);

    [JsonConverter(typeof(RawValueConverter))]
    public sealed record RawValue(JsonElement Value);
    public sealed class RawValueConverter : JsonConverter<RawValue>
    {
        public override RawValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, RawValue value, JsonSerializerOptions options) => value.Value.WriteTo(writer);
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class TypedValueDecodeBenchmarks
{
    ObservationValue input;
    JsonSerializerOptions options = null!;

    [Params(1, 1024)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        options = ExecutionDefinitionJsonSerializer.CreateOptions();
        options.PropertyNamingPolicy = null;
        options.Converters.Insert(0, new DeclaredJsonValueConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        input = ObservationValue.FromObject(new Payload(Enumerable.Range(0, Rows)
            .Select(i => new Row(i, "é<>&🙂", 1.2300m)).ToArray()));
        var expected = StringReference();
        if (!expected.Rows.SequenceEqual(PooledUtf8().Rows)
            || !expected.Rows.SequenceEqual(DocumentReference().Rows))
            throw new InvalidOperationException("Decoded result differs.");
    }

    [Benchmark(Baseline = true)]
    public Payload StringReference() => JsonSerializer.Deserialize<Payload>(input.GetRawText(), options)!;

    [Benchmark]
    public Payload DocumentReference()
    {
        using var document = JsonSerializer.SerializeToDocument(input, options);
        return document.RootElement.Deserialize<Payload>(options)!;
    }

    [Benchmark]
    public Payload PooledUtf8() => input.Deserialize<Payload>(options)!;

    public sealed record Payload(Row[] Rows);
    public sealed record Row(long Number, string Text, decimal Amount);
}

[MemoryDiagnoser]
[ShortRunJob]
public class HostedAdapterDecodeBenchmarks
{
    PortableValue input = null!;
    readonly ValueContract contract = new(new JsonTypeRef());

    [Params(1, 1024)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        input = PortableValue.Concrete(contract, ObservationValue.FromObject(new TypedValueDecodeBenchmarks.Payload(
            Enumerable.Range(0, Rows).Select(i => new TypedValueDecodeBenchmarks.Row(i, "é<>&🙂", 1.2300m)).ToArray())));
        if (Decode().Type != ResultType.Success) throw new InvalidOperationException("Decode failed.");
    }

    // The real adapter owns fresh admission; no copied admission oracle can drift.
    [Benchmark]
    public Result<TypedValueDecodeBenchmarks.Payload, DocumentValidationDiagnostic> Decode() =>
        HostedQueryValueAdapter.Decode<TypedValueDecodeBenchmarks.Payload>(input, contract);
}
