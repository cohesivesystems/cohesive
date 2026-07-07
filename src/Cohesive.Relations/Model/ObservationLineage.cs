namespace Cohesive.Relations.Model;

/// <summary>
/// Observation-level lineage metadata.
/// </summary>
public sealed record ObservationLineage
{
    /// <summary>
    /// Empty lineage value.
    /// </summary>
    public static readonly ObservationLineage Empty = new([]);

    /// <summary>
    /// Creates lineage metadata.
    /// </summary>
    public ObservationLineage(IReadOnlyList<FieldLineage> fields)
    {
        Fields = Guard.RequireNotNull(fields);
    }

    /// <summary>
    /// Field-level lineage.
    /// </summary>
    public IReadOnlyList<FieldLineage> Fields { get; init; }
}