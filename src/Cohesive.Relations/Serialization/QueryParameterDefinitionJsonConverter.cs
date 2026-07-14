using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Reads the current explicit query-parameter default contract and its legacy
/// <c>relation-query/v1</c> representation.
/// </summary>
/// <remarks>
/// Legacy parameter JSON did not carry <c>defaultKind</c>. A concrete legacy
/// <c>defaultValue</c> is interpreted as <see cref="QueryParameterDefaultKind.Value"/>;
/// an omitted or JSON-null value is interpreted as <see cref="QueryParameterDefaultKind.None"/>
/// because the legacy representation could not distinguish no default from an explicit null default.
/// Writers always emit the discriminator.
/// </remarks>
sealed class QueryParameterDefinitionJsonConverter : JsonConverter<QueryParameterDefinition>
{
    /// <inheritdoc />
    public override QueryParameterDefinition Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A query parameter definition must be a JSON object.");

        QueryParameterId id = default;
        TypeRef? type = null;
        var presence = FieldPresence.Required;
        var defaultKind = QueryParameterDefaultKind.None;
        ObservationValue? defaultValue = null;
        var hasId = false;
        var hasType = false;
        var hasPresence = false;
        var hasDefaultKind = false;
        var hasDefaultValue = false;
        var ended = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                ended = true;
                break;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("A query parameter definition contains an invalid JSON token.");

            var property = reader.GetString();
            if (!reader.Read())
                throw new JsonException("A query parameter definition ended before its property value.");

            switch (property)
            {
                case "id" when hasId:
                case "type" when hasType:
                case "presence" when hasPresence:
                case "defaultKind" when hasDefaultKind:
                case "defaultValue" when hasDefaultValue:
                    throw new JsonException($"A query parameter definition contains duplicate property '{property}'.");
                case "id":
                    hasId = true;
                    id = JsonSerializer.Deserialize<QueryParameterId>(ref reader, options);
                    break;
                case "type":
                    hasType = true;
                    type = JsonSerializer.Deserialize<TypeRef>(ref reader, options);
                    break;
                case "presence":
                    hasPresence = true;
                    presence = JsonSerializer.Deserialize<FieldPresence>(ref reader, options);
                    break;
                case "defaultKind":
                    hasDefaultKind = true;
                    defaultKind = JsonSerializer.Deserialize<QueryParameterDefaultKind>(ref reader, options);
                    break;
                case "defaultValue":
                    hasDefaultValue = true;
                    defaultValue = JsonSerializer.Deserialize<ObservationValue?>(ref reader, options);
                    break;
                default:
                    throw new JsonException($"Unknown query parameter definition property '{property}'.");
            }
        }

        if (!ended)
            throw new JsonException("A query parameter definition JSON object was not terminated.");

        if (!hasDefaultKind)
        {
            defaultKind = defaultValue is { Kind: not ObservationValueKind.Undefined }
                ? QueryParameterDefaultKind.Value
                : QueryParameterDefaultKind.None;
        }

        try
        {
            return new(id, type!, presence, defaultKind, defaultValue);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The query parameter definition is invalid.", exception);
        }
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        QueryParameterDefinition value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            _ = new QueryParameterDefinition(
                value.Id,
                value.Type,
                value.Presence,
                value.DefaultKind,
                value.DefaultValue);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The query parameter definition is invalid.", exception);
        }

        writer.WriteStartObject();
        writer.WritePropertyName("id");
        JsonSerializer.Serialize(writer, value.Id, options);
        writer.WritePropertyName("type");
        JsonSerializer.Serialize(writer, value.Type, options);
        writer.WritePropertyName("presence");
        JsonSerializer.Serialize(writer, value.Presence, options);
        writer.WritePropertyName("defaultKind");
        JsonSerializer.Serialize(writer, value.DefaultKind, options);
        writer.WritePropertyName("defaultValue");
        JsonSerializer.Serialize(writer, value.DefaultValue, options);
        writer.WriteEndObject();
    }
}
