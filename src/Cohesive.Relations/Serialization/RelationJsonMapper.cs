using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// JSON mapper for canonical relation IR.
/// </summary>
public static class RelationJsonMapper
{
    /// <summary>
    /// Converts IR to relation JSON contract.
    /// </summary>
    public static RelationJsonDocument ToJsonContract(RelationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new RelationJsonDocument(definition);
    }

    /// <summary>
    /// Converts relation JSON contract to IR.
    /// </summary>
    public static RelationDefinition ToDefinition(RelationJsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Relation;
    }

    /// <summary>
    /// Serializes IR to JSON text.
    /// </summary>
    public static string ToJson(RelationDefinition definition, bool indented = true)
    {
        var contract = ToJsonContract(definition);
        var options = RelationJsonSerializer.CreateOptions();
        options.WriteIndented = indented;
        return JsonSerializer.Serialize(contract, options);
    }

    /// <summary>
    /// Parses JSON text to IR.
    /// </summary>
    public static RelationDefinition ParseJson(string json)
    {
        var source = Guard.RequireNotNullOrWhiteSpace(json);
        var root = JsonNode.Parse(source) ?? throw new InvalidOperationException("Failed to parse relation JSON.");
        NormalizeLegacyAssignmentTargetFields(root);
        var contract = root.Deserialize<RelationJsonDocument>(RelationJsonSerializer.CreateOptions());
        return contract is null
            ? throw new InvalidOperationException("Failed to deserialize relation JSON document.")
            : ToDefinition(contract);
    }

    static void NormalizeLegacyAssignmentTargetFields(JsonNode root)
    {
        if (root is not JsonObject obj
            || obj["relation"] is not JsonObject relation
            || relation["mappings"] is not JsonArray mappings)
        {
            return;
        }

        foreach (var mappingNode in mappings)
        {
            if (mappingNode is not JsonObject mapping || mapping["assignments"] is not JsonArray assignments)
                continue;

            foreach (var assignmentNode in assignments)
            {
                if (assignmentNode is not JsonObject assignment)
                    continue;

                if (assignment["targetField"] is JsonObject fieldObject
                    && fieldObject["value"] is JsonValue raw
                    && raw.TryGetValue<string>(out var fieldIdentity)
                    && !string.IsNullOrWhiteSpace(fieldIdentity))
                {
                    assignment["targetField"] = fieldIdentity;
                }
            }
        }
    }
}
