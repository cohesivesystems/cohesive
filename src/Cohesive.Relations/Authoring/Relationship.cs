namespace Cohesive.Relations.Authoring;

/// <summary>
/// Entry point for authoring canonical semantic relationships.
/// </summary>
public static class Relationship
{
    /// <summary>Starts a relationship from an explicit canonical source shape.</summary>
    /// <param name="sourceShape">Graph-qualified shape containing the reference field.</param>
    /// <returns>A builder that accepts the source reference path.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourceShape"/> is default or incomplete.</exception>
    public static RelationshipFromBuilder From(QualifiedShapeId sourceShape) => new(sourceShape);

    /// <summary>Starts a typed relationship using deterministic CLR shape qualification.</summary>
    /// <typeparam name="TSource">CLR type containing the reference member.</typeparam>
    /// <returns>A typed builder that accepts a source member selector.</returns>
    /// <exception cref="InvalidOperationException">
    /// The source CLR type's assembly does not expose a stable name.
    /// </exception>
    public static RelationshipFromBuilder<TSource> From<TSource>() where TSource : notnull =>
        new(ClrRelationshipShapeConvention.GetQualifiedShapeId<TSource>());

    /// <summary>Starts a typed relationship from an explicit canonical source shape.</summary>
    /// <typeparam name="TSource">CLR type containing the reference member.</typeparam>
    /// <param name="sourceShape">Graph-qualified shape represented by <typeparamref name="TSource"/>.</param>
    /// <returns>A typed builder that accepts a source member selector.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourceShape"/> is default or incomplete.</exception>
    public static RelationshipFromBuilder<TSource> From<TSource>(QualifiedShapeId sourceShape)
        where TSource : notnull =>
        new(sourceShape);
}
