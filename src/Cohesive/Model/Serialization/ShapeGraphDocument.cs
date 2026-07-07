using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Portable JSON document envelope for a <see cref="ShapeGraph"/>.
/// </summary>
public sealed record ShapeGraphDocument
{
    /// <summary>
    /// Current shape graph document schema version.
    /// </summary>
    public const string CurrentSchemaVersion = "shape-graph/v1";

    /// <summary>
    /// Creates a portable shape graph document.
    /// </summary>
    [JsonConstructor]
    public ShapeGraphDocument(
        string schemaVersion,
        ShapeGraph graph,
        ShapeGraphDocumentMetadata? metadata = null
        )
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        Graph = Guard.RequireNotNull(graph);
        Metadata = metadata ?? ShapeGraphDocumentMetadata.Empty;
    }

    /// <summary>
    /// Portable document schema version.
    /// </summary>
    public string SchemaVersion { get; init; }

    /// <summary>
    /// Document metadata.
    /// </summary>
    public ShapeGraphDocumentMetadata Metadata { get; init; }

    /// <summary>
    /// Shape graph payload.
    /// </summary>
    public ShapeGraph Graph { get; init; }

    /// <summary>
    /// Wraps a shape graph in a portable document envelope.
    /// </summary>
    public static ShapeGraphDocument FromGraph(ShapeGraph graph, ShapeGraphDocumentMetadata? metadata = null) =>
        new(schemaVersion: CurrentSchemaVersion, graph, metadata);
}
