namespace Cohesive.AI.Semantics;

/// <summary>
/// Resolves stable ontology concept identifiers to preferred display labels.
/// </summary>
public interface IConceptLabelResolver
{
    /// <summary>
    /// Resolves one concept identifier to its preferred label, when available.
    /// </summary>
    /// <param name="conceptId">Concept identifier to resolve.</param>
    /// <returns>The preferred concept label, or <see langword="null"/> when no label is known.</returns>
    string? ResolveLabel(string conceptId);
}
