using System.Text.Json.Serialization;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Convention options for auto-extracting observation metadata from records.
/// </summary>
public sealed record ObjectObservationMetadataConventionOptions
{
    /// <summary>
    /// Candidate CLR property names for observation id.
    /// </summary>
    public IReadOnlyList<string> IdPropertyNames { get; init; } = ["Id", "Key"];

    /// <summary>
    /// Candidate <see cref="JsonPropertyNameAttribute"/> values for observation id.
    /// </summary>
    public IReadOnlyList<string> IdJsonPropertyNames { get; init; } = ["id", "key"];

    /// <summary>
    /// Candidate CLR property names for observation version.
    /// </summary>
    public IReadOnlyList<string> VersionPropertyNames { get; init; } = ["Version"];

    /// <summary>
    /// Candidate <see cref="JsonPropertyNameAttribute"/> values for observation version.
    /// </summary>
    public IReadOnlyList<string> VersionJsonPropertyNames { get; init; } = ["version", "_version"];

    /// <summary>
    /// True to match metadata using <see cref="JsonPropertyNameAttribute"/> in addition to CLR property names.
    /// </summary>
    public bool UseJsonPropertyNameAttributes { get; init; } = true;
}
