namespace Cohesive.Adapters.OpenApi;

/// <summary>
/// Options for OpenAPI document emission.
/// </summary>
public sealed record OpenApiEmitterOptions
{
    /// <summary>
    /// Generated document file name.
    /// </summary>
    public string FileName { get; init; } = "openapi.generated.json";

    /// <summary>
    /// OpenAPI document title.
    /// </summary>
    public string Title { get; init; } = "Cohesive API";

    /// <summary>
    /// OpenAPI document version.
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// Whether to emit indented JSON.
    /// </summary>
    public bool WriteIndented { get; init; } = true;
}
