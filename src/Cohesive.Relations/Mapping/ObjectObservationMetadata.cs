using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Optional metadata overrides when mapping an object into an observed shape.
/// </summary>
public sealed record ObjectObservationMetadata
{
    /// <summary>
    /// Explicit observation id override. Falls back to mapper conventions when null.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Explicit version override. Falls back to mapper conventions when null.
    /// </summary>
    public long? Version { get; init; }

    /// <summary>
    /// Explicit lineage override.
    /// </summary>
    public ObservationLineage? Lineage { get; init; }
}
