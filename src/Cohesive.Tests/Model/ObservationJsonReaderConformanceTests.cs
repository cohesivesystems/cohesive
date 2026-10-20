using System.Buffers;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ObservationJsonReaderConformanceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(4096)]
    public void Containers_KeepExactValuesAcrossInlineAndPooledBoundaries(int count)
    {
        var json = new StringBuilder("{\"values\":[");
        for (var index = 0; index < count; index++)
        {
            if (index != 0) json.Append(',');
            json.Append("{\"value\":").Append(index).Append(",\"text\":\"café 🚚\",\"null\":null}");
        }
        json.Append("],\"fields\":{");
        for (var index = 0; index < count; index++)
        {
            if (index != 0) json.Append(',');
            json.Append('"').Append(index).Append("\":").Append(index);
        }
        json.Append("}}");
        AssertEquivalent(json.ToString());
    }

    [Fact]
    public void DuplicateDecodedNames_KeepLastValueBeforeAndAfterDictionaryPromotion()
    {
        AssertEquivalent("""{"a":1,"\u0061":2,"b":3,"a":null}""");
        AssertEquivalent("""{"a":1,"b":2,"c":3,"d":4,"e":5,"f":6,"g":7,"h":8,"i":9,"\u0061":[2]}""");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    public void DuplicateReplacement_PreservesValuesAcrossObjectWidths(int width)
    {
        var properties = string.Join(',', Enumerable.Range(0, width).Select(index => $"\"field{index}\":{index}"));
        // Replacement at the beginning, middle and end exercises lookup on both storage paths.
        AssertEquivalent("{" + properties + $",\"field0\":null,\"field{width / 2}\":[true],\"field{width - 1}\":{{\"value\":42}}" + "}");
    }

    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    public void WiderObjectStorage_PreservesCanonicalBytesLookupAndIndependentUpdates(int width)
    {
        // Reverse source order so canonical ordering cannot depend on insertion order.
        var json = "{" + string.Join(',', Enumerable.Range(0, width).Reverse().Select(index => $"\"field{index}\":{index}")) + "}";
        using var document = JsonDocument.Parse(json);
        var expected = ObservationValue.FromJsonElement(document.RootElement);
        var actual = JsonSerializer.Deserialize<ObservationValue>(json);
        Assert.Equal(expected, actual);
        Assert.Equal(expected.GetHashCode(), actual.GetHashCode());
        Assert.Equal(Canonical(expected), Canonical(actual));
        for (var index = 0; index < width; index++)
            Assert.Equal(index, actual.GetProperty(new string($"field{index}".ToCharArray())).Int64);
        Assert.False(actual.TryGetProperty("field", out _));
        Assert.False(actual.TryGetProperty("field-1", out _));
        Assert.False(actual.TryGetProperty("zzzz", out _));
        var changed = actual.WithField(FieldPath.FromField("field0"), ObservationValue.FromInt64(-1));
        var removed = actual.WithoutField(FieldPath.FromField("field0"));
        Assert.Equal(0, actual.GetProperty("field0").Int64);
        Assert.Equal(-1, changed.GetProperty("field0").Int64);
        Assert.False(removed.TryGetProperty("field0", out _));
        Assert.Equal(width - 1, removed.Fields!.Count);
        // Later parsing cannot mutate a previously frozen vector or its replacement graph.
        _ = JsonSerializer.Deserialize<ObservationValue>(json.Replace(":0", ":999", StringComparison.Ordinal));
        Assert.Equal(Canonical(expected), Canonical(actual));
    }

    static byte[] Canonical(ObservationValue value)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);
        CanonicalJsonWriter.WriteCanonicalObservationValue(writer, value);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    [Fact]
    public void GeneratedNestedValues_MatchTheReferenceWithExactNumericKinds()
    {
        var random = new Random(1729);
        for (var sample = 0; sample < 128; sample++) AssertEquivalent(Generate(depth: 0));

        string Generate(int depth)
        {
            var choice = random.Next(depth == 5 ? 6 : 8);
            return choice switch
            {
                0 => "null", 1 => "true", 2 => "12345678901234567890.123456789", 3 => "1e-29",
                4 => "-0", 5 => "\"escaped \\u0061 \\n café\"",
                6 => "[" + string.Join(',', Enumerable.Range(0, random.Next(12)).Select(_ => Generate(depth + 1))) + "]",
                _ => "{" + string.Join(',', Enumerable.Range(0, random.Next(12)).Select(i => $"\"f{i}\":" + Generate(depth + 1))) + "}"
            };
        }
    }

    [Fact]
    public void SegmentedUtf8Input_AndNestedContainerDepthRemainSupported()
    {
        var json = new string('[', 48) + "{\"escaped\\u006bey\":1e-29}" + new string(']', 48);
        var bytes = Encoding.UTF8.GetBytes(json);
        var first = new Segment(bytes.AsMemory(0, bytes.Length / 2));
        var last = first.Append(bytes.AsMemory(bytes.Length / 2));
        var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
        reader.Read();
        var actual = ObservationJsonReader.ReadValue(ref reader);
        using var document = JsonDocument.Parse(bytes);
        AssertExact(ObservationValue.FromJsonElement(document.RootElement), actual);
    }

    [Theory]
    [InlineData("[1,2,3,4,5,6,7,8,9,")]
    [InlineData("{\"a\":[1,2,3,4,5,6,7,8,9],\"b\":")]
    [InlineData("[1,2,3,4,5,6,7,8,9,}")]
    public void MalformedInput_ThrowsAndCannotCorruptPreviouslyFrozenStorage(string malformed)
    {
        var valid = JsonSerializer.Deserialize<ObservationValue>("[1,2,3,4,5,6,7,8,9]");
        for (var repeat = 0; repeat < 10; repeat++)
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ObservationValue>(malformed));
            Assert.Equal(9, valid.Array.Length);
            Assert.Equal(9, valid.Array[8].Int64);
        }
    }

    [Fact]
    public void LargeArray_WarmAllocationTracksRetainedStorageRatherThanGrowingTemporaryArrays()
    {
        const int count = 4096;
        var bytes = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("1", count)) + "]");
        for (var warm = 0; warm < 20; warm++) GC.KeepAlive(JsonSerializer.Deserialize<ObservationValue>(bytes));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var value = JsonSerializer.Deserialize<ObservationValue>(bytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(count, value.Array.Length);
        // One exact 32-byte-per-value retained array, with room for serializer bookkeeping.
        Assert.InRange(allocated, 0, count * 32L + 2048);
    }

    static void AssertEquivalent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var actual = JsonSerializer.Deserialize<ObservationValue>(json);
        AssertExact(ObservationValue.FromJsonElement(document.RootElement), actual);
    }

    static void AssertExact(ObservationValue expected, ObservationValue actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected, actual);
        if (expected.Kind == ObservationValueKind.Array)
            for (var index = 0; index < expected.Array.Length; index++) AssertExact(expected.Array[index], actual.Array[index]);
        if (expected.Kind == ObservationValueKind.Object)
            foreach (var field in expected.Fields!) AssertExact(field.Value, actual.GetProperty(field.Key));
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
}
