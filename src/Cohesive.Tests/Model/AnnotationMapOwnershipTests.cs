using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Map = System.Collections.Immutable.ImmutableDictionary<Cohesive.Model.AnnotationKey, Cohesive.Model.AnnotationValue>;

namespace Cohesive.Tests.Model;

public sealed class AnnotationMapOwnershipTests
{
    static JsonSerializerOptions Options(bool shared)
    {
        var options = StrictDocumentJson.CreateOptions();
        if (shared) options.Converters.Add(new AnnotationMapJsonConverter());
        return options;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"a\":null,\"b\":true,\"c\":1.00,\"d\":\"é\",\"e\":[1,2],\"f\":{\"key\":1}}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    public void SharedSnapshotMatchesPerValueSemanticsAndOutput(string json)
    {
        var previous = JsonSerializer.Deserialize<Map>(json, Options(false))!;
        var current = JsonSerializer.Deserialize<Map>(json, Options(true))!;
        Assert.Equal(previous.Count, current.Count);
        foreach (var pair in previous)
            Assert.Equal(pair.Value?.Value.GetRawText(), current[pair.Key]?.Value.GetRawText());
        Assert.Equal(JsonSerializer.Serialize(previous, Options(false)), JsonSerializer.Serialize(current, Options(true)));
    }

    [Theory]
    [InlineData("{\"\":1}")]
    [InlineData("{\" \":1}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("{\"a\":{\"x\":1,\"x\":2}}")]
    public void StrictFailuresRetainTheirExceptionCategory(string json)
    {
        var previous = Options(false); previous.AllowDuplicateProperties = false;
        var current = Options(true); current.AllowDuplicateProperties = false;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Map>(json, previous));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Map>(json, current));
    }

    [Fact]
    public void NullMapRetainsSerializerNullSemantics()
    {
        Assert.Null(JsonSerializer.Deserialize<Map>("null", Options(false)));
        Assert.Null(JsonSerializer.Deserialize<Map>("null", Options(true)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    public void NonObjectMapsRetainJsonFailureCategory(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Map>(json, Options(false)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Map>(json, Options(true)));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(70_000)]
    public void ValuesSurviveCallerMemoryReuseForSmallAndLargeMaps(int length)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { a = new string('x', length), b = "retained" });
        var map = JsonSerializer.Deserialize<Map>(bytes, Options(true))!;
        Array.Fill(bytes, (byte)' ');
        Assert.Equal(new string('x', length), map[new("a")].Value.GetString());
        Assert.Equal("retained", map[new("b")].Value.GetString());
    }

    [Fact]
    public void CustomAnnotationConverterStillRunsPerValue()
    {
        var options = Options(true);
        options.Converters.Insert(0, new CustomValue());
        var result = JsonSerializer.Deserialize<Map>("{\"a\":1,\"b\":2}", options)!;
        Assert.All(result.Values, value => Assert.Equal("custom", value.Value.GetString()));
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(result, options));
        Assert.Equal("custom", written.RootElement.GetProperty("a").GetString());
        Assert.Equal("custom", written.RootElement.GetProperty("b").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CustomKeyElementAndDocumentConvertersRetainPerValueBehavior(int kind)
    {
        var previous = Options(false);
        var current = Options(true);
        JsonConverter converter = kind switch { 0 => new CustomKey(), 1 => new CustomElement(), _ => new CustomDocument() };
        previous.Converters.Insert(0, converter);
        current.Converters.Insert(0, converter);
        const string json = "{\"a\":1,\"b\":2}";
        var expected = JsonSerializer.Deserialize<Map>(json, previous)!;
        var actual = JsonSerializer.Deserialize<Map>(json, current)!;
        Assert.Equal(expected.Count, actual.Count);
        foreach (var pair in expected) Assert.Equal(pair.Value.Value.GetRawText(), actual[pair.Key].Value.GetRawText());
        Assert.Equal(JsonSerializer.Serialize(expected, previous), JsonSerializer.Serialize(actual, current));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferencePoliciesRetainTheOrdinaryImmutableMapContract(bool preserve)
    {
        var previous = Options(false);
        var current = Options(true);
        previous.ReferenceHandler = current.ReferenceHandler = preserve ? ReferenceHandler.Preserve : ReferenceHandler.IgnoreCycles;
        var expected = JsonSerializer.Deserialize<Map>("{\"a\":1}", previous)!;
        var actual = JsonSerializer.Deserialize<Map>("{\"a\":1}", current)!;
        Assert.Equal(JsonSerializer.Serialize(expected, previous), JsonSerializer.Serialize(actual, current));
    }

    [Theory]
    [InlineData("{\"$id\":\"1\",\"a\":1}")]
    [InlineData("{\"$ref\":\"1\"}")]
    public void ReferenceMetadataMatchesOrdinaryImmutableMapBehavior(string json)
    {
        var previous = Options(false); previous.ReferenceHandler = ReferenceHandler.Preserve;
        var current = Options(true); current.ReferenceHandler = ReferenceHandler.Preserve;
        Map? expected = null, actual = null;
        var before = Record.Exception(() => expected = JsonSerializer.Deserialize<Map>(json, previous));
        var after = Record.Exception(() => actual = JsonSerializer.Deserialize<Map>(json, current));
        Assert.Equal(before?.GetType(), after?.GetType());
        if (before is null) Assert.Equal(JsonSerializer.Serialize(expected, previous), JsonSerializer.Serialize(actual, current));
    }

    [Fact]
    public void WarmEmptyMapReusesEmptyStorageWithoutAllocating()
    {
        var options = Options(true);
        byte[] bytes = "{}"u8.ToArray();
        for (var i = 0; i < 20; i++) JsonSerializer.Deserialize<Map>(bytes, options);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = JsonSerializer.Deserialize<Map>(bytes, options);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Same(Map.Empty, result);
        }
        Array.Sort(samples);
        Assert.Equal(0, samples[3]);
    }

    [Fact]
    public void SharedMapHasFixedWarmAllocationCeiling()
    {
        var options = Options(true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, 32).ToDictionary(i => $"annotation{i}",
            i => new { identity = "shared-contract", fields = new[] { "a", "b", "c" }, ordinal = i }));
        for (var i = 0; i < 20; i++) JsonSerializer.Deserialize<Map>(bytes, options);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = JsonSerializer.Deserialize<Map>(bytes, options);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 13_500);
    }

    sealed class CustomKey : JsonConverter<AnnotationKey>
    {
        public override AnnotationKey Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new("custom:" + reader.GetString());
        public override AnnotationKey ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Read(ref reader, type, options);
        public override void Write(Utf8JsonWriter writer, AnnotationKey value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
        public override void WriteAsPropertyName(Utf8JsonWriter writer, AnnotationKey value, JsonSerializerOptions options) => writer.WritePropertyName(value.Value);
    }
    sealed class CustomElement : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { reader.Skip(); return JsonSerializer.SerializeToElement("custom"); }
        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) => value.WriteTo(writer);
    }
    sealed class CustomDocument : JsonConverter<JsonDocument>
    {
        public override JsonDocument Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { reader.Skip(); return JsonDocument.Parse("{\"changed\":true}"); }
        public override void Write(Utf8JsonWriter writer, JsonDocument value, JsonSerializerOptions options) => value.WriteTo(writer);
    }

    sealed class CustomValue : JsonConverter<AnnotationValue>
    {
        public override AnnotationValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            reader.Skip();
            return AnnotationValue.FromString("custom");
        }
        public override void Write(Utf8JsonWriter writer, AnnotationValue value, JsonSerializerOptions options) => writer.WriteStringValue("custom");
    }
}
