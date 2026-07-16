using System.Text.Json.Serialization;

namespace Cohesive.Relations.Realization;

/// <summary>Requested runtime occurrence-lineage observability for one relation/query interpretation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryOccurrenceProvenanceMode
{
    /// <summary>
    /// Every result row must retain all contributing source occurrences and, where applicable, its relation root.
    /// </summary>
    ExactContributors = 0,

    /// <summary>
    /// Contributor occurrence lineage is not requested independently. Rooted relation semantics may still require
    /// root occurrence correlation.
    /// </summary>
    NotRequested = 1
}

/// <summary>
/// Explicit result-observability contract applied while projecting and realizing one compiled relation/query plan.
/// </summary>
/// <remarks>
/// Runtime occurrence lineage is distinct from compiler artifact provenance. Derived artifacts must retain
/// attribution to their semantic plan and lowering decisions regardless of this contract.
/// </remarks>
public readonly record struct RelationQueryResultObservability
{
    /// <summary>Creates a result-observability contract.</summary>
    /// <param name="occurrenceProvenance">Requested contributing-occurrence lineage behavior.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="occurrenceProvenance"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public RelationQueryResultObservability(
        RelationQueryOccurrenceProvenanceMode occurrenceProvenance)
    {
        if (!Enum.IsDefined(occurrenceProvenance))
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurrenceProvenance),
                occurrenceProvenance,
                "Unsupported occurrence-provenance mode.");
        }
        OccurrenceProvenance = occurrenceProvenance;
    }

    /// <summary>
    /// Strict default requiring exact contributing occurrences and relation-root attribution where applicable.
    /// </summary>
    public static RelationQueryResultObservability ExactContributors { get; } =
        new(RelationQueryOccurrenceProvenanceMode.ExactContributors);

    /// <summary>
    /// Value-result contract that does not independently request runtime contributor occurrence lineage.
    /// </summary>
    public static RelationQueryResultObservability NotRequested { get; } =
        new(RelationQueryOccurrenceProvenanceMode.NotRequested);

    /// <summary>Requested contributing-occurrence lineage behavior.</summary>
    public RelationQueryOccurrenceProvenanceMode OccurrenceProvenance { get; }
}
