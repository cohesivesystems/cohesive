using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cohesive.Tests.Model;

public sealed class NativeStringAnnotationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("é 😀 <>& \" \\ \n")]
    [InlineData(null)]
    public void NativeAuthoringPreservesJsonSnapshotWireAndHash(string? text)
    {
        var expected = new AnnotationValue(JsonSerializer.SerializeToElement(text));
        foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var options = new JsonSerializerOptions { Encoder = encoder };
            var actual = AnnotationValue.FromString(text!);
            var before = JsonSerializer.Serialize(actual, options);
            Assert.Equal(JsonSerializer.Serialize(expected, options), before);
            Assert.Equal(expected, actual);
            Assert.Equal(actual, expected);
            Assert.Equal(expected.GetHashCode(), actual.GetHashCode());
            Assert.True(JsonElement.DeepEquals(expected.Value, actual.Value));
            Assert.Equal(before, JsonSerializer.Serialize(actual, options));
        }
    }

    [Fact]
    public void MalformedUtf16RetainsSerializerNormalization()
    {
        foreach (var text in new[] { new string((char)0xD800, 1), new string((char)0xDC00, 1), new string(new[] { (char)0xD800, 'x' }) })
            NativeAuthoringPreservesJsonSnapshotWireAndHash(text);
    }

    [Fact]
    public void FlattenAndObjectAuthoringKeepNativeStringsWithoutRequiringViews()
    {
        var annotations = AnnotationMap.Create("text", "é 😀 <>&");
        Assert.Equal("é 😀 <>&", AnnotationMap.FlattenScalars(annotations)["text"]);
        var composed = AnnotationValue.FromObject((IEnumerable<KeyValuePair<AnnotationKey, AnnotationValue>>)annotations);
        Assert.Equal("é 😀 <>&", composed.Value.GetProperty("text").GetString());
    }

    [Fact]
    public void LazyViewIsOwnedAndSafeDuringConcurrentReadsWritesAndEquality()
    {
        var annotation = AnnotationValue.FromString("é 😀 <>&");
        var expected = JsonSerializer.Serialize(annotation);
        Parallel.For(0, 128, _ =>
        {
            Assert.Equal("é 😀 <>&", annotation.Value.GetString());
            Assert.Equal(expected, JsonSerializer.Serialize(annotation));
            Assert.Equal(AnnotationValue.FromString("é 😀 <>&"), annotation);
            Assert.Equal(annotation.GetHashCode(), AnnotationValue.FromString("é 😀 <>&").GetHashCode());
        });
        var copy = annotation with { };
        Assert.Equal(annotation, copy);
        Assert.Equal(expected, JsonSerializer.Serialize(copy));
    }

    [Fact]
    public void NativeStringCreationAndSerializationHaveFixedAllocationCeiling()
    {
        // Reuse output storage; measure real public authoring + serializer without asking for Value.
        var text = new string('x', 1024);
        var buffer = new ArrayBufferWriter<byte>(2048);
        using var writer = new Utf8JsonWriter(buffer);
        for (var warm = 0; warm < 20; warm++) Write();
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 128; i++) Write();
            samples[run] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 128 * 96);
        Assert.Equal(1026, buffer.WrittenCount);

        void Write()
        {
            buffer.Clear();
            writer.Reset(buffer);
            JsonSerializer.Serialize(writer, AnnotationValue.FromString(text));
            writer.Flush();
        }
    }

    [Fact]
    public void ParsedStringsRetainOriginalOwnedJsonSnapshot()
    {
        const string json = "\"na\\u006de\"";
        var parsed = JsonSerializer.Deserialize<AnnotationValue>(json)!;
        Assert.Equal(json, parsed.Value.GetRawText());
        Assert.Equal(AnnotationValue.FromString("name"), parsed);
        Assert.Equal(parsed.GetHashCode(), AnnotationValue.FromString("name").GetHashCode());
    }
}
