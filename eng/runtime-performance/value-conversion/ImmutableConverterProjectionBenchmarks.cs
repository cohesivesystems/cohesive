using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

[MemoryDiagnoser]
public class ImmutableConverterProjectionBenchmarks
{
    Token source = null!;
    [Params(1, 128)] public int Items { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        source = new("{\"items\":[" + string.Join(',', Enumerable.Range(0, Items).Select(i =>
            $"{{\"id\":{i},\"name\":\"é<>&😀\",\"nested\":[1,null,true]}}")) + "]}");
        if (UnpreparedSerializer() != WarmOwner()) throw new InvalidOperationException("Projection differs.");
    }

    // Independent serializer reference: recreates the full token tree instead of using owner preparation.
    [Benchmark(Baseline = true)]
    public ObservationValue UnpreparedSerializer()
    {
        using var document = JsonSerializer.SerializeToDocument(source);
        return ObservationValue.FromJsonElement(document.RootElement);
    }

    [Benchmark] public ObservationValue ColdOwner() => ObservationValue.FromObject(new Token(source.Json));
    [Benchmark] public ObservationValue WarmOwner() => ObservationValue.FromObject(source);

    [PortableJsonValue(JsonTypeKind.Any)]
    [JsonConverter(typeof(TokenConverter))]
    public sealed record Token(string Json) : IImmutableObservationProjection;
    public sealed class TokenConverter : JsonConverter<Token>
    {
        public override Token Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, Token value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.Json);
    }
}
