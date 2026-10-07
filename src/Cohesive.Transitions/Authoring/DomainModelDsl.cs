using Cohesive.Transitions.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Entry point for building <see cref="DomainModelDefinition"/> instances.
/// </summary>
public static class DomainModelDsl
{
    /// <summary>Starts a query source directly from an entity's authoritative state graph.</summary>
    /// <typeparam name="T">Entity state type.</typeparam>
    /// <param name="author">Caller-owned query authoring session.</param>
    /// <param name="entity">Canonical entity; no replacement CLR schema is inferred.</param>
    /// <returns>A bound source node usable by filters, traversals and projections.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A conflicting graph is already imported.</exception>
    /// <exception cref="InvalidOperationException">The session already inferred a competing graph or CLR mapping fails.</exception>
    public static RelationQueryExpressionBoundNode<SourceQueryNode, T> Source<T>(
        this RelationQueryExpressionAuthoring author, DomainEntity<T> entity) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(entity);
        return author.Source(entity.QueryShape(author));
    }

    /// <summary>
    /// Builds a domain model using the fluent builder API.
    /// </summary>
    public static DomainModelDefinition Define(Action<DomainModelBuilder> configure)
    {
        var builder = new DomainModelBuilder();
        configure(obj: builder);
        return builder.Build();
    }
}