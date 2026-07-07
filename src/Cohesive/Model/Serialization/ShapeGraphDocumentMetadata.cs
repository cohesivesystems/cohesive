using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Metadata carried by a serialized shape graph document.
/// </summary>
public sealed record ShapeGraphDocumentMetadata
{
    /// <summary>
    /// Empty shape graph document metadata.
    /// </summary>
    public static ShapeGraphDocumentMetadata Empty { get; } = new();

    /// <summary>
    /// Creates shape graph document metadata.
    /// </summary>
    [JsonConstructor]
    public ShapeGraphDocumentMetadata(
        DocumentOrigin origin = DocumentOrigin.Unknown,
        string? name = null,
        string? description = null,
        string? sourceUri = null,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? updatedAtUtc = null,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        Origin = origin;
        Name = name.TrimmedEmptyOrWhiteSpaceAs();
        Description = description.TrimmedEmptyOrWhiteSpaceAs();
        SourceUri = sourceUri.TrimmedEmptyOrWhiteSpaceAs();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Annotations = AnnotationMap.Normalize(annotations);
    }

    /// <summary>
    /// Coarse origin category for the document.
    /// </summary>
    public DocumentOrigin Origin { get; init; }

    /// <summary>
    /// Optional human-facing document name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Optional human-facing document description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional source URI or path from which this document was derived.
    /// </summary>
    public string? SourceUri { get; init; }

    /// <summary>
    /// Optional creation timestamp.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; init; }

    /// <summary>
    /// Optional last update timestamp.
    /// </summary>
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>
    /// Structured metadata annotations.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }
}
