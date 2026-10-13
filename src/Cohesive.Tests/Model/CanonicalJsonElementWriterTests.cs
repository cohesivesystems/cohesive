using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CanonicalJsonElementWriterTests
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    static CanonicalJsonArrayOrdering Ordering(CanonicalJsonArrayPath path) => path.Value switch
    {
        "/sets" => CanonicalJsonArrayOrdering.ObjectSet("id"),
        "/sets/*/tags" or "/a~1b~2~0" => CanonicalJsonArrayOrdering.StringSet,
        _ => CanonicalJsonArrayOrdering.Sequence
    };

    [Theory]
    [InlineData(CanonicalJsonNumberSemantics.PortableObservation)]
    [InlineData(CanonicalJsonNumberSemantics.ExactDecimalRational)]
    public void ImmutableWriterMatchesNodeWriterAcrossKindsNumbersAndNestedOrdering(CanonicalJsonNumberSemantics semantics)
    {
        string[] numbers = ["-0.00e99", "1.2300", "1e-7", "1e-6", "1e20", "1e21", "18446744073709551615",
            "12345678901234567890.123456789", "1.0000000000000001e18"];
        HashSet<JsonValueKind> covered = [];
        Random random = new(78113);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var number = numbers[random.Next(numbers.Length)];
            var json = $$$"""{"sets":[{"id":"β","tags":["λ","a"],"seq":[true,false,null,{{{number}}}]},{"id":"a","empty":{},"seq":[]}],"text":"λ/\\\"<>&","a/b*~":["b","a"]}""";
            using var parsed = JsonDocument.Parse(json);
            var oldPaths = new List<string>();
            var newPaths = new List<string>();
            var expected = CanonicalJsonWriter.GetCanonicalBytes(JsonNode.Parse(json)!, Options,
                path => { oldPaths.Add(path.Value); return Ordering(path); }, semantics);
            var actual = CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement,
                path => { newPaths.Add(path.Value); return Ordering(path); }, semantics);
            Assert.Equal(expected, actual);
            Assert.Equal(oldPaths, newPaths);
            Visit(parsed.RootElement);
        }
        Assert.Equal(Enum.GetValues<JsonValueKind>().Where(kind => kind != JsonValueKind.Undefined).Order(), covered.Order());

        void Visit(JsonElement element)
        {
            covered.Add(element.ValueKind);
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) Visit(property.Value);
            if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Visit(item);
        }
    }

    [Theory]
    [InlineData("[{\"id\":null}]", true)]
    [InlineData("[{\"id\":1}]", true)]
    [InlineData("[null]", true)]
    [InlineData("[{\"id\":\"z\"},{\"id\":\"a\"},{\"id\":\"z\"},{\"id\":\"a\"}]", true)]
    [InlineData("[null]", false)]
    [InlineData("[1]", false)]
    [InlineData("[\"z\",\"a\",\"z\",\"a\"]", false)]
    public void SetFailuresPreserveFirstDuplicateAndExactDiagnostics(string json, bool objectSet)
    {
        var ordering = objectSet ? CanonicalJsonArrayOrdering.ObjectSet("id") : CanonicalJsonArrayOrdering.StringSet;
        using var parsed = JsonDocument.Parse(json);
        var expected = Assert.Throws<InvalidOperationException>(() =>
            CanonicalJsonWriter.GetCanonicalBytes(JsonNode.Parse(json)!, Options, _ => ordering));
        var actual = Assert.Throws<InvalidOperationException>(() =>
            CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement, _ => ordering));
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public void DuplicatePropertiesAndUndefinedInputAreRejected()
    {
        using var parsed = JsonDocument.Parse("{\"nested\":{\"a\":1,\"a\":2}}");
        Assert.Throws<ArgumentException>(() => CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement, Ordering));
        Assert.Throws<InvalidOperationException>(() => CanonicalJsonWriter.GetCanonicalBytes(default(JsonElement), Ordering));
        Assert.Throws<ArgumentOutOfRangeException>(() => CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement, Ordering,
            (CanonicalJsonNumberSemantics)99));
    }

    [Fact]
    public void PropertyNameReusePreservesEscapingCollisionsAndFailureRecovery()
    {
        using var duplicate = JsonDocument.Parse("""{"x":1,"\u0078":2}""");
        Assert.Equal("Duplicate JSON property 'x'.", Assert.Throws<ArgumentException>(() =>
            CanonicalJsonWriter.GetCanonicalBytes(duplicate.RootElement, Ordering)).Message);
        var rows = Enumerable.Range(0, 128).Select(index => new Dictionary<string, int>
        {
            [index % 2 == 0 ? "é" : "different"] = index,
            [index % 3 == 0 ? "a/b*~" : "quote\""] = index + 1,
            ["𝄞"] = index + 2
        }).ToArray();
        var json = JsonSerializer.Serialize(new { rows });
        using var parsed = JsonDocument.Parse(json);
        var expected = CanonicalJsonWriter.GetCanonicalBytes(JsonNode.Parse(json)!, Options, Ordering,
            CanonicalJsonNumberSemantics.ExactDecimalRational);
        Parallel.For(0, 16, _ => Assert.Equal(expected,
            CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement, Ordering, CanonicalJsonNumberSemantics.ExactDecimalRational)));
    }

    [Fact]
    public void RepeatedPropertyNamesDoNotAllocatePerRowDuringCanonicalWriting()
    {
        using var parsed = JsonDocument.Parse("{\"rows\":[" + string.Join(",", Enumerable.Repeat(
            "{\"alpha\":1,\"beta\":true,\"gamma\":\"x\"}", 4096)) + "]}");
        var write = typeof(CanonicalJsonWriter).GetMethod("WriteCanonicalSequence",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<Utf8JsonWriter, JsonElement>>();
        ArrayBufferWriter<byte> buffer = new(1_000_000);
        for (var index = 0; index < 16; index++) Write();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Write();
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16_384);

        void Write()
        {
            buffer.Clear();
            using var writer = new Utf8JsonWriter(buffer);
            write(writer, parsed.RootElement);
        }
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public void NonportableNumbersRetainRejectionAndExactProfileSupport(string number)
    {
        using var parsed = JsonDocument.Parse(number);
        Assert.Throws<InvalidOperationException>(() => CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement, Ordering));
        Assert.Equal(CanonicalJsonWriter.GetCanonicalBytes(JsonNode.Parse(number)!, Options, Ordering,
            CanonicalJsonNumberSemantics.ExactDecimalRational), CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement, Ordering,
            CanonicalJsonNumberSemantics.ExactDecimalRational));
    }

    [Fact]
    public void ImmutableCanonicalizationAvoidsMutableTreeMaterializationAllocation()
    {
        var json = "{\"rows\":[" + string.Join(",", Enumerable.Repeat(
            "{\"z\":\"example\",\"a\":[1,2,null],\"nested\":{\"enabled\":true}}", 512)) + "]}";
        using var parsed = JsonDocument.Parse(json);
        for (var iteration = 0; iteration < 8; iteration++)
            Assert.Equal(Reference(), Direct());
        var before = GC.GetAllocatedBytesForCurrentThread();
        var expected = Reference();
        var referenceBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var actual = Direct();
        var directBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        Assert.True(directBytes < referenceBytes / 2, $"Immutable writer {directBytes} B, node materialization {referenceBytes} B.");

        byte[] Reference() => CanonicalJsonWriter.GetCanonicalBytes(JsonNode.Parse(json)!, Options, Ordering);
        byte[] Direct() => CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement, Ordering);
    }
}
