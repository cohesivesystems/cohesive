using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Transitions.Model;
using YamlDotNet.Serialization;

namespace Cohesive.Host.Configuration;

/// <summary>
/// Supported text formats for external domain-model definitions.
/// </summary>
public enum DomainModelExternalDslFormat
{
    /// <summary>Represents the auto option.</summary>
    Auto = 0,
    /// <summary>Represents the json option.</summary>
    Json = 1,
    /// <summary>Represents the yaml option.</summary>
    Yaml = 2
}

/// <summary>
/// Parses and emits <see cref="DomainModelDefinition"/> definitions as JSON or YAML.
/// </summary>
public static class DomainModelExternalDsl
{
    static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>
    /// Parses a domain model from external DSL text.
    /// </summary>
    public static DomainModelDefinition Parse(string text, DomainModelExternalDslFormat format = DomainModelExternalDslFormat.Auto)
    {
        var source = Guard.RequireNotNullOrWhiteSpace(value: text);
        var resolvedFormat = ResolveFormat(text: source, requestedFormat: format);
        return resolvedFormat switch
        {
            DomainModelExternalDslFormat.Json => ParseJson(json: source),
            DomainModelExternalDslFormat.Yaml => ParseYaml(yaml: source),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported external DSL format.")
        };
    }

    /// <summary>
    /// Parses a domain model from JSON text.
    /// </summary>
    public static DomainModelDefinition ParseJson(string json)
    {
        var source = Guard.RequireNotNullOrWhiteSpace(value: json);
        var root = JsonNode.Parse(source) ?? throw new InvalidOperationException("Failed to parse domain model JSON.");
        NormalizeLegacyTransitionFieldIdentities(root);
        var model = root.Deserialize<DomainModelDefinition>(options: JsonOptions);
        return model ?? throw new InvalidOperationException("Failed to deserialize domain model from JSON.");
    }

    /// <summary>
    /// Parses a domain model from YAML text.
    /// </summary>
    public static DomainModelDefinition ParseYaml(string yaml)
    {
        var source = Guard.RequireNotNullOrWhiteSpace(value: yaml);
        var deserializer = new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build();
        var yamlObject = deserializer.Deserialize<object?>(input: source);
        var jsonNode = ToJsonNode(value: yamlObject);
        return ParseJson(json: jsonNode?.ToJsonString() ?? "null");
    }

    /// <summary>
    /// Serializes a domain model to JSON text.
    /// </summary>
    public static string ToJson(DomainModelDefinition model, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        var options = new JsonSerializerOptions(JsonOptions)
        {
            WriteIndented = indented
        };
        return JsonSerializer.Serialize(value: model, options: options);
    }

    /// <summary>
    /// Serializes a domain model to YAML text.
    /// </summary>
    public static string ToYaml(DomainModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var json = ToJson(model: model, indented: false);
        var root = JsonNode.Parse(json: json);
        var serializer = new SerializerBuilder()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        return serializer.Serialize(ToYamlObject(node: root));
    }

    static DomainModelExternalDslFormat ResolveFormat(string text, DomainModelExternalDslFormat requestedFormat)
    {
        if (requestedFormat != DomainModelExternalDslFormat.Auto)
            return requestedFormat;

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            return DomainModelExternalDslFormat.Json;

        return DomainModelExternalDslFormat.Yaml;
    }

    static JsonNode? ToJsonNode(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonNode node)
            return node.DeepClone();

        if (value is string text)
            return JsonValue.Create(value: text);

        if (value is IDictionary dictionary)
        {
            JsonObject obj = [];
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = Convert.ToString(entry.Key, provider: System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new InvalidOperationException("YAML dictionary keys must be non-empty strings.");
                }

                obj[key] = ToJsonNode(entry.Value);
            }

            return obj;
        }

        if (value is IEnumerable enumerable)
        {
            JsonArray array = [];
            foreach (var item in enumerable)
                array.Add(item: ToJsonNode(item));
            return array;
        }

        return JsonSerializer.SerializeToNode(value: value, options: JsonOptions);
    }

    static object? ToYamlObject(JsonNode? node)
    {
        if (node is null)
            return null;

        using var document = JsonDocument.Parse(json: node.ToJsonString());
        return ToYamlObject(element: document.RootElement);
    }

    static object? ToYamlObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    keySelector: x => x.Name,
                    elementSelector: x => ToYamlObject(element: x.Value),
                    comparer: StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(selector: ToYamlObject)
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => ToYamlNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    static object ToYamlNumber(JsonElement element)
    {
        if (element.TryGetInt32(value: out var int32Value))
            return int32Value;

        if (element.TryGetInt64(value: out var int64Value))
            return int64Value;

        if (element.TryGetDecimal(value: out var decimalValue))
            return decimalValue;

        return element.GetDouble();
    }

    static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(item: new JsonStringEnumConverter());
        return options;
    }

    static void NormalizeLegacyTransitionFieldIdentities(JsonNode root)
    {
        if (root is not JsonObject obj || obj["entities"] is not JsonArray entities)
            return;

        foreach (var entityNode in entities)
        {
            if (entityNode is not JsonObject entity || entity["transitions"] is not JsonArray transitions)
                continue;

            foreach (var transitionNode in transitions)
            {
                if (transitionNode is not JsonObject transition)
                    continue;

                NormalizeFieldIdentityArray(transition, propertyName: "readSet");
                NormalizeFieldIdentityArray(transition, propertyName: "writeSet");

                if (transition["updates"] is not JsonArray updates)
                    continue;

                foreach (var updateNode in updates)
                {
                    if (updateNode is JsonObject update)
                        NormalizeFieldIdentityProperty(update, propertyName: "field");
                }
            }
        }
    }

    static void NormalizeFieldIdentityArray(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is not JsonArray array)
            return;

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject identityObject && TryReadLegacyFieldIdentity(identityObject, out var value))
                array[i] = value;
        }
    }

    static void NormalizeFieldIdentityProperty(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is JsonObject identityObject && TryReadLegacyFieldIdentity(identityObject, out var value))
            obj[propertyName] = value;
    }

    static bool TryReadLegacyFieldIdentity(JsonObject obj, out string value)
    {
        value = string.Empty;
        if (obj["value"] is not JsonValue raw || !raw.TryGetValue<string>(out var stringValue) || string.IsNullOrWhiteSpace(stringValue))
            return false;

        value = stringValue;
        return true;
    }
}
