using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ObservationSharedLayoutConformanceTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(32)]
    public void SharedLayout_PreservesCanonicalIdentityEscapesAndOrdinalReads(int width)
    {
        var shape = CreateShape(width);
        var layout = ObservationLayout.Create(shape);
        Assert.Same(layout, ObservationLayout.Create(shape));
        var json = Json(width).Replace("field0", "\\u0066ield0", StringComparison.Ordinal);
        var input = Encoding.UTF8.GetBytes(json);
        var actual = Read(shape, layout, input);
        Array.Fill(input, (byte)0);
        var expected = JsonSerializer.Deserialize<ObservationValue>(json);
        Assert.True(ObservationValidator.TryValidateAgainstShape(expected, shape.Graph.GetShape(shape.ShapeId), out _, shape.Graph));
        Assert.Equal(expected, actual);
        Assert.Equal(expected.GetHashCode(), actual.GetHashCode());
        Assert.Equal(Canonical(expected), Canonical(actual));
        var reader = Assert.IsAssignableFrom<IOrdinalObservationFieldReader>(actual.Fields);
        Assert.Same(layout, reader.Layout);
        for (var index = 0; index < width; index++)
        {
            Assert.True(reader.TryGetField(layout.GetOrdinal("field" + index), out var value));
            Assert.Equal(index, value.Int64);
        }
        Assert.False(reader.TryGetField(-1, out _));
        Assert.False(reader.TryGetField(width, out _));
        // The parser owns decoded values and keeps no references to mutable input bytes.
        _ = Read(shape, layout, Json(width).Replace(":0", ":999", StringComparison.Ordinal));
        Assert.Equal(0, actual.GetProperty("field0").Int64);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(32)]
    public void OptionalMissingAndNullRemainDistinct(int width)
    {
        var shape = CreateShape(width);
        var layout = ObservationLayout.Create(shape);
        var json = Json(width);
        var missing = Read(shape, layout, json.Replace($"\"field{width - 1}\":{width - 1},", "", StringComparison.Ordinal));
        var presentNull = Read(shape, layout, json.Replace($"\"field{width - 1}\":{width - 1}", $"\"field{width - 1}\":null", StringComparison.Ordinal));
        Assert.False(missing.TryGetProperty("field" + (width - 1), out _));
        Assert.Equal(width - 1, missing.Fields!.Count);
        Assert.True(presentNull.TryGetProperty("field" + (width - 1), out var value));
        Assert.Equal(ObservationValueKind.Null, value.Kind);
        Assert.NotEqual(missing, presentNull);
    }

    [Theory]
    [InlineData("{\"field0\":1,\"\\u0066ield0\":2}")]
    [InlineData("{\"FIELD0\":1}")]
    [InlineData("{\"unknown\":1}")]
    public void StrictLayoutRootPolicyDoesNotReplaceGeneralJsonPolicy(string json)
    {
        var shape = CreateShape(4);
        var layout = ObservationLayout.Create(shape);
        Assert.Throws<JsonException>(() => Read(shape, layout, json));
        // The generic interpretation continues to retain arbitrary exact-case keys and last duplicates.
        var plain = JsonSerializer.Deserialize<ObservationValue>(json);
        if (json.Contains("u0066", StringComparison.Ordinal)) Assert.Equal(2, plain.GetProperty("field0").Int64);
        else Assert.Single(plain.Fields!);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"field0\":null,\"field1\":1,\"field2\":2}")]
    [InlineData("{\"field0\":true,\"field1\":1,\"field2\":2}")]
    public void ShapedAdmissionStillRejectsMissingRequiredNullAndWrongType(string json)
    {
        var shape = CreateShape(4);
        var layout = ObservationLayout.Create(shape);
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();
        var values = new ObservationValue[4];
        Span<ulong> presence = stackalloc ulong[1];
        Assert.False(ObservationJsonReader.TryReadShape(ref reader, shape, layout, values, presence, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void SegmentedEscapedNamesRemainSupported()
    {
        var shape = CreateShape(16);
        var layout = ObservationLayout.Create(shape);
        var bytes = Encoding.UTF8.GetBytes(Json(16).Replace("field0", "\\u0066ield0", StringComparison.Ordinal));
        var split = Array.IndexOf(bytes, (byte)'u') + 2;
        var first = new Segment(bytes.AsMemory(0, split));
        var last = first.Append(bytes.AsMemory(split));
        var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
        reader.Read();
        var values = new ObservationValue[16];
        Span<ulong> presence = stackalloc ulong[1];
        Assert.True(ObservationJsonReader.TryReadShape(ref reader, shape, layout, values, presence, out _));
        Assert.Equal(0, values[layout.GetOrdinal("field0")].Int64);
        Assert.Equal(16, BitOperations.PopCount(presence[0]));
    }

    [Fact]
    public void LayoutCannotBeAppliedToAnotherQualifiedGraph()
    {
        var shape = CreateShape(4);
        var definition = shape.Graph.GetShape(shape.ShapeId);
        ShapeGraph otherGraph = new(new("another-layout-test-graph"), [definition]);
        GraphShapeId otherShape = new(otherGraph, definition.Id);
        Assert.Throws<ArgumentException>(() => Read(otherShape, ObservationLayout.Create(shape), Json(4)));
    }

    sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(32)]
    public void SharedLayoutAllocationTracksOnlyOwnedValuesAndWrapper(int width)
    {
        var shape = CreateShape(width);
        var layout = ObservationLayout.Create(shape);
        var bytes = Encoding.UTF8.GetBytes(Json(width));
        for (var index = 0; index < 100; index++) _ = Read(shape, layout, bytes);
        var before = GC.GetAllocatedBytesForCurrentThread();
        ObservationValue last = default;
        for (var index = 0; index < 100; index++) last = Read(shape, layout, bytes);
        var bytesPerRecord = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        Assert.InRange(bytesPerRecord, width * 32L, width * 32L + 96);
        GC.KeepAlive(last);
    }

    static GraphShapeId CreateShape(int width)
    {
        Shape definition = new(new("record"), [.. Enumerable.Range(0, width).Select(index => new FieldDefinition(
            new("field" + index), new ScalarTypeRef(ScalarTypeKind.Int64),
            presence: index == width - 1 ? FieldPresence.Optional : FieldPresence.Required,
            nullability: index == width - 1 ? FieldNullability.Nullable : FieldNullability.NonNullable))]);
        ShapeGraph graph = new(new("shared-layout-test"), [definition]);
        return new(graph, definition.Id);
    }

    static string Json(int width) => "{" + string.Join(',', Enumerable.Range(0, width).Reverse().Select(index => $"\"field{index}\":{index}")) + "}";
    static ObservationValue Read(GraphShapeId shape, ObservationLayout layout, string json) => Read(shape, layout, Encoding.UTF8.GetBytes(json));
    static ObservationValue Read(GraphShapeId shape, ObservationLayout layout, byte[] bytes)
    {
        var values = new ObservationValue[layout.Count];
        Span<ulong> presence = stackalloc ulong[1];
        var reader = new Utf8JsonReader(bytes);
        reader.Read();
        if (!ObservationJsonReader.TryReadShape(ref reader, shape, layout, values, presence, out var error))
            throw new InvalidOperationException(error);
        if (reader.Read()) throw new JsonException("Trailing content.");
        return ObservationValue.FromOrdinalFields(new OrdinalObservationFields(layout,
            ImmutableCollectionsMarshal.AsImmutableArray(values), BitOperations.PopCount(presence[0])));
    }

    static byte[] Canonical(ObservationValue value)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);
        CanonicalJsonWriter.WriteCanonicalObservationValue(writer, value);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }
}
