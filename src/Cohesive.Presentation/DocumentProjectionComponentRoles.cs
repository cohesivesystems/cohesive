namespace Cohesive.Presentation;

/// <summary>
/// Standard component roles for semantic document projections.
/// </summary>
/// <remarks>
/// These values are target-adapter binding names, not React component names.
/// A concrete frontend can interpret the same role with different component
/// implementations depending on document profile, projection coordinates, or
/// available platform widgets.
/// </remarks>
public static class DocumentProjectionComponentRoles
{
    /// <summary>
    /// Editable or read-only JSON document text surface.
    /// </summary>
    public const string JsonDocumentEditor = "cohesive.document-projection.json-document-editor";

    /// <summary>
    /// Semantic tree over the projected document structure.
    /// </summary>
    public const string SemanticStructureTree = "cohesive.document-projection.semantic-structure-tree";

    /// <summary>
    /// Tree browser for type-system projections.
    /// </summary>
    public const string TypeSystemTree = "cohesive.document-projection.type-system-tree";

    /// <summary>
    /// Node-link graph or flow projection over document semantics.
    /// </summary>
    public const string GraphFlow = "cohesive.document-projection.graph-flow";
}
