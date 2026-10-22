using System.Buffers;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ObservationJsonStringReuseTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    public void SmallStringArraysAllocateOnlyOwnedResultStorage(int count)
    {
        var bytes = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("\"repeat\"", count)) + "]");
        for (var warm = 0; warm < 10; warm++) _ = Read(bytes);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var value = Read(bytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(count, value.Array.Length);
        // One owned 40-byte string and one 32-byte value per item; one final array header.
        // Warm pooled scratch is excluded, and a small traversal needs no cache owner/table.
        Assert.InRange(allocated, 0, count * 72L + 64);
    }

    [Fact]
    public void LongPrefixThenShortRepeatedTextPreservesOwnershipAndReuse()
    {
        var longText = new string('x', 1024);
        var prefix = string.Join(',', Enumerable.Repeat(JsonSerializer.Serialize(longText), 64));
        var repeated = string.Join(',', Enumerable.Repeat("\"repeat\"", 128));
        var bytes = Encoding.UTF8.GetBytes("[" + prefix + "," + repeated + "]");
        var value = Read(bytes);
        Assert.Equal(longText, value.Array[0].String);
        Assert.Same(value.Array[^1].String, value.Array[^2].String);
        bytes.AsSpan().Fill((byte)' ');
        Assert.Equal("repeat", value.Array[^1].String);
        Assert.Equal(longText, value.Array[0].String);
    }

    [Fact]
    public void RepeatedNamesAndValuesShareOwnedStringsOnlyWithinOneTraversal()
    {
        var bytes = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("{\"repeated text\":\"repeated text\"}", 512)) + "]");
        var first = Read(bytes);
        var last = first.Array[^1];
        Assert.Same(last.Fields!.Keys.Single(), last.GetProperty("repeated text").String);
        Assert.Same(last.GetProperty("repeated text").String, first.Array[^2].GetProperty("repeated text").String);
        var second = Read(bytes);
        Assert.NotSame(last.GetProperty("repeated text").String, second.Array[^1].GetProperty("repeated text").String);
        bytes.AsSpan().Fill((byte)' ');
        Assert.Equal("repeated text", last.GetProperty("repeated text").String);
    }

    [Theory]
    [InlineData("\"repeat\"")]
    [InlineData("\"repe\\u0061t\"")]
    [InlineData("\"café 🚚\"")]
    [InlineData("\"\\uD83D\\uDE9A\"")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    [InlineData("\"\\uD800x\"")]
    public void ActiveReusePreservesEscapingDuplicateNamesAndParserFailures(string suffix)
    {
        var bytes = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("\"repeat\"", 128)) + "," + suffix + "]");
        AssertReference(bytes);
        AssertReference(bytes, segmented: true);
    }

    [Theory]
    [InlineData(0xFF)]
    [InlineData(0xC0)]
    [InlineData(0x80)]
    public void ActiveReuseCannotAcceptInvalidUtf8(int invalidByte)
    {
        var prefix = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("\"repeat\"", 128)) + ",\"repeat");
        byte[] bytes = [.. prefix, (byte)invalidByte, (byte)'"', (byte)']'];
        AssertReference(bytes);
        AssertReference(bytes, segmented: true);
    }

    [Fact]
    public void ACacheCollisionCannotReplaceInvalidUtf8WithAValidReplacementCharacter()
    {
        byte[]? valid = null, invalid = null;
        for (var index = 0; index < 65536; index++)
        {
            var prefix = Encoding.UTF8.GetBytes("replacement" + index);
            byte[] candidateValid = [.. prefix, 0xEF, 0xBF, 0xBD];
            byte[] candidateInvalid = [.. prefix, 0xFF];
            if (ObservationJsonPropertyNames.Bucket(candidateValid) != ObservationJsonPropertyNames.Bucket(candidateInvalid)) continue;
            valid = candidateValid;
            invalid = candidateInvalid;
            break;
        }
        Assert.NotNull(valid);
        Assert.NotNull(invalid);
        var prefixJson = "[" + string.Join(',', Enumerable.Repeat("\"" + Encoding.UTF8.GetString(valid) + "\"", 128)) + ",\"";
        byte[] bytes = [.. Encoding.UTF8.GetBytes(prefixJson), .. invalid, (byte)'"', (byte)']'];
        AssertReference(bytes);
    }

    [Fact]
    public void UniqueAndLargeTextDoesNotChangeValueSemanticsOrRetainFailedReadState()
    {
        var unique = "[" + string.Join(',', Enumerable.Range(0, 2048).Select(index => $"{{\"key{index}\":\"value{index}\"}}")) + "]";
        AssertReference(Encoding.UTF8.GetBytes(unique));
        var text = new string('x', 8192);
        var longValues = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat(JsonSerializer.Serialize(text), 128)) + "]");
        AssertReference(longValues);
        var malformed = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("\"repeat\"", 128)) + ",}");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.ThrowsAny<JsonException>(() => Read(malformed));
            Assert.Equal(text, Read(longValues).Array[^1].String);
        }
    }

    [Fact]
    public void RepeatedShortStringsAddBoundedStorageRatherThanOneStringPerItem()
    {
        const int Count = 4096;
        var bytes = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("\"repeat\"", Count)) + "]");
        for (var warm = 0; warm < 10; warm++) _ = Read(bytes);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var value = Read(bytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Count, value.Array.Length);
        // Required 32-byte values, plus the bounded activation prefix and owner; no per-item text.
        Assert.InRange(allocated, 0, Count * 32L + 8192);
    }

    [Fact]
    public void TypedArrayReuseDoesNotConveySemanticAdmission()
    {
        var type = new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.String));
        var graph = new ShapeGraph(new("string-reuse"), [], []);
        var prefix = string.Join(',', Enumerable.Repeat("\"repeat\"", 512));
        foreach (var finalItem in new[] { "\"repeat\"", "7" })
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes("[" + prefix + "," + finalItem + "]"));
            reader.Read();
            var value = ObservationJsonReader.ReadTypedValue(ref reader, type, graph);
            Assert.Same(value.Array[510].String, value.Array[511].String);
            Assert.Equal(finalItem != "7", ObservationValidator.TryValidateAgainstType(value, type, out _, graph));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TaggedReuseOwnsStringsAcrossCanonicalAndDeferredPayloads(bool payloadFirst)
    {
        var text = "repeated text";
        while (new[] { "array", "object", "string" }.Any(kind =>
            ObservationJsonPropertyNames.Bucket(Encoding.UTF8.GetBytes(kind)) == ObservationJsonPropertyNames.Bucket(Encoding.UTF8.GetBytes(text)))) text += "x";
        var quoted = JsonSerializer.Serialize(text);
        var leaf = payloadFirst ? "{\"$value\":" + quoted + ",\"$kind\":\"string\"}"
            : "{\"$kind\":\"string\",\"$value\":" + quoted + "}";
        var body = "{" + quoted + ":" + leaf + "}";
        var item = payloadFirst ? "{\"$value\":" + body + ",\"$kind\":\"object\"}"
            : "{\"$kind\":\"object\",\"$value\":" + body + "}";
        var payload = "[" + string.Join(',', Enumerable.Repeat(item, 512)) + "]";
        var json = payloadFirst ? "{\"$value\":" + payload + ",\"$kind\":\"array\"}"
            : "{\"$kind\":\"array\",\"$value\":" + payload + "}";
        var options = new JsonSerializerOptions();
        options.Converters.Add(PortableValueJsonConverter.TaggedObservationValues);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = JsonSerializer.Deserialize<ObservationValue>(bytes, options);
        Assert.Same(result.Array[^1].Fields!.Keys.Single(), result.Array[^1].GetProperty(text).String);
        Assert.Same(result.Array[^2].GetProperty(text).String, result.Array[^1].GetProperty(text).String);
        bytes.AsSpan().Clear();
        Assert.Equal(text, result.Array[^1].GetProperty(text).String);
    }

    static void AssertReference(byte[] bytes, bool segmented = false)
    {
        ObservationValue expected = default, actual = default;
        var expectedError = Record.Exception(() =>
        {
            // Decode every scalar with the parser itself: the element cache is not the reference
            // for malformed-text admission, because it also performs bounded string reuse.
            using var document = JsonDocument.Parse(bytes);
            foreach (var element in document.RootElement.EnumerateArray())
                if (element.ValueKind == JsonValueKind.String) _ = element.GetString();
            expected = ObservationValue.FromJsonElement(document.RootElement, names: null);
        });
        var actualError = Record.Exception(() => actual = Read(bytes, segmented));
        Assert.Equal(expectedError?.GetType(), actualError?.GetType());
        if (expectedError is null) Assert.Equal(expected, actual);
    }

    static ObservationValue Read(byte[] bytes, bool segmented = false)
    {
        if (!segmented)
        {
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            return ObservationJsonReader.ReadValue(ref reader);
        }
        var first = new Segment(bytes.AsMemory(0, 1));
        var last = first;
        for (var index = 1; index < bytes.Length; index++) last = last.Append(bytes.AsMemory(index, 1));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        var segmentedReader = new Utf8JsonReader(sequence);
        segmentedReader.Read();
        return ObservationJsonReader.ReadValue(ref segmentedReader);
    }

    sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
