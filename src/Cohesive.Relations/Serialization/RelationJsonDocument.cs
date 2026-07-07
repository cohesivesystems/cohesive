using System.Text.Json.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Root JSON payload for relation documents.
/// </summary>
public sealed record RelationJsonDocument
{
    /// <summary>
    /// Creates a relation JSON document.
    /// </summary>
    [JsonConstructor]
    public RelationJsonDocument(RelationDefinition relation)
    {
        Relation = Guard.RequireNotNull(relation);
    }

    /// <summary>
    /// Root relation node.
    /// </summary>
    [JsonPropertyName("relation")]
    public RelationDefinition Relation { get; init; }
}
