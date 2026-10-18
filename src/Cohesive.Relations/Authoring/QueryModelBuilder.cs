using System.Collections.Immutable;
using Cohesive.Prelude;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>Ordered query registration and optional session-owned authoring, independent of providers.</summary>
/// <remarks>Author before sharing. Definition freezes registration; callbacks run only during authoring, never during query execution.</remarks>
public sealed class QueryModelBuilder
{
    bool authoring;
    readonly List<RelationQueryCompilationRequest> queries = [];
    readonly HashSet<QueryId> ids = [];
    readonly AuthoringState<ImmutableArray<RelationQueryCompilationRequest>> definition;

    /// <summary>Starts an independently owned ordered query declaration.</summary>
    public QueryModelBuilder() => definition = new(() => [.. queries]);

    /// <summary>Starts authoring queries with one named invocation input.</summary>
    /// <typeparam name="TInput">Input type.</typeparam><param name="parameterName">Canonical parameter name.</param>
    /// <returns>A reusable recipe that creates an isolated session for each query.</returns>
    /// <exception cref="ArgumentException">The parameter name is empty or whitespace.</exception>
    public QueryModelInputBuilder<TInput> WithInput<TInput>(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        return new(this, parameterName);
    }

    internal RelationQuery<TInput, TResult> Author<TInput, TResult>(QueryId id, Func<RelationQuery<TInput, TResult>> build) =>
        definition.Edit(() =>
        {
            RequireIdle();
            if (ids.Contains(id)) throw new ArgumentException("A query with this semantic ID is already declared.", nameof(id));
            RelationQuery<TInput, TResult> query;
            authoring = true;
            try { query = build(); }
            finally { authoring = false; }
            return Include(query);
        });

    /// <summary>Canonical requests in declaration order, materialized once and safe to share.</summary>
    public ImmutableArray<RelationQueryCompilationRequest> Definition { get { RequireIdle(); return definition.Value; } }

    /// <summary>Includes a typed query without changing its portable definition or result assembly.</summary>
    /// <typeparam name="TInput">Invocation input.</typeparam>
    /// <typeparam name="TResult">Declared result.</typeparam>
    /// <param name="query">Completed query from any supported authoring path.</param>
    /// <returns>The same typed handle, suitable for assignment to an instance property.</returns>
    /// <exception cref="ArgumentNullException">Query is null.</exception>
    /// <exception cref="ArgumentException">A query with the same semantic ID was already contributed.</exception>
    /// <exception cref="InvalidOperationException">The catalog has already been frozen.</exception>
    public RelationQuery<TInput, TResult> Include<TInput, TResult>(RelationQuery<TInput, TResult> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return definition.Edit(() =>
        {
            RequireIdle();
            var request = query.CompilationRequest;
            if (request.DefinitionDocument.Definition is not QueryDefinition queryDefinition)
                throw new ArgumentException("A query catalog requires a canonical query definition.", nameof(query));
            if (!ids.Add(queryDefinition.Id))
                throw new ArgumentException("A query with this semantic ID is already declared.", nameof(query));
            queries.Add(request);
            return query;
        });
    }
    void RequireIdle()
    {
        if (authoring) throw new InvalidOperationException("Complete the current query recipe before modifying or freezing the model.");
    }
}
