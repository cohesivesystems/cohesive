using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>Optional instance catalog of typed queries authored through the existing query builders.</summary>
/// <remarks>Construct on one thread before sharing. Reading Definition freezes the catalog; it neither
/// compiles queries nor selects a provider. Canonical requests remain the authorities, not CLR member names.</remarks>
public abstract class QueryModel
{
    readonly Dictionary<QueryId, RelationQueryCompilationRequest> queries = [];
    readonly object gate = new();
    ImmutableArray<RelationQueryCompilationRequest> definition;

    /// <summary>Canonical requests in declaration order, materialized once and safe to share.</summary>
    public ImmutableArray<RelationQueryCompilationRequest> Definition
    {
        get
        {
            lock (gate)
            {
                if (definition.IsDefault) definition = [.. queries.Values];
                return definition;
            }
        }
    }

    /// <summary>Includes a typed query without changing its portable definition or result assembly.</summary>
    /// <typeparam name="TInput">Invocation input.</typeparam>
    /// <typeparam name="TResult">Declared result.</typeparam>
    /// <param name="query">Completed query from any supported authoring path.</param>
    /// <returns>The same typed handle, suitable for assignment to an instance property.</returns>
    /// <exception cref="ArgumentNullException">Query is null.</exception>
    /// <exception cref="ArgumentException">A query with the same semantic ID was already contributed.</exception>
    /// <exception cref="InvalidOperationException">The catalog has already been frozen.</exception>
    protected RelationQuery<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (gate)
        {
            if (!definition.IsDefault) throw new InvalidOperationException("The query model is frozen.");
            var request = query.CompilationRequest;
            if (request.DefinitionDocument.Definition is not QueryDefinition queryDefinition)
                throw new ArgumentException("A query catalog requires a canonical query definition.", nameof(query));
            if (!queries.TryAdd(queryDefinition.Id, request))
                throw new ArgumentException("A query with this semantic ID is already declared.", nameof(query));
            return query;
        }
    }
}
