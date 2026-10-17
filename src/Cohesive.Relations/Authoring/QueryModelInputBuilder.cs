using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>Authors and registers typed queries with an automatically owned session and input.</summary>
/// <typeparam name="TInput">Invocation input type.</typeparam>
public sealed class QueryModelInputBuilder<TInput>
{
    readonly QueryModelBuilder model;
    readonly string parameterName;
    internal QueryModelInputBuilder(QueryModelBuilder model, string parameterName)
    { this.model = model; this.parameterName = parameterName; }

    /// <summary>Authors an array query and registers it; no compilation or IO occurs.</summary>
    /// <typeparam name="TNode">Result row node.</typeparam><typeparam name="TRow">Result row type.</typeparam>
    /// <param name="id">Stable semantic query ID.</param>
    /// <param name="define">Builds the result rows using the supplied session and parameter.</param>
    /// <param name="name">Optional display name, defaulting to the exact semantic ID.</param>
    /// <returns>The registered typed query.</returns>
    /// <exception cref="ArgumentNullException">Callback is null.</exception>
    /// <exception cref="ArgumentException">Identity, input or result ownership is invalid, or the ID is already registered.</exception>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public RelationQuery<TInput, TRow[]> Array<TNode, TRow>(string id,
        Func<RelationQueryExpressionAuthoring, RelationQueryExpressionParameter<TInput>, RelationQueryExpressionBoundNode<TNode, TRow>> define,
        string? name = null) where TNode : LogicalQueryNode where TRow : notnull
    {
        ArgumentNullException.ThrowIfNull(define);
        var identity = new QueryId(id);
        var displayName = new QueryName(name ?? id);
        return model.Author(identity, () =>
        {
            var session = RelationQuery.Expression();
            var parameter = session.Parameter<TInput>(parameterName);
            var rows = define(session, parameter);
            ArgumentNullException.ThrowIfNull(rows);
            return session.BuildArrayQuery(identity, displayName, rows, parameter);
        });
    }

    /// <summary>Authors and registers a nested single-parent query, preserving keyed result assembly.</summary>
    /// <typeparam name="TResult">Parent result type.</typeparam>
    /// <param name="id">Stable semantic query ID.</param>
    /// <param name="define">Builds mappings using the supplied session, input and typed result source; cardinality and finalization are owned by this method.</param>
    /// <param name="name">Optional display name, defaulting to the exact semantic ID.</param>
    /// <returns>The registered query, returning null when no parent exists.</returns>
    /// <exception cref="ArgumentNullException">Callback is null.</exception>
    /// <exception cref="ArgumentException">Identity, input, mapping or session ownership is invalid, or the ID is already registered.</exception>
    /// <exception cref="InvalidOperationException">The model or result builder is frozen.</exception>
    public RelationQuery<TInput, TResult?> SingleOrDefault<TResult>(string id,
        Func<RelationQueryExpressionAuthoring, RelationQueryExpressionParameter<TInput>, NestedQueryResultSource<TResult>, NestedQueryResultBuilder<TResult>> define,
        string? name = null) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(define);
        var identity = new QueryId(id);
        var displayName = new QueryName(name ?? id);
        return model.Author(identity, () =>
        {
            var session = RelationQuery.Expression();
            var parameter = session.Parameter<TInput>(parameterName);
            var result = define(session, parameter, session.SingleOrDefault<TResult>());
            ArgumentNullException.ThrowIfNull(result);
            return result.Build(identity, displayName, parameter);
        });
    }
}
