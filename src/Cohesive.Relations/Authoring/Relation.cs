namespace Cohesive.Relations.Authoring;

/// <summary>
/// Typed relation DSL entry point.
/// </summary>
/// <typeparam name="TTarget">Target CLR shape.</typeparam>
public static class Relation<TTarget>
{
    /// <summary>
    /// Starts a typed relation from a source CLR shape.
    /// </summary>
    public static RelationFromBuilder<TSource, TTarget> From<TSource>()
        => new(new(typeof(TSource).Name), new(typeof(TTarget).Name));

    /// <summary>
    /// Starts a typed relation with an explicit source schema id.
    /// </summary>
    public static RelationFromBuilder<TSource, TTarget> From<TSource>(string sourceShapeId)
        => new(new(sourceShapeId), new(typeof(TTarget).Name));
}