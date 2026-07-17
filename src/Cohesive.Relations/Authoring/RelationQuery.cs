namespace Cohesive.Relations.Authoring;

/// <summary>Entry point for structural and typed expression authoring of canonical relation/query definitions.</summary>
public static class RelationQuery
{
    /// <summary>
    /// Creates a fresh structural authoring core that can be used directly by developers or as the
    /// lowering target of a higher-level frontend.
    /// </summary>
    /// <returns>An empty structural authoring core.</returns>
    public static RelationQueryAuthoringCore Structural() => new();

    /// <summary>
    /// Creates a typed C# expression-authoring session that lowers exclusively through the structural core.
    /// </summary>
    /// <param name="clr">
    /// Optional deterministic CLR shape/member context. A default context is created when omitted.
    /// </param>
    /// <returns>An empty expression-authoring session.</returns>
    public static RelationQueryExpressionAuthoring Expression(
        RelationQueryClrAuthoringContext? clr = null) =>
        new(clr);
}
