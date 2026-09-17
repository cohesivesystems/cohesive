using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Xunit;

namespace Cohesive.Tests.Model;

public sealed class WebJsonPropertyConverterTests
{
    static readonly JsonSerializerOptions HostOptions = new()
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null
    };

    [Fact]
    public void Write_PreservesHostAndNestedPropertyNamingContracts()
    {
        HostEnvelope envelope = new(
            EnvelopeName: "outer",
            Document: new(DocumentName: "inner"));

        var json = JsonSerializer.SerializeToElement(envelope, HostOptions);

        Assert.Equal("outer", json.GetProperty(nameof(HostEnvelope.EnvelopeName)).GetString());
        var document = json.GetProperty(nameof(HostEnvelope.Document));
        Assert.Equal("inner", document.GetProperty("documentName").GetString());
        Assert.False(document.TryGetProperty(nameof(WebDocument.DocumentName), out _));
    }

    [Fact]
    public void Read_UsesNestedWebContractWithoutChangingHostContract()
    {
        const string json = """
            {
              "EnvelopeName": "outer",
              "Document": {
                "documentName": "inner"
              }
            }
            """;

        var envelope = JsonSerializer.Deserialize<HostEnvelope>(json, HostOptions);

        Assert.NotNull(envelope);
        Assert.Equal("outer", envelope.EnvelopeName);
        Assert.Equal("inner", envelope.Document.DocumentName);
    }

    sealed record HostEnvelope(
        string EnvelopeName,
        [property: JsonConverter(typeof(WebJsonPropertyConverter<WebDocument>))]
        WebDocument Document);

    sealed record WebDocument(string DocumentName);
}
