using System.Buffers;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CompactOrdinalObservationFieldsTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4)] [InlineData(16)] [InlineData(32)]
    [InlineData(63)] [InlineData(64)] [InlineData(65)] [InlineData(127)] [InlineData(128)]
    [InlineData(129)] [InlineData(256)] [InlineData(1025)]
    public void PackingPreservesPresenceRankOrderingAndOwnedStorage(int width)
    {
        var layout = Layout(width);
        foreach (var pattern in new[] { 0, 1, 2, 3 })
        {
            var slots = new ObservationValue[width];
            for (var ordinal = 0; ordinal < width; ordinal++)
                if (pattern == 2 || pattern == 1 && ordinal % 4 == 0 || pattern == 3 && ordinal == width - 1)
                    slots[ordinal] = ordinal % 3 == 0 ? ObservationValue.Null : ObservationValue.FromInt64(ordinal);
            var expectedFields = new Dictionary<string, ObservationValue>(StringComparer.Ordinal);
            for (var ordinal = width - 1; ordinal >= 0; ordinal--)
                if (slots[ordinal].Kind != ObservationValueKind.Undefined) expectedFields.Add(layout.FieldIdentities[ordinal], slots[ordinal]);
            var expected = ObservationValue.FromObject(expectedFields);
            var packed = CompactOrdinalObservationFields.Create(layout, slots);
            var actual = ObservationValue.FromObject(packed);
            Assert.Same(packed, actual.Fields); // The immutable format must not be snapshotted into a dictionary.
            Assert.Same(layout, packed.Layout);
            Assert.Equal(expectedFields.Count, packed.Count);
            Assert.Equal(expected, actual);
            Assert.Equal(expected.GetHashCode(), actual.GetHashCode());
            Assert.Equal(Canonical(expected), Canonical(actual));
            Assert.Equal(expectedFields.Keys.Order(StringComparer.Ordinal), packed.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(packed.Count, packed.Values.Count());
            Assert.Equal(packed.Count, packed.ToArray().Length);
            for (var ordinal = 0; ordinal < width; ordinal++)
            {
                var present = slots[ordinal].Kind != ObservationValueKind.Undefined;
                Assert.Equal(present, packed.TryGetField(ordinal, out var value));
                Assert.Equal(slots[ordinal], value);
                Assert.Equal(present, packed.TryGetValue(layout.FieldIdentities[ordinal], out var named));
                Assert.Equal(value, named);
            }
            Assert.False(packed.TryGetField(-1, out _)); Assert.False(packed.TryGetField(width, out _));
            Assert.False(packed.TryGetField(int.MaxValue, out _)); Assert.False(packed.ContainsKey("missing"));
            Assert.Throws<KeyNotFoundException>(() => packed["missing"]);
            Array.Clear(slots);
            Assert.Equal(expected, actual);
            Assert.Equal(Canonical(expected), Canonical(actual));
        }
    }

    [Fact]
    public void EveryValueKindRetainsItsPayloadAndCanonicalInterpretation()
    {
        ObservationValue[] kinds = [ObservationValue.Undefined, ObservationValue.Null, ObservationValue.FromInt64(long.MinValue),
            ObservationValue.FromDouble(-0d), new(ObservationValueKind.Decimal, dec: decimal.MaxValue), ObservationValue.FromBool(true),
            ObservationValue.FromString("é <>& 🚚 \\"), ObservationValue.FromBytes(new byte[] { 0, 255 }),
            ObservationValue.FromDateTimeOffset(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(-7))),
            ObservationValue.FromDateOnly(new DateOnly(2026, 10, 21)), ObservationValue.FromTimeOnly(new TimeOnly(12, 34, 56)),
            ObservationValue.FromTimeSpan(TimeSpan.FromDays(-1)), ObservationValue.EmptyObject,
            ObservationValue.FromImmutableArray([ObservationValue.Null, ObservationValue.FromDouble(1e-29)])];
        Assert.Equal(Enum.GetValues<ObservationValueKind>().Order(), kinds.Select(value => value.Kind).Order());
        var layout = Layout(256);
        var slots = new ObservationValue[layout.Count];
        var expected = new Dictionary<string, ObservationValue>(StringComparer.Ordinal);
        for (var index = 0; index < kinds.Length; index++)
        {
            var ordinal = index * 17;
            slots[ordinal] = kinds[index];
            if (kinds[index].Kind != ObservationValueKind.Undefined) expected.Add(layout.FieldIdentities[ordinal], kinds[index]);
        }
        var value = ObservationValue.FromObject(CompactOrdinalObservationFields.Create(layout, slots));
        Assert.Equal(ObservationValue.FromObject(expected), value);
        Assert.Equal(Canonical(ObservationValue.FromObject(expected)), Canonical(value));
        Assert.False(value.TryGetProperty(layout.FieldIdentities[0], out _)); // Undefined slots represent absence.
    }

    [Fact]
    public void DefaultOrdinalMaterializationCopiesMutableByteOutputs()
    {
        Shape definition = new(new("binary"), [new(new("Bytes"), new ScalarTypeRef(ScalarTypeKind.Bytes))]);
        ShapeGraph graph = new(new("packed-bytes"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var layout = ObservationLayout.Create(shape);
        var fields = CompactOrdinalObservationFields.Create(layout, [ObservationValue.FromBytes(new byte[] { 1, 2, 3 })]);
        var plan = ObservationMaterializer.For<BinaryRow>(shape).Compile(layout);
        var first = plan.Materialize(fields);
        first.Bytes[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, plan.Materialize(fields).Bytes);
    }

    [Fact]
    public void PackedAllocationTracksPresentValuesAndBoundReadsAllocateNothing()
    {
        var layout = Layout(256);
        var slots = new ObservationValue[256];
        foreach (var ordinal in new[] { 0, 63, 64, 255 }) slots[ordinal] = ObservationValue.FromInt64(ordinal);
        for (var index = 0; index < 100; index++) _ = CompactOrdinalObservationFields.Create(layout, slots);
        var before = GC.GetAllocatedBytesForCurrentThread();
        CompactOrdinalObservationFields? fields = null;
        for (var index = 0; index < 100; index++) fields = CompactOrdinalObservationFields.Create(layout, slots);
        Assert.InRange((GC.GetAllocatedBytesForCurrentThread() - before) / 100, 0, 512);
        for (var index = 0; index < 100; index++) _ = fields!.TryGetField(255, out _);
        before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        for (var index = 0; index < 10000; index++) if (fields!.TryGetField(255, out var value)) checksum += value.Int64;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); Assert.Equal(2550000, checksum);
    }

    [Fact]
    public void SourceBoundariesAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => CompactOrdinalObservationFields.Create(null!, []));
        Assert.Throws<ArgumentException>(() => CompactOrdinalObservationFields.Create(Layout(2), [ObservationValue.Null]));
    }

    static ObservationLayout Layout(int width)
    {
        Shape definition = new(new("record"), [.. Enumerable.Range(0, width).Select(index => new FieldDefinition(
            new("field" + index), new JsonTypeRef(JsonTypeKind.Any), presence: FieldPresence.Optional, nullability: FieldNullability.Nullable))]);
        ShapeGraph graph = new(new("packed-test-" + width), [definition]);
        return ObservationLayout.Create(new(graph, definition.Id), [.. definition.Fields.Reverse().Select(field => field.Name.Value)]);
    }

    static byte[] Canonical(ObservationValue value)
    {
        var output = new ArrayBufferWriter<byte>();
        CanonicalJsonWriter.WriteCanonicalObservationValue(output, value);
        return output.WrittenSpan.ToArray();
    }

    public sealed record BinaryRow(byte[] Bytes);
}
