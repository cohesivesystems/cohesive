using System.Text.Json;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// JSON mapper for the prototype executable relation model.
/// </summary>
public static class RelationJsonMapper
{
    /// <summary>
    /// Converts an executable relation definition to its JSON contract.
    /// </summary>
    public static RelationJsonDocument ToJsonContract(RelationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new RelationJsonDocument(definition);
    }

    /// <summary>
    /// Converts a relation JSON contract to an executable relation definition.
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
        var contract = JsonSerializer.Deserialize<RelationJsonDocument>(source, RelationJsonSerializer.CreateOptions());
        return contract is null
            ? throw new InvalidOperationException("Failed to deserialize relation JSON document.")
            : ToDefinition(contract);
    }
}
