using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class PreparedScalarDecoderTests
{
    static JsonSerializerOptions Options => ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions();

    static Func<ObservationValue, T> Prepare<T>(JsonSerializerOptions? options = null) =>
        (Func<ObservationValue, T>)DefaultObservationValueConverterCache.PrepareClrContract(typeof(T), options ?? Options)!;

    [Theory]
    [InlineData("shape")]
    [InlineData("é😀<>&")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void WrapperInterpretationPreservesConstructorAndScalarRules(string? text)
    {
        Compare(ObservationValue.FromString(text!), Prepare<ShapeId>(), Options);
        Compare(ObservationValue.FromString(text!), Prepare<NormalizedId>(), Options);
    }

    [Fact]
    public void WrapperCompatibilityInputsAndMalformedTextRetainTheSerializer()
    {
        var inputs = new[] { ObservationValue.Null, ObservationValue.FromInt64(42),
            ObservationValue.FromString(new string((char)0xD800, 1)),
            ObservationValue.FromString(new string((char)0xDC00, 1)),
            ObservationValue.FromString(new string([(char)0xD800, 'x'])),
            ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = ObservationValue.FromString("legacy") }) };
        foreach (var legacy in new[] { false, true })
        {
            var options = new JsonSerializerOptions(Options);
            if (legacy) options.Converters.Insert(0, new SingleValueWrapperJsonConverter());
            options.MakeReadOnly();
            var read = Prepare<ShapeId>(options);
            foreach (var input in inputs) Compare(input, read, options);
        }
    }

    [Fact]
    public void CustomStringAndWrapperReadersRemainAuthoritative()
    {
        foreach (JsonConverter converter in new JsonConverter[] { new PrefixStringConverter(), new IdConverter() })
        {
            var options = new JsonSerializerOptions(Options);
            options.Converters.Insert(0, converter);
            options.MakeReadOnly();
            Compare(ObservationValue.FromString("input"), Prepare<ShapeId>(options), options);
        }
    }

    [Fact]
    public void WrapperCollectionsHaveBoundedRetainedResultAllocation()
    {
        var input = ObservationValue.FromObject(new { Values = Enumerable.Range(0, 128).Select(i => $"field{i}").ToArray() });
        var read = Prepare<WrapperCollection>();
        for (var i = 0; i < 20; i++) read(input);
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = read(input);
            samples[run] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(128, result.Values.Length);
            Assert.Equal("field127", result.Values[127].Value);
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 4_096);
    }

    [Fact]
    public void PublicPreparedJsonReaderUsesTheSameNativeWrapperInterpretation()
    {
        var input = ObservationValue.FromArray(Enumerable.Range(0, 128).Select(i => ObservationValue.FromString($"field{i}")).ToArray());
        var read = ObservationMaterializer.PrepareJsonValue<ShapeId[]>();
        for (var i = 0; i < 20; i++) read(input);
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = read(input)!;
            samples[run] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal("field127", result[127].Value);
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 4_096);
        Assert.NotSame(read(input), read(input));
    }

    [Fact]
    public void DefaultShapeMaterializerUsesItsSelectedWrapperConverter()
    {
        var graph = new ShapeGraph(new("decoder"), [new(new("row"), [new(new("id"), new ScalarTypeRef(ScalarTypeKind.String))])]);
        var shape = new GraphShapeId(graph, new("row"));
        var input = Cohesive.Model.Observation.Create(shape, new Dictionary<string, ObservationValue> { ["id"] = ObservationValue.FromString("field") });
        var read = ObservationMaterializer.For<WrapperRow>(shape).Map("id", row => row.Id).Compile();
        for (var i = 0; i < 20; i++) read.Materialize(input);
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 128; i++) read.Materialize(input);
            samples[run] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 128 * 96);
        Assert.Equal("field", read.Materialize(input).Id.Value);
    }

    static void Compare<T>(ObservationValue input, Func<ObservationValue, T> read, JsonSerializerOptions options)
    {
        T? expected = default, actual = default;
        var expectedError = Record.Exception(() => expected = input.DeserializeIndependent<T>(options));
        var actualError = Record.Exception(() => actual = read(input));
        Assert.Equal(expectedError?.GetType(), actualError?.GetType());
        if (expectedError is null) Assert.Equal(JsonSerializer.Serialize(expected, options), JsonSerializer.Serialize(actual, options));
    }

    public sealed record WrapperCollection(ImmutableArray<ShapeId> Values);
    public sealed record WrapperRow(ShapeId Id);
    [JsonConverter(typeof(SingleValueWrapperJsonConverter))]
    public readonly record struct NormalizedId
    {
        public string Value { get; }
        public NormalizedId(string value) => Value = value.Trim();
    }
    public sealed class PrefixStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => "prefix/" + reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
    public sealed class IdConverter : JsonConverter<ShapeId>
    {
        public override ShapeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new("custom/" + reader.GetString());
        public override void Write(Utf8JsonWriter writer, ShapeId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
}
