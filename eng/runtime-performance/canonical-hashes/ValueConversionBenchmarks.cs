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
public class HostedDecodeBenchmarks
{
    PortableValue input = null!;
    readonly ValueContract contract = new(new JsonTypeRef());
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
        input = PortableValue.Concrete(contract, ObservationValue.FromObject(new Payload(Enumerable.Range(0, Rows)
            .Select(i => new Row(i, "é<>&🙂", 1.2300m)).ToArray())));
        var expected = ClonedElement();
        var actual = ScopedDocument();
        if (expected.Type != ResultType.Success || actual.Type != ResultType.Success
            || !expected.Success!.Rows.SequenceEqual(actual.Success!.Rows))
            throw new InvalidOperationException("Decoded result differs.");
    }

    [Benchmark(Baseline = true)]
    public Result<Payload, DocumentValidationDiagnostic> ClonedElement()
    {
        if (input.Contract != contract || input.State != PortableValueState.Concrete) throw new InvalidOperationException();
        var validation = PortableExecutionValidator.Validate(input);
        if (validation.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } error)
            return Result<Payload, DocumentValidationDiagnostic>.FromFailure(error);
        var element = JsonSerializer.SerializeToElement(input.Value!.Value, options);
        return Result<Payload, DocumentValidationDiagnostic>.FromSuccess(element.Deserialize<Payload>(options)!);
    }

    [Benchmark]
    public Result<Payload, DocumentValidationDiagnostic> ScopedDocument() => HostedQueryValueAdapter.Decode<Payload>(input, contract);

    public sealed record Payload(Row[] Rows);
    public sealed record Row(long Number, string Text, decimal Amount);
}
