using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Relations.Serialization;

/// <summary>Embeds a native relation draft document in an independently configured JSON envelope.</summary>
/// <remarks>
/// Register on envelope options or apply to a property, not to the document type itself. Nested reads retain the native serializer's
/// version, duplicate-property, semantic and fingerprint validation. Writes use its canonical options,
/// independently of the containing envelope's naming and enum conventions.
/// </remarks>
public sealed class RelationDraftDocumentJsonConverter : JsonConverter<RelationDraftDocument>
{
    static readonly JsonSerializerOptions CanonicalOptions = CreateOptions();

    /// <inheritdoc />
    public override RelationDraftDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Preserve the complete nested JSON, including duplicate properties, for native admission.
        using var document = JsonDocument.ParseValue(ref reader);
        return RelationDraftJsonSerializer.Deserialize(document.RootElement.GetRawText());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RelationDraftDocument value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, CanonicalOptions);

    static JsonSerializerOptions CreateOptions()
    {
        var options = RelationDraftJsonSerializer.CreateOptions();
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
