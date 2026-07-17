namespace Cohesive.Relations.Authoring;

/// <summary>Entry point for structural authoring of canonical relation/query definitions.</summary>
public static class RelationQuery
{
    /// <summary>
    /// Creates a fresh structural authoring core that can be used directly by developers or as the
    /// lowering target of a higher-level frontend.
    /// </summary>
    /// <returns>An empty structural authoring core.</returns>
    public static RelationQueryAuthoringCore Structural() => new();
}
