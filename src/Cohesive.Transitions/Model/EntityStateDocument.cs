using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Transitions.Model;

/// <summary>Portable export of an existing entity definition, including its invariants and explicit shape binding.</summary>
/// <param name="SchemaVersion">Wire version.</param><param name="Definition">Original lowered entity.</param>
public sealed record EntityStateDocument(string SchemaVersion, EntityDefinition Definition)
{
    /// <summary>Current entity inspection schema.</summary>
    public const string Schema = "cohesive-entity-state/v1";
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();
    /// <summary>Exports an original lowered entity; never an independently editable model.</summary>
    /// <param name="definition">Canonical entity definition.</param><returns>Original definition wrapped in its portable schema.</returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    public static JsonElement Export(EntityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.SerializeToElement(new EntityStateDocument(Schema, definition), Options);
    }
    /// <summary>Restores an entity for its owning domain validator.</summary>
    /// <param name="json">Original entity document.</param><returns>Typed entity with its retained shape binding.</returns>
    /// <exception cref="JsonException">Schema or definition is absent or unsupported.</exception>
    public static EntityDefinition Read(JsonElement json)
    {
        if (StrictDocumentJson.TryFindDuplicateProperty(json, "", out var duplicate)) throw new JsonException($"Duplicate entity property '{duplicate}'.");
        return json.Deserialize<EntityStateDocument>(Options) is { SchemaVersion: Schema, Definition: { } definition }
            ? definition : throw new JsonException("Invalid entity state document.");
    }
}
