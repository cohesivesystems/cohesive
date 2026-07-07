namespace Cohesive.Relations.Model;

/// <summary>
/// Options for reading observation payloads from JSON documents.
/// </summary>
public record JsonObservationReadOptions
{
    /// <summary>
    /// Property name for document key/id.
    /// </summary>
    public string IdPropertyName { get; init; } = "id";

    /// <summary>
    /// Optional property name for version.
    /// </summary>
    public string? VersionPropertyName { get; init; } = "version";

    /// <summary>
    /// Property name for nested state payload.
    /// </summary>
    public string StatePropertyName { get; init; } = "state";

    /// <summary>
    /// Treats the root document as state when true.
    /// </summary>
    public bool FlattenedState { get; init; }

    /// <summary>
    /// Ignores known metadata properties when <see cref="FlattenedState"/> is true.
    /// </summary>
    public bool IgnoreMetadataInFlattenedState { get; init; } = true;

    /// <summary>
    /// Additional state properties to ignore while mapping.
    /// </summary>
    public IReadOnlySet<string>? IgnoredStateProperties { get; init; }

    /// <summary>
    /// Explicit id override.
    /// </summary>
    public string? IdOverride { get; init; }

    /// <summary>
    /// Explicit version override.
    /// </summary>
    public long? VersionOverride { get; init; }
}