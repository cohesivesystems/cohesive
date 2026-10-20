using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class PreparedJsonProfileTests
{
    static readonly JsonSerializerOptions ComparisonOptions = new() { MaxDepth = 256 };
    public sealed record Child(int Value);
    public sealed record Row(int Number, string? Text, Child[]? Items);
    public sealed record BinaryRow(string? Text, byte[]? Bytes);
    public sealed class Mutable { public int Number { get; set; } = 42; }
    public sealed class Required { public required string Text { get; init; } }
    public sealed record RecursiveRow(int Number, RecursiveRow? Child);
    public sealed class ReadOnlyRow { public string? Ignored => null; }

    [Fact]
    public void DepthAndInvalidIgnoredValuesRemainWholeInputConstraints()
    {
        foreach (var depth in new[] { 1, 2, 64, 65 })
        foreach (var limit in new[] { 0, 1, 2, 64, 256 })
        {
            var options = new JsonSerializerOptions { MaxDepth = limit };
            options.MakeReadOnly(populateMissingResolver: true);
            var value = ObservationValue.EmptyObject;
            for (var level = 1; level < depth; level++)
                value = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Child"] = value });
            Compare<RecursiveRow>(value, options);
        }
        foreach (var bad in new[] { ObservationValue.FromBytes(new byte[] { 1 }), new ObservationValue(ObservationValueKind.Double, d: double.NaN) })
            Compare<ReadOnlyRow>(ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Ignored"] = bad }), JsonSerializerOptions.Default);
    }

    [Theory]
    [InlineData("{\"Number\":7,\"Text\":\"row\",\"Items\":[{\"Value\":3}]}")]
    [InlineData("{\"number\":7,\"text\":\"row\",\"items\":[{\"value\":3}]}")]
    [InlineData("{\"NUMBER\":2,\"Number\":3,\"number\":4}")]
    [InlineData("{\"Number\":3,\"unknown\":1}")]
    [InlineData("{\"Number\":\"7\"}")]
    [InlineData("{\"Number\":2147483648}")]
    [InlineData("{\"Number\":1.5}")]
    [InlineData("{\"Items\":true}")]
    [InlineData("{\"Number\":null}")]
    [InlineData("{}")]
    [InlineData("null")]
    public void PreparedProfileMatchesSerializerForNamesNumbersAndFailures(string json)
    {
        foreach (var insensitive in new[] { false, true })
        foreach (var handling in new[] { JsonUnmappedMemberHandling.Skip, JsonUnmappedMemberHandling.Disallow })
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = insensitive, UnmappedMemberHandling = handling,
                NumberHandling = JsonNumberHandling.AllowReadingFromString };
            options.MakeReadOnly(populateMissingResolver: true);
            Compare<Row>(JsonSerializer.Deserialize<ObservationValue>(json), options);
            Compare<Mutable>(JsonSerializer.Deserialize<ObservationValue>(json), options);
        }
    }

    [Fact]
    public void CustomConvertersMetadataAndRequiredPoliciesRemainSerializerOwned()
    {
        var value = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Number"] = ObservationValue.FromInt64(7) });
        var converter = new CountingInteger();
        var options = new JsonSerializerOptions();
        options.Converters.Add(converter);
        options.MakeReadOnly(populateMissingResolver: true);
        var read = ObservationMaterializer.PrepareJsonValue<Row>(options);
        Assert.Equal(0, converter.Reads);
        Assert.Equal(8, read(value)!.Number);
        Assert.Equal(9, read(value)!.Number);
        Assert.Equal(2, converter.Reads);

        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info => { foreach (var property in info.Properties) property.Name = property.Name.ToLowerInvariant(); });
        var modified = new JsonSerializerOptions { TypeInfoResolver = resolver };
        modified.MakeReadOnly();
        Compare<Row>(value, modified);
        var overridden = new JsonSerializerOptions { TypeInfoResolver = new RenamedResolver() };
        overridden.MakeReadOnly();
        Compare<Mutable>(value, overridden);
        var naming = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        naming.MakeReadOnly(populateMissingResolver: true);
        Compare<Row>(value, naming);
        Compare<Required>(ObservationValue.EmptyObject, options);
        var required = new JsonSerializerOptions { RespectRequiredConstructorParameters = true };
        required.MakeReadOnly(populateMissingResolver: true);
        Compare<Row>(ObservationValue.EmptyObject, required);
    }

    [Theory]
    [InlineData(ObservationBytesJsonEncoding.Throw)]
    [InlineData(ObservationBytesJsonEncoding.Base64String)]
    public void BinaryPolicyIsPreservedAtEveryFallback(ObservationBytesJsonEncoding encoding)
    {
        var bytes = ObservationValue.FromBytes(new byte[] { 1, 2, 3 });
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.MakeReadOnly(populateMissingResolver: true);
        Compare<string>(bytes, options, encoding);
        Compare<byte[]>(bytes, options, encoding);
        Compare<BinaryRow>(ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Bytes"] = bytes }), options, encoding);
        Compare<BinaryRow>(ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Text"] = bytes }), options, encoding);
    }

    [Fact]
    public void ExactNameResponsesShareTextButKeepMutableResultsIndependent()
    {
        var text = new string('x', 65536);
        var value = ObservationValue.FromObject(new Row(7, text, [new(3), new(9)]));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.MakeReadOnly(populateMissingResolver: true);
        var read = ObservationMaterializer.PrepareJsonValue<Row>(options);
        for (var warm = 0; warm < 20; warm++) _ = read(value);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var first = read(value)!;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var second = read(value)!;
        Assert.InRange(allocated, 0, 256);
        Assert.Same(text, first.Text);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Items, second.Items);
        Assert.NotSame(first.Items![0], second.Items![0]);
    }

    [Fact]
    public void PreparationRequiresAFrozenProfileAndKnownBinaryPolicy()
    {
        Assert.Throws<ArgumentNullException>(() => ObservationMaterializer.PrepareJsonValue<Row>(null!));
        Assert.Throws<ArgumentException>(() => ObservationMaterializer.PrepareJsonValue<Row>(new JsonSerializerOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObservationMaterializer.PrepareJsonValue<Row>(JsonSerializerOptions.Default, (ObservationBytesJsonEncoding)123));
    }

    [Fact]
    public void StringFastPathsPreserveJsonNormalizationAndDoNotAllocateValidationStorage()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.MakeReadOnly(populateMissingResolver: true);
        var ordinary = ObservationMaterializer.PrepareJsonValue<string>();
        foreach (var length in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 64, 65536 })
        foreach (var suffix in new[] { "", "é", "🚚", "\uD800", "\uDC00", "\uD800x", "\uDC00\uD800" })
        {
            var text = new string('x', length) + suffix;
            var value = ObservationValue.FromString(text);
            Compare<string>(value, options);
            Assert.Equal(value.Deserialize<string>(), ordinary(value));
            var expectedValid = !suffix.Contains('\uD800') && !suffix.Contains('\uDC00');
            for (var warm = 0; warm < 3; warm++) _ = StringExtensions.IsWellFormedUtf16(text.AsSpan());
            var before = GC.GetAllocatedBytesForCurrentThread();
            var valid = StringExtensions.IsWellFormedUtf16(text.AsSpan());
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expectedValid, valid);
            Assert.Equal(0, allocated);
        }
    }

    static void Compare<T>(ObservationValue value, JsonSerializerOptions options, ObservationBytesJsonEncoding encoding = ObservationBytesJsonEncoding.Throw)
    {
        T? expected = default, actual = default;
        var expectedError = Record.Exception(() => expected = value.Deserialize<T>(options, encoding));
        var prepared = ObservationMaterializer.PrepareJsonValue<T>(options, encoding);
        var actualError = Record.Exception(() => actual = prepared(value));
        Assert.Equal(expectedError?.GetType(), actualError?.GetType());
        if (expectedError is null) Assert.Equal(JsonSerializer.Serialize(expected, ComparisonOptions), JsonSerializer.Serialize(actual, ComparisonOptions));
    }

    sealed class CountingInteger : JsonConverter<int>
    {
        internal int Reads;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetInt32() + ++Reads;
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    sealed class RenamedResolver : DefaultJsonTypeInfoResolver
    {
        public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            var info = base.GetTypeInfo(type, options);
            foreach (var property in info.Properties) property.Name = property.Name.ToLowerInvariant();
            return info;
        }
    }
}
