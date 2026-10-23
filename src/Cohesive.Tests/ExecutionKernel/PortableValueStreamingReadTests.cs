using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class PortableValueStreamingReadTests
{
    static readonly JsonSerializerOptions Options = CreateOptions();

    [Theory]
    [InlineData("null")]
    [InlineData("undefined")]
    [InlineData(@"n\u0075ll")]
    public void SingletonKindMetadataAllocatesNoDecodedText(string kind)
    {
        var json = Encoding.UTF8.GetBytes("{\"$kind\":\"" + kind + "\"}");
        for (var i = 0; i < 100; i++) _ = JsonSerializer.Deserialize<ObservationValue>(json, Options);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) _ = JsonSerializer.Deserialize<ObservationValue>(json, Options);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void InvalidUtf8KindCannotMatchFixedMetadata()
    {
        byte[] json = [.. Encoding.UTF8.GetBytes("{\"$kind\":\"nu"), 0xFF, .. Encoding.UTF8.GetBytes("ll\"}")];
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ObservationValue>(json, Options));
    }

    [Fact]
    public void InvalidFailureEvidenceRetainsTheJsonExceptionBoundary()
    {
        var portable = PortableValue.Failed(new ValueContract(), new("test.failure", DiagnosticSeverity.Error,
            "Failed.", Evidence: new(stage: "test.stage")));
        var json = JsonSerializer.Serialize(portable, Options).Replace("test.stage", " ", StringComparison.Ordinal);
        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PortableValue>(json, Options));
        Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
    }

    [Fact]
    public void EveryKindPreservesExactWireBytesWithEitherMetadataOrder()
    {
        ObservationValue[] fixtures = [ObservationValue.Undefined, ObservationValue.Null,
            ObservationValue.FromInt64(long.MinValue), ObservationValue.FromDouble(-0d),
            new(ObservationValueKind.Decimal, dec: 1.000m), ObservationValue.FromBool(true),
            ObservationValue.FromString("é <&> 🚚"), ObservationValue.FromBytes(new byte[] { 0, 1, 255 }),
            ObservationValue.FromDateTimeOffset(new DateTimeOffset(2026, 10, 18, 12, 34, 56, TimeSpan.FromHours(-7))),
            ObservationValue.FromDateOnly(new DateOnly(2026, 10, 18)), ObservationValue.FromTimeOnly(new TimeOnly(12, 34, 56)),
            ObservationValue.FromTimeSpan(TimeSpan.FromDays(-2)), ObservationValue.EmptyObject,
            ObservationValue.FromImmutableArray([ObservationValue.Null, ObservationValue.Undefined])];
        Assert.Equal(Enum.GetValues<ObservationValueKind>().Order(), fixtures.Select(value => value.Kind).Order());
        foreach (var fixture in fixtures)
        {
            var canonical = JsonSerializer.Serialize(fixture, Options);
            var escaped = System.Text.RegularExpressions.Regex.Replace(canonical, "(?<=\"\\$kind\":\")[a-z]",
                match => "\\u" + ((int)match.Value[0]).ToString("x4"));
            foreach (var json in new[] { canonical, escaped, Reverse(JsonNode.Parse(canonical))!.ToJsonString() })
            {
                var actual = JsonSerializer.Deserialize<ObservationValue>(json, Options);
                var bytes = Encoding.UTF8.GetBytes(json);
                var first = new Segment(bytes.AsMemory(0, 12));
                var last = first.Append(bytes.AsMemory(12));
                var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
                Assert.Equal(canonical, JsonSerializer.Serialize(JsonSerializer.Deserialize<ObservationValue>(ref reader, Options), Options));
                Assert.Equal(fixture.Kind, actual.Kind);
                Assert.Equal(canonical, JsonSerializer.Serialize(actual, Options));
            }
        }
    }

    [Fact]
    public void PortableEnvelopeMemberOrderIsIndependentOfStateAndContract()
    {
        var contract = new ValueContract();
        PortableValue[] values = [PortableValue.Missing(contract), PortableValue.Absent(contract), PortableValue.Null(contract),
            PortableValue.Unknown(contract), PortableValue.Failed(contract, new("test.failure", DiagnosticSeverity.Error, "Failed.")),
            PortableValue.Concrete(contract, ObservationValue.FromObject(new Dictionary<string, ObservationValue>
                { ["child"] = ObservationValue.FromImmutableArray([ObservationValue.FromInt64(1), ObservationValue.FromBytes(new byte[] { 2 })]) }))];
        foreach (var original in values)
        {
            var canonical = JsonSerializer.Serialize(original, Options);
            var actual = JsonSerializer.Deserialize<PortableValue>(Reverse(JsonNode.Parse(canonical))!.ToJsonString(), Options);
            Assert.Equal(original, actual);
            Assert.Equal(canonical, JsonSerializer.Serialize(actual, Options));
        }
    }

    [Fact]
    public void GeneratedTreesRoundTripInCanonicalAndReversedOrder()
    {
        var random = new Random(442);
        for (var index = 0; index < 128; index++)
        {
            var original = Generate(0);
            var json = JsonSerializer.Serialize(original, Options);
            var decoded = JsonSerializer.Deserialize<ObservationValue>(json, Options);
            Assert.Equal(json, JsonSerializer.Serialize(decoded, Options));
            decoded = JsonSerializer.Deserialize<ObservationValue>(Reverse(JsonNode.Parse(json))!.ToJsonString(), Options);
            Assert.Equal(json, JsonSerializer.Serialize(decoded, Options));
        }

        ObservationValue Generate(int depth) => random.Next(depth == 4 ? 6 : 8) switch
        {
            0 => ObservationValue.Null, 1 => ObservationValue.Undefined, 2 => ObservationValue.FromInt64(random.NextInt64()),
            3 => new(ObservationValueKind.Decimal, dec: random.Next() / 100m), 4 => ObservationValue.FromDouble(1e-29),
            5 => ObservationValue.FromString("nested café 🚚"),
            6 => ObservationValue.FromImmutableArray([.. Enumerable.Range(0, random.Next(12)).Select(_ => Generate(depth + 1))]),
            _ => ObservationValue.FromObject(Enumerable.Range(0, random.Next(12)).ToDictionary(i => "f" + i, _ => Generate(depth + 1)))
        };
    }

    [Theory]
    [InlineData("{\"$kind\":\"Null\"}")]
    [InlineData("{\"$kind\":\"unknown\"}")]
    [InlineData("{\"$kind\":\"\\uD800\"}")]
    [InlineData("{\"$kind\":\"null\",\"$value\":null}")]
    [InlineData("{\"$value\":null,\"$kind\":\"undefined\"}")]
    [InlineData("{\"$kind\":\"string\",\"$kind\":\"string\",\"$value\":\"a\"}")]
    [InlineData("{\"$kind\":\"string\",\"$value\":\"a\",\"$value\":\"b\"}")]
    [InlineData("{\"$kind\":\"object\",\"$value\":{\"a\":{\"$kind\":\"null\"},\"\\u0061\":{\"$kind\":\"null\"}}}")]
    [InlineData("{\"$kind\":\"int64\",\"$value\":\"9223372036854775808\"}")]
    [InlineData("{\"$kind\":\"double\",\"$value\":1e400}")]
    [InlineData("{\"$kind\":\"bool\",\"$value\":0}")]
    [InlineData("{\"$kind\":\"bytes\",\"$value\":\"!!!\"}")]
    [InlineData("{\"$kind\":\"array\",\"$value\":[{\"$kind\":\"null\"},")]
    public void MalformedTagsFailWithJsonDiagnostics(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ObservationValue>(json, Options));

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    public void WiderTaggedObjects_RejectDecodedDuplicatesAndPreserveCanonicalOrder(int width)
    {
        var original = ObservationValue.FromObject(Enumerable.Range(0, width).Reverse()
            .ToDictionary(index => "field" + index, index => ObservationValue.FromInt64(index)));
        var canonical = JsonSerializer.Serialize(original, Options);
        var reversed = Reverse(JsonNode.Parse(canonical))!.ToJsonString();
        Assert.Equal(canonical, JsonSerializer.Serialize(JsonSerializer.Deserialize<ObservationValue>(reversed, Options), Options));
        var properties = string.Join(',', Enumerable.Range(0, width).Select(index => $"\"field{index}\":{{\"$kind\":\"null\"}}"));
        var duplicate = "{\"$kind\":\"object\",\"$value\":{" + properties + ",\"\\u0066ield0\":{\"$kind\":\"null\"}}}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ObservationValue>(duplicate, Options));
    }

    [Fact]
    public void BytesTransferOneOwnedDecodeAndStayIndependentAfterLaterReads()
    {
        var bytes = new byte[65536];
        Random.Shared.NextBytes(bytes);
        var json = JsonSerializer.SerializeToUtf8Bytes(ObservationValue.FromBytes(bytes), Options);
        for (var warm = 0; warm < 20; warm++) _ = JsonSerializer.Deserialize<ObservationValue>(json, Options);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var first = JsonSerializer.Deserialize<ObservationValue>(json, Options);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var second = JsonSerializer.Deserialize<ObservationValue>(json, Options);
        Assert.InRange(allocated, 0, bytes.Length + 2048L);
        Assert.Equal(bytes, first.Bytes.ToArray());
        Assert.False(first.Bytes.Equals(second.Bytes));
    }

    [Fact]
    public void SegmentedInputAndDeepPayloadsKeepTheConfiguredWireLimit()
    {
        var value = ObservationValue.FromInt64(1);
        for (var depth = 0; depth < 80; depth++) value = ObservationValue.FromImmutableArray([value]);
        var json = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        var first = new Segment(json.AsMemory(0, json.Length / 2));
        var last = first.Append(json.AsMemory(json.Length / 2));
        var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length), new JsonReaderOptions { MaxDepth = Options.MaxDepth });
        var decoded = JsonSerializer.Deserialize<ObservationValue>(ref reader, Options);
        Assert.Equal(json, JsonSerializer.SerializeToUtf8Bytes(decoded, Options));
        var limited = new JsonSerializerOptions(Options) { MaxDepth = 64 };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ObservationValue>(json, limited));
    }

    static JsonNode? Reverse(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Reverse().Select(pair => KeyValuePair.Create(pair.Key, Reverse(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Reverse).ToArray()),
        _ => node?.DeepClone()
    };

    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { MaxDepth = 256 };
        options.Converters.Add(PortableValueJsonConverter.TaggedObservationValues);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
