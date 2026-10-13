using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class DeclaredJsonObservationProjectionTests
{
    static readonly JsonSerializerOptions Options = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new DeclaredJsonValueConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[true,false,null,\"é<>&🙂\",1.2300,-0.0,1e-300,1e300]")]
    [InlineData("{\"z\":[],\"a\":{},\"n\":123456789012345678901234567890}")]
    public void RootAndDeclaredPropertyMatchPreviousNodeProjection(string json)
    {
        var value = new RawValue(json);
        var expected = Previous(value, typeof(RawValue));
        Assert.Equal(expected, ObservationValue.FromObject(value));
        Assert.Equal(expected, ObservationValue.FromObject(new RawEnvelope(value)).GetProperty(nameof(RawEnvelope.Value)));
    }

    [Fact]
    public void DeclaredPolymorphicPropertyAndSequenceRetainDiscriminators()
    {
        BaseValue value = new DerivedValue("é", 1.2300m);
        var envelope = new PolymorphicEnvelope(value, [value, new DerivedValue("other", -0m)]);
        var observed = ObservationValue.FromObject(envelope);
        Assert.Equal(Previous(value, typeof(BaseValue)), observed.GetProperty(nameof(PolymorphicEnvelope.Value)));
        Assert.Equal(Previous(envelope.Items, typeof(IReadOnlyList<BaseValue>)), observed.GetProperty(nameof(PolymorphicEnvelope.Items)));
        Assert.Equal("derived", observed.GetProperty(nameof(PolymorphicEnvelope.Value)).GetProperty("$kind").GetString());
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1,\"a\":1}")]
    [InlineData("{\"array\":[{\"a/b~\":1,\"a/b~\":2}]}")]
    public void ConverterDuplicatesFailAtTheDeclaredParser(string json)
    {
        var value = new RawValue(json);
        var expected = Record.Exception(() => Previous(value, typeof(RawValue)));
        var root = Record.Exception(() => ObservationValue.FromObject(value));
        var nested = Record.Exception(() => ObservationValue.FromObject(new RawEnvelope(value)));
        Assert.NotNull(expected);
        Assert.NotNull(root);
        Assert.NotNull(nested);
        Assert.IsType<ArgumentException>(expected);
        Assert.IsType<JsonException>(root);
        Assert.IsType<JsonException>(nested);
    }

    [Fact]
    public void PublicTokenProjectionRetainsLastPropertyWins()
    {
        using var source = JsonDocument.Parse("{\"a\":1,\"a\":2}");
        Assert.Equal(2, ObservationValue.FromJsonElement(source.RootElement).GetProperty("a").GetInt32());
    }

    [Fact]
    public void ProjectionOwnsValuesAfterTemporaryDocumentDisposal()
    {
        var value = new RawValue("{\"rows\":[{\"number\":12,\"text\":\"retained\"}]}");
        var observed = ObservationValue.FromObject(value);
        // Exercise the shared JSON pools after the temporary document has been returned.
        for (var i = 0; i < 64; i++) ObservationValue.FromObject(new RawValue("{\"other\":\"overwrite\"}"));
        Assert.Equal(12, observed.GetProperty("rows").Array[0].GetProperty("number").GetInt32());
        Assert.Equal("retained", observed.GetProperty("rows").Array[0].GetProperty("text").GetString());
        Assert.Equal(Previous(value, typeof(RawValue)), observed);
    }

    [Fact]
    public void WarmLargeConvertedProjectionHasABoundedAllocationPerRow()
    {
        // Token input matches the opt-in projection benchmark.
        using var source = JsonSerializer.SerializeToDocument(Enumerable.Range(0, 1024)
            .Select(i => new { number = i, text = "é<>&🙂", nested = new { enabled = true } }));
        var value = new TokenValue(source.RootElement);
        for (var i = 0; i < 4; i++)
        {
            Previous(value, typeof(TokenValue));
            ObservationValue.FromObject(value);
        }
        var expected = Previous(value, typeof(TokenValue));
        ObservationValue actual = default;
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            actual = ObservationValue.FromObject(value);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.Equal(expected, actual);
        Assert.True(samples[3] <= 960L * 1024, $"Median projection allocation {samples[3]} exceeds 960 bytes per row.");
    }

    [Fact]
    public void FlatConvertedProjectionHasABoundedAllocationPerField()
    {
        var value = new RawValue(JsonSerializer.Serialize(Enumerable.Range(0, 128).ToDictionary(i => $"field{i:D3}", i => i)));
        for (var i = 0; i < 4; i++) ObservationValue.FromObject(value);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            ObservationValue.FromObject(value);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.True(samples[3] <= 176L * 128, $"Median projection allocation {samples[3]} exceeds 176 bytes per field.");
    }

    static ObservationValue Previous(object value, Type declaredType) =>
        ObservationValue.FromJsonNode(JsonSerializer.SerializeToNode(value, declaredType, Options));

    [JsonConverter(typeof(TokenValueConverter))]
    public sealed record TokenValue(JsonElement Value);
    public sealed class TokenValueConverter : JsonConverter<TokenValue>
    {
        public override TokenValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, TokenValue value, JsonSerializerOptions options) => value.Value.WriteTo(writer);
    }

    [JsonConverter(typeof(RawValueConverter))]
    public sealed record RawValue(string Json);
    public sealed class RawValueConverter : JsonConverter<RawValue>
    {
        public override RawValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, RawValue value, JsonSerializerOptions options) => writer.WriteRawValue(value.Json);
    }
    sealed record RawEnvelope(RawValue Value);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
    [JsonDerivedType(typeof(DerivedValue), "derived")]
    public abstract record BaseValue;
    public sealed record DerivedValue(string Name, decimal Amount) : BaseValue;
    sealed record PolymorphicEnvelope(BaseValue Value, IReadOnlyList<BaseValue> Items);
}
