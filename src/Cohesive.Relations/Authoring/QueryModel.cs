using Cohesive.Prelude;
using System.Collections.Immutable;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Authoring;

/// <summary>Optional instance catalog of typed queries authored through the existing query builders.</summary>
/// <remarks>Construct on one thread before sharing. Reading Definition freezes the catalog; it neither
/// compiles queries nor selects a provider. Canonical requests remain the authorities, not CLR member names.</remarks>
public abstract class QueryModel : IAuthoringModel
{
    void IAuthoringModel.Complete() => _ = Definition;

    /// <summary>Underlying builder, also usable directly without subclassing QueryModel.</summary>
    protected QueryModelBuilder Queries { get; } = new();

    /// <summary>Canonical requests in declaration order, materialized once and safe to share.</summary>
    public ImmutableArray<RelationQueryCompilationRequest> Definition => Queries.Definition;

    /// <summary>Includes an independently authored query in this model.</summary>
    /// <typeparam name="TInput">Input type.</typeparam><typeparam name="TResult">Result type.</typeparam>
    /// <param name="query">Canonical typed query.</param><returns>The same query handle.</returns>
    protected RelationQuery<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query) => Queries.Include(query);
}
