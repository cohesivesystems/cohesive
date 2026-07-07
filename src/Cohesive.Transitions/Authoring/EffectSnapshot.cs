namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Snapshot token projected from selected fields for concurrency checks.
/// </summary>
public sealed record EffectSnapshot(string Token, IReadOnlyList<string> FieldNames)
{
    /// <summary>
    /// Creates a snapshot token projection.
    /// </summary>
    public EffectSnapshot(string token, IEnumerable<string> fieldNames) : this(
        Guard.RequireNotNullOrWhiteSpace(token),
        [.. Guard.RequireNotNull(fieldNames).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)])
    {
    }
}