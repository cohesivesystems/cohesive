using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CanonicalIntegerNormalizationTests
{
    delegate string NormalizeText(ReadOnlySpan<char> text);
    static readonly NormalizeText Reference = typeof(CanonicalJsonWriter)
        .GetMethod("GetExactDecimalRationalText", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<NormalizeText>();
    static readonly Action<Utf8JsonWriter, JsonElement> Write = typeof(CanonicalJsonWriter)
        .GetMethod("WriteCanonicalSequence", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Action<Utf8JsonWriter, JsonElement>>();

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("-9223372036854775808")]
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("100000000000000000000")]
    [InlineData("1000000000000000000000")]
    [InlineData("1.0000000000000001e18")]
    [InlineData("-0.00e99")]
    [InlineData("0.000001")]
    [InlineData("0.0000001")]
    [InlineData("1e999")]
    public void IntegerFastPathAndFallbackMatchExactTextNormalizer(string text)
    {
        using var parsed = JsonDocument.Parse(text);
        var actual = CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement,
            static _ => CanonicalJsonArrayOrdering.Sequence, CanonicalJsonNumberSemantics.ExactDecimalRational);
        Assert.Equal(Reference(text), System.Text.Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void GeneratedSignedIntegersMatchExactTextNormalizer()
    {
        Random random = new(71913);
        for (var i = 0; i < 256; i++)
        {
            var text = random.NextInt64(long.MinValue, long.MaxValue).ToString(CultureInfo.InvariantCulture);
            using var parsed = JsonDocument.Parse(text);
            var actual = CanonicalJsonWriter.GetCanonicalBytes(parsed.RootElement,
                static _ => CanonicalJsonArrayOrdering.Sequence, CanonicalJsonNumberSemantics.ExactDecimalRational);
            Assert.Equal(Reference(text), System.Text.Encoding.UTF8.GetString(actual));
        }
    }

    [Fact]
    public void WarmIntegerSequenceDoesNotAllocatePerNumberRepresentations()
    {
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Range(-2048, 4096)));
        using var writer = new Utf8JsonWriter(Stream.Null);
        Write(writer, parsed.RootElement);
        writer.Flush();
        writer.Reset(Stream.Null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Write(writer, parsed.RootElement);
        writer.Flush();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Input and writer storage are prepared outside the boundary; no owned output is retained.
        Assert.InRange(allocated, 0, 16_384);
    }
}
