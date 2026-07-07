namespace Cohesive.Presentation;

/// <summary>
/// Conventional response field names copied onto prompt document preview resources when
/// a preview endpoint returns a raw response instead of an explicit resource envelope.
/// </summary>
public static class ConventionalPreviewResourceFields
{
    /// <summary>
    /// Timestamp when the source resource was archived.
    /// </summary>
    public const string ArchivedAtUtc = "ArchivedAtUtc";

    /// <summary>
    /// Optimistic concurrency token for the source resource.
    /// </summary>
    public const string ConcurrencyToken = "ConcurrencyToken";

    /// <summary>
    /// Timestamp when the source resource was created.
    /// </summary>
    public const string CreatedAtUtc = "CreatedAtUtc";

    /// <summary>
    /// Diagnostics emitted while producing the preview.
    /// </summary>
    public const string Diagnostics = "Diagnostics";

    /// <summary>
    /// Entity observation or persistence version.
    /// </summary>
    public const string EntityVersion = "EntityVersion";

    /// <summary>
    /// Source or provenance category for the previewed resource.
    /// </summary>
    public const string Origin = "Origin";

    /// <summary>
    /// Timestamp when the source resource was last updated.
    /// </summary>
    public const string UpdatedAtUtc = "UpdatedAtUtc";

    /// <summary>
    /// Domain or document version value.
    /// </summary>
    public const string Version = "Version";
}
