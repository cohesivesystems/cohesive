using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CanonicalOrderedProjectionTests
{
    static CanonicalJsonArrayOrdering Ordering(CanonicalJsonArrayPath path) => path.Value switch
    {
        "/sets" => CanonicalJsonArrayOrdering.ObjectSet("id"),
        "/sets/*/tags" => CanonicalJsonArrayOrdering.StringSet,
        _ => CanonicalJsonArrayOrdering.Sequence
    };

    [Theory]
    [InlineData(CanonicalJsonNumberSemantics.PortableObservation)]
    [InlineData(CanonicalJsonNumberSemantics.ExactDecimalRational)]
    public void OrderedProjectedHashMatchesFormerNodeProjectionWithVersionPrefix(CanonicalJsonNumberSemantics numbers)
    {
        const string json = "{\"id\":\"lifecycle\",\"excluded\":{\"bad\":\"\\uD800\"},\"text\":\"é<>&😀\",\"sets\":[{\"id\":\"b\",\"tags\":[\"z\",\"a\"],\"number\":1.2300},{\"id\":\"a\",\"tags\":[\"c\",\"b\"]}]}";
        using var document = JsonDocument.Parse(json);
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("id"));
        Assert.True(node.Remove("excluded"));
        var options = StrictDocumentJson.CreateOptions();
        var bytes = CanonicalJsonWriter.GetCanonicalBytes(node, options, Ordering, numbers);
        var exclusions = new CanonicalJsonPropertyExclusions("/id", "/excluded");
        Assert.Equal(bytes, CanonicalJsonWriter.GetCanonicalBytes(document.RootElement, Ordering, numbers, exclusions));
        byte[] prefix = "version\0"u8.ToArray();
        var actual = CanonicalJsonWriter.ComputeCanonicalSequenceSha256(document.RootElement, Ordering,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }, numbers, exclusions, prefix);
        Assert.Equal(SHA256.HashData(prefix.Concat(bytes).ToArray()), actual);
    }

    [Fact]
    public void ExplicitEncoderChoiceMatchesDirectOutput()
    {
        using var document = JsonDocument.Parse("{\"text\":\"é<>&😀\"}");
        foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var options = new JsonWriterOptions { Encoder = encoder };
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, options))
                CanonicalJsonWriter.WriteCanonicalSequence(writer, document.RootElement, CanonicalJsonSequenceProfile.ExactDecimalRational);
            Assert.Equal(SHA256.HashData(stream.ToArray()), CanonicalJsonWriter.ComputeCanonicalSequenceSha256(
                document.RootElement, Ordering, options, CanonicalJsonNumberSemantics.ExactDecimalRational));
        }
    }
}
