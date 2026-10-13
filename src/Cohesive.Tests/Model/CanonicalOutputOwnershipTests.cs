using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CanonicalOutputOwnershipTests
{
    [Theory]
    [InlineData("\"\\uD800\"")]
    [InlineData("{\"\\uD800\":1}")]
    public void TextRejectsMalformedSurrogatesAndLaterOutputStillWorks(string json)
    {
        using var malformed = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() => CanonicalJsonWriter.GetCanonicalSequenceText(
            malformed.RootElement, CanonicalJsonSequenceProfile.PreserveLexical));
        using var valid = JsonDocument.Parse("{\"valid\":true}");
        Assert.Equal("{\"valid\":true}", CanonicalJsonWriter.GetCanonicalSequenceText(
            valid.RootElement, CanonicalJsonSequenceProfile.PreserveLexical));
    }

    [Theory]
    [InlineData(CanonicalJsonSequenceProfile.ExactDecimalRational)]
    [InlineData(CanonicalJsonSequenceProfile.PreserveLexical)]
    public void TextMatchesDirectWritingWithEncoderAndExclusions(CanonicalJsonSequenceProfile profile)
    {
        using var document = JsonDocument.Parse("{\"z\":1.2300,\"é\":\"<>&😀\",\"excluded\":3}");
        var options = new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default };
        var exclusions = new CanonicalJsonPropertyExclusions("/excluded");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
            CanonicalJsonWriter.WriteCanonicalSequence(writer, document.RootElement, profile, exclusions);
        var text = CanonicalJsonWriter.GetCanonicalSequenceText(document.RootElement, profile, options, exclusions);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(stream.ToArray()), text);
        Assert.DoesNotContain("excluded", text);
    }

    [Fact]
    public void WarmTextOwnsItsResultWithoutTemporaryPayloadAllocations()
    {
        var json = JsonSerializer.Serialize(new string('x', 65_536));
        using var document = JsonDocument.Parse(json);
        string Write() => CanonicalJsonWriter.GetCanonicalSequenceText(document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical);
        for (var i = 0; i < 20; i++) Write();
        var samples = new long[7];
        string? retained = null;
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = Write();
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            retained ??= result;
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], json.Length * 2L, json.Length * 2L + 1_024);
        Assert.Equal(json, retained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmOutputAllocatesOnlyOwnedResultAndBoundedBookkeeping(bool nodePath)
    {
        var json = JsonSerializer.Serialize(new string('x', 65_536));
        using var document = JsonDocument.Parse(json);
        var node = JsonNode.Parse(json)!;
        var options = StrictDocumentJson.CreateOptions();
        byte[] Write() => nodePath
            ? CanonicalJsonWriter.GetCanonicalSequenceBytes(node, options, CanonicalJsonNumberSemantics.ExactDecimalRational)
            : CanonicalJsonWriter.GetCanonicalSequenceBytes(document.RootElement);
        for (var i = 0; i < 20; i++) Write();
        var samples = new long[7];
        byte[]? retained = null;
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var output = Write();
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            retained ??= output;
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], json.Length, json.Length + 1_024);
        // Later rentals and disposal cannot change a caller-owned result.
        Assert.Equal(json, System.Text.Encoding.UTF8.GetString(retained!));
    }
}
