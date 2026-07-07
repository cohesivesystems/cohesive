namespace Cohesive.Relations.Model;

/// <summary>
/// Field-level lineage details.
/// </summary>
public sealed record FieldLineage
{
    /// <summary>
    /// Creates field lineage details.
    /// </summary>
    public FieldLineage(string targetField, IReadOnlyList<LineageContribution> contributions)
    {
        TargetField = Guard.RequireNotNullOrWhiteSpace(targetField);
        Contributions = Guard.RequireNotNull(contributions);
    }

    /// <summary>
    /// Emitted target field name.
    /// </summary>
    public string TargetField { get; init; }

    /// <summary>
    /// Contributions for this field.
    /// </summary>
    public IReadOnlyList<LineageContribution> Contributions { get; init; }
}
