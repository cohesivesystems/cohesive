using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CanonicalJsonSequenceProfileTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("{\"b\":0,\"a\":1,\"a\":2}")]
    [InlineData("{\"\uE000\":1,\"😀\":2,\"\uFFFF\":3}")]
    [InlineData("[1.00,-0,-0.0,1e+30,1e-300,123456789012345678901234567890]")]
    [InlineData("{\"empty\":{},\"array\":[],\"text\":\"<>&\\n\\u0000\\uD83D\\uDE00\"}")]
    [InlineData("null")]
    public void LexicalProfileMatchesOrdinalStableReference(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        foreach (var relaxed in new[] { false, true })
        {
            var options = new JsonWriterOptions
            {
                Encoder = relaxed ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping : JavaScriptEncoder.Default
            };
            Assert.Equal(Write(document.RootElement, options, reference: true),
                Write(document.RootElement, options, reference: false));
        }
    }

    [Fact]
    public void RepeatedKeysRetainSourceOrderAcrossSortingAndFlushes()
    {
        var raw = "{" + string.Join(",", Enumerable.Range(0, 256).SelectMany(i =>
            new[] { $"\"z\":{i}", $"\"a\":\"{new string('x', 80)}{i}\"" })) + "}";
        LexicalProfileMatchesOrdinalStableReference(raw);
    }

    [Theory]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    [InlineData("{\"\\uD800\":1,\"a\":2}")]
    public void MalformedEscapesMatchReferenceRejection(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var expected = Assert.Throws<InvalidOperationException>(() => Write(document.RootElement, default, reference: true));
        var actual = Assert.Throws<InvalidOperationException>(() => Write(document.RootElement, default, reference: false));
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public void StrictProfileStillNormalizesNumbersAndRejectsRepeatedKeys()
    {
        using var document = JsonDocument.Parse("[1.00,-0,1e+03]");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            CanonicalJsonWriter.WriteCanonicalSequence(writer, document.RootElement, CanonicalJsonSequenceProfile.ExactDecimalRational);
        Assert.Equal("[1,0,1000]", Encoding.UTF8.GetString(stream.ToArray()));
        using var repeated = JsonDocument.Parse("{\"a\":1,\"a\":2}");
        using var rejectedWriter = new Utf8JsonWriter(Stream.Null);
        Assert.Throws<ArgumentException>(() => CanonicalJsonWriter.WriteCanonicalSequence(rejectedWriter,
            repeated.RootElement, CanonicalJsonSequenceProfile.ExactDecimalRational));
    }

    [Fact]
    public void InvalidProfileIsRejectedBeforeWriting()
    {
        using var document = JsonDocument.Parse("null");
        using var writer = new Utf8JsonWriter(Stream.Null);
        Assert.Throws<ArgumentOutOfRangeException>(() => CanonicalJsonWriter.WriteCanonicalSequence(writer,
            document.RootElement, (CanonicalJsonSequenceProfile)42));
        Assert.Equal(0, writer.BytesPending);
    }

    [Theory]
    [InlineData(false, 16_384)]
    [InlineData(true, 32_768)]
    public void ScalarHeavyHashHasBoundedWorkingAllocations(bool escaped, long ceiling)
    {
        var raw = JsonSerializer.Serialize(Enumerable.Range(0, 64).Select(i => new
        {
            text = escaped ? new string('é', 512) + "\n\"\\😀" : new string('a', 512), number = i + .125m
        }));
        for (var warm = 0; warm < 100; warm++) Hash(raw);
        var samples = new long[7];
        for (var index = 0; index < samples.Length; index++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = Hash(raw);
            samples[index] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
        }
        Array.Sort(samples);
        output.WriteLine($"Scalar hash escaped={escaped}: {samples[3]} B");
        Assert.InRange(samples[3], 0, ceiling);
    }

    static byte[] Hash(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using (var writer = new Utf8JsonWriter(stream))
            CanonicalJsonWriter.WriteCanonicalSequence(writer, document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical);
        stream.FlushFinalBlock();
        return hash.Hash!;
    }

    static byte[] Write(JsonElement element, JsonWriterOptions options, bool reference)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            if (reference) WriteReference(writer, element);
            else CanonicalJsonWriter.WriteCanonicalSequence(writer, element, CanonicalJsonSequenceProfile.PreserveLexical);
        }
        return stream.ToArray();
    }

    static void WriteReference(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteReference(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in element.EnumerateArray()) WriteReference(writer, child);
            writer.WriteEndArray();
        }
        else if (element.ValueKind == JsonValueKind.String) writer.WriteStringValue(element.GetString());
        else if (element.ValueKind == JsonValueKind.Number) writer.WriteRawValue(element.GetRawText());
        else element.WriteTo(writer);
    }
}
