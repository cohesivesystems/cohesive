namespace Cohesive.Relations.Queries;

/// <summary>
/// Root observation query specification for a composed relations query.
/// </summary>
/// <param name="Source">Query source.</param>
/// <oaram name="Request">Query request.</oaram>
public sealed record RootQuery(
    QuerySource Source,
    EntityQuery Request
);

/// <summary>
/// Query plan consisting of a root observation query, optional joins, an optional post-join predicate, and a result projection.
/// </summary>
/// <param name="RootQuery">Root observation query.</param>
/// <param name="Joins">Optional join specifications.</param>
/// <param name="ResultPredicate">Optional post-join predicate.</param>
/// <param name="Projector">Projection function applied to the result of the query.</param>
/// <typeparam name="TResult">Projection result type.</typeparam>
public sealed record QueryPlan<TResult>(
    RootQuery RootQuery,
    IReadOnlyList<JoinSpec> Joins,
    EntityPredicate? ResultPredicate,
    Func<JoinContext, TResult> Projector
);
