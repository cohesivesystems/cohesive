using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Cohesive.Tests.Model;

[Collection(CoreObservationPerformanceTestCollection.Name)]
public sealed class ObservationJsonWritingTests
{
    [Theory]
    [InlineData("small")]
    [InlineData("owned")]
    [InlineData("immutable")]
    [InlineData("sorted")]
    [InlineData("ordinal")]
    public void WritingPreservesStoredOrder(string storage)
    {
        var value = Leaf(storage);
        var expected = "{" + string.Join(",", value.Fields!.Select(field => JsonSerializer.Serialize(field.Key) + ":" + field.Value.Int64)) + "}";
        Assert.Equal(expected, value.GetRawText());
        if (storage is "owned" or "ordinal") Assert.Equal("{\"z\":1,\"a\":2}", expected);
    }

    [Theory]
    [InlineData("small", 128)]
    [InlineData("small", 1024)]
    [InlineData("owned", 128)]
    [InlineData("owned", 1024)]
    [InlineData("ordinal", 128)]
    [InlineData("ordinal", 1024)]
    public void NestedWritingDoesNotAllocatePerObject(string storage, int nodes)
    {
        var child = Leaf(storage);
        var value = ObservationValue.FromArray([.. Enumerable.Repeat(child, nodes)]);
        var output = new ArrayBufferWriter<byte>(nodes * 128);
        using var writer = new Utf8JsonWriter(output);
        void Write() { output.Clear(); writer.Reset(output); value.WriteTo(writer); writer.Flush(); }
        for (var i = 0; i < 32; i++) Write();
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 8; i++) Write();
            samples[run] = (GC.GetAllocatedBytesForCurrentThread() - before) / 8;
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 256);
        using var json = JsonDocument.Parse(output.WrittenMemory);
        Assert.Equal(nodes, json.RootElement.GetArrayLength());
        Assert.Equal(1, json.RootElement[0].GetProperty("z").GetInt64());
    }

    [Fact]
    public void NestedBytesFailureLeavesSubsequentWritesIndependent()
    {
        var value = ObservationValue.FromArray([ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        { ["bytes"] = ObservationValue.FromBytes(new byte[] { 0, 255 }) })]);
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);
        Assert.Throws<InvalidOperationException>(() => value.WriteTo(writer));
        output.Clear(); writer.Reset(output);
        value.WriteTo(writer, ObservationBytesJsonEncoding.Base64String); writer.Flush();
        Assert.Equal("[{\"bytes\":\"AP8=\"}]", Encoding.UTF8.GetString(output.WrittenSpan));
    }

    static ObservationValue Leaf(string storage)
    {
        Dictionary<string, ObservationValue> fields = new(StringComparer.Ordinal)
        { ["z"] = ObservationValue.FromInt64(1), ["a"] = ObservationValue.FromInt64(2) };
        switch (storage)
        {
            case "small":
                using (var json = JsonDocument.Parse("{\"z\":1,\"a\":2}")) return ObservationValue.FromJsonElement(json.RootElement);
            case "owned": return ObservationValue.FromObject(fields);
            case "immutable": return ObservationValue.FromObject(fields.ToImmutableDictionary(StringComparer.Ordinal));
            case "sorted": return ObservationValue.FromObject(fields.ToImmutableSortedDictionary(StringComparer.Ordinal));
            case "ordinal":
                Shape shape = new(new("json-write"), [new(new("a"), new ScalarTypeRef(ScalarTypeKind.Int64)), new(new("z"), new ScalarTypeRef(ScalarTypeKind.Int64))]);
                ShapeGraph graph = new(new("json-write-graph"), [shape]);
                var layout = ObservationLayout.Create(new(graph, shape.Id), ["z", "a"]);
                return ObservationValue.FromOrdinalFields(new(layout, [fields["z"], fields["a"]], 2));
            default: throw new ArgumentOutOfRangeException(nameof(storage));
        }
    }
}
