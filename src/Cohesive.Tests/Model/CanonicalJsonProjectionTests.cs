using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CanonicalJsonProjectionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("{\"\":0,\"a\":1,\"\":2,\"\\u0000\":3}")]
    [InlineData("{\"z\":1.00,\"a\":1,\"\\u0061\":2,\"a\":3}")]
    [InlineData("{\"\\uE000\":1,\"😀\":2,\"\\uD83D\\uDE00\":3,\"\\uFFFF\":4}")]
    [InlineData("{\"a/b\":1,\"a~b\":2,\"\\n\":3,\"\":4,\"é\":5}")]
    [InlineData("[null,true,false,1.00,-0,1e+03,\"<>&\\n🙂\"]")]
    public void PooledHashMatchesIndependentLexicalReference(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var options = new JsonWriterOptions { Encoder = encoder };
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, options)) CanonicalJsonSequenceProfileTests.WriteReference(writer, document.RootElement);
            Assert.Equal(SHA256.HashData(stream.ToArray()), CanonicalJsonWriter.ComputeCanonicalSequenceSha256(
                document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical, options));
        }
    }

    [Fact]
    public void GeneratedUnicodeKeysMatchUtf16OrdinalReference()
    {
        int[] scalars = [1, 65, 127, 128, 255, 256, 0x7FF, 0x800, 0xD7FF, 0xE000, 0xFFFF,
            0x10000, 0x10001, 0x103FF, 0x10400, 0x1F600, 0x20000, 0x10FFFF];
        var keys = scalars.SelectMany(scalar => new[]
        {
            new Rune(scalar).ToString(), "prefix/" + new Rune(scalar),
            "prefix/" + new Rune(scalar) + "/suffix"
        }).Append("").Append("prefix/").ToArray();
        PooledHashMatchesIndependentLexicalReference(JsonSerializer.Serialize(
            keys.Reverse().Select((key, index) => new KeyValuePair<string, int>(key, index)).ToDictionary()));
    }

    [Fact]
    public void LongEscapedNamesMatchReferenceAcrossScratchGrowth()
    {
        var payload = new Dictionary<string, object>
        {
            [new string('é', 64) + "🙂"] = 1,
            [new string('é', 512) + "\n"] = 2,
            [new string('é', 2048) + "🙂"] = new { nested = 3 }
        };
        PooledHashMatchesIndependentLexicalReference(JsonSerializer.Serialize(payload));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(193)]
    public void InvalidUtf8NamesMatchBclRejection(byte invalid)
    {
        using var document = JsonDocument.Parse(new byte[] { 123, 34, invalid, 34, 58, 49, 125 });
        var expected = Assert.Throws<InvalidOperationException>(() => document.RootElement.EnumerateObject().First().Name);
        var actual = Assert.Throws<InvalidOperationException>(() => CanonicalJsonWriter.ComputeCanonicalSequenceSha256(
            document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical));
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public void ExclusionsAreExactPathsAndPreserveUnrelatedFieldsAndRepeatedOrder()
    {
        using var document = JsonDocument.Parse("""
            {"contentHash":"retain","identity":{"keep":1.00,"contentHash":"omit"},
             "closure":{"inventoryHash":"omit","nested":{"inventoryHash":"retain"}},
             "canonicalHash":"omit","canonicalHash":"omit too","message":"omit",
             "children":[{"message":"retain","identity":{"contentHash":"retain"}}],"z":-0}
            """);
        var exclusions = new CanonicalJsonPropertyExclusions("/canonicalHash", "/identity/contentHash",
            "/closure/inventoryHash", "/message");
        Assert.Equal("""
            {"children":[{"identity":{"contentHash":"retain"},"message":"retain"}],"closure":{"nested":{"inventoryHash":"retain"}},"contentHash":"retain","identity":{"keep":1.00},"z":-0}
            """, Write(document.RootElement, exclusions));
    }

    [Fact]
    public void ExclusionPathsCopyConfigurationAndDecodePointerEscapes()
    {
        var paths = new[] { "/a~1b/~0", "/" };
        var exclusions = new CanonicalJsonPropertyExclusions(paths);
        paths[0] = "/retained";
        using var document = JsonDocument.Parse("{\"a/b\":{\"~\":1,\"retained\":2},\"\":3,\"retained\":4}");
        Assert.Equal("{\"a/b\":{\"retained\":2},\"retained\":4}", Write(document.RootElement, exclusions));
    }

    [Theory]
    [InlineData("")]
    [InlineData("identity/hash")]
    [InlineData("/bad~")]
    [InlineData("/bad~2")]
    public void InvalidExclusionPathsFailDuringPreparation(string path) =>
        Assert.Throws<ArgumentException>(() => new CanonicalJsonPropertyExclusions(path));

    [Fact]
    public void OmittedSubtreesAreProjectionRatherThanAdmission()
    {
        using var document = JsonDocument.Parse("{\"excluded\":{\"bad\":\"\\uD800\"},\"retained\":1.00}");
        var exclusions = new CanonicalJsonPropertyExclusions("/excluded");
        Assert.Equal("{\"retained\":1.00}", Write(document.RootElement, exclusions));
        Assert.Throws<InvalidOperationException>(() => Write(document.RootElement, null));
        Assert.Equal(SHA256.HashData("{\"retained\":1.00}"u8), CanonicalJsonWriter.ComputeCanonicalSequenceSha256(
            document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical, exclusions: exclusions));
    }

    [Fact]
    public void StrictHashPreservesNormalizationAndDuplicateRejection()
    {
        using var document = JsonDocument.Parse("{\"omit\":0,\"b\":1.00,\"a\":-0}");
        Assert.Equal(SHA256.HashData("{\"a\":0,\"b\":1}"u8), CanonicalJsonWriter.ComputeCanonicalSequenceSha256(
            document.RootElement, CanonicalJsonSequenceProfile.ExactDecimalRational,
            exclusions: new("/omit")));
        using var repeated = JsonDocument.Parse("{\"a\":1,\"\\u0061\":2}");
        var error = Assert.Throws<ArgumentException>(() => CanonicalJsonWriter.ComputeCanonicalSequenceSha256(
            repeated.RootElement, CanonicalJsonSequenceProfile.ExactDecimalRational));
        Assert.Equal("Duplicate JSON property 'a'.", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WideNamesAndLargeRowsHaveFixedWarmHashAllocationCeiling(bool escapedNames)
    {
        var raw = escapedNames
            ? JsonSerializer.Serialize(Enumerable.Range(0, 128).Reverse().ToDictionary(i => $"{i:D3}/é/🙂/\n", i => i))
            : "[" + string.Join(",", Enumerable.Repeat("{\"text\":\"" + new string('a', 512) + "\",\"number\":1.2300}", 4096)) + "]";
        using var document = JsonDocument.Parse(raw);
        byte[] Hash() => CanonicalJsonWriter.ComputeCanonicalSequenceSha256(document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical);
        for (var warm = 0; warm < 20; warm++) Hash();
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var digest = Hash();
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(digest);
        }
        Array.Sort(samples);
        output.WriteLine($"Borrowed hash escaped names={escapedNames}: {samples[3]} B");
        Assert.InRange(samples[3], 0, 1_536);
    }

    [Fact]
    public async Task SharedExclusionsDoNotShareInvocationStorage()
    {
        using var document = JsonDocument.Parse("{\"identity\":{\"contentHash\":\"omit\",\"id\":\"retain\"},\"value\":1.00}");
        var exclusions = new CanonicalJsonPropertyExclusions("/identity/contentHash");
        var expected = CanonicalJsonWriter.ComputeCanonicalSequenceSha256(document.RootElement,
            CanonicalJsonSequenceProfile.PreserveLexical, exclusions: exclusions);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                Assert.Equal(expected, CanonicalJsonWriter.ComputeCanonicalSequenceSha256(document.RootElement,
                    CanonicalJsonSequenceProfile.PreserveLexical, exclusions: exclusions));
        })));
    }

    static string Write(JsonElement element, CanonicalJsonPropertyExclusions? exclusions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            CanonicalJsonWriter.WriteCanonicalSequence(writer, element, CanonicalJsonSequenceProfile.PreserveLexical, exclusions);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

}
