using System.Linq.Expressions;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Queries;

/// <summary>
/// Builder for structured observation queries with optional joins and post-join filtering.
/// </summary>
/// <param name="rootQuery">Optional root observation query used when roots are loaded from a repository.</param>
/// <param name="rootPredicate">Optional predicate applied to explicitly supplied root observations before joins are evaluated.</param>
/// <param name="roots">Optional explicit root observations used instead of a repository-backed root query.</param>
public sealed class QueryBuilder(
    RootQuery? rootQuery = null, 
    EntityPredicate? rootPredicate = null,
    IReadOnlyList<Observation>? roots = null
    )
{
    readonly List<JoinSpec> joins = [];
    EntityPredicate? resultPredicate;

    /// <summary>
    /// Adds a one-to-one join resolved from a root observation field containing the joined id.
    /// </summary>
    /// <param name="alias">The alias used to reference the joined observation in later joins and the final projection.</param>
    /// <param name="source">The source from which joined observations are loaded.</param>
    /// <param name="rootKeyField">The root observation field containing the joined observation id.</param>
    /// <param name="options">Optional field selection applied when loading joined observations.</param>
    /// <param name="sourcePredicate">Optional predicate applied to joined observations.</param>
    /// <returns>The same builder so additional joins, filters, or projection can be configured.</returns>
    public QueryBuilder JoinOne(
        string alias,
        QuerySource source,
        string rootKeyField,
        FieldSelection? options = null,
        EntityPredicate? sourcePredicate = null
        )
    {
        ValidateAlias(alias);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKeyField);
        joins.Add(new OneJoinSpec(alias, source, rootKeyField, options, sourcePredicate));
        return this;
    }

    /// <summary>
    /// Adds a one-to-one join resolved from a root CLR selector at the authoring boundary.
    /// </summary>
    /// <param name="alias">The alias used to reference the joined observation in later joins and the final projection.</param>
    /// <param name="source">The source from which joined observations are loaded.</param>
    /// <param name="rootKeySelector">Selects the root member containing the joined observation id.</param>
    /// <param name="options">Optional field selection applied when loading joined observations.</param>
    /// <param name="sourcePredicate">Optional predicate applied to joined observations.</param>
    /// <typeparam name="TRoot">The CLR type used at the authoring boundary for root observations.</typeparam>
    /// <typeparam name="TKey">The type of the root key member.</typeparam>
    /// <returns>The same builder so additional joins, filters, or projection can be configured.</returns>
    public QueryBuilder JoinOne<TRoot, TKey>(
        string alias,
        QuerySource source,
        Expression<Func<TRoot, TKey>> rootKeySelector,
        FieldSelection? options = null,
        EntityPredicate? sourcePredicate = null
        )
    {
        ArgumentNullException.ThrowIfNull(rootKeySelector);
        return JoinOne(alias, source, MemberSelector.ResolveName(rootKeySelector), options, sourcePredicate);
    }

    /// <summary>
    /// Adds a one-to-one join resolved from a previously hydrated alias.
    /// </summary>
    /// <param name="alias">The alias used to reference the joined observation in later joins and the final projection.</param>
    /// <param name="fromAlias">The alias whose joined observation provides the source key field.</param>
    /// <param name="source">The source from which joined observations are loaded.</param>
    /// <param name="sourceKeyField">The field on the previously joined observation containing the next joined observation id.</param>
    /// <param name="options">Optional field selection applied when loading joined observations.</param>
    /// <param name="sourcePredicate">Optional predicate applied to joined observations.</param>
    /// <returns>The same builder so additional joins, filters, or projection can be configured.</returns>
    public QueryBuilder JoinOneFrom(
        string alias,
        string fromAlias,
        QuerySource source,
        string sourceKeyField,
        FieldSelection? options = null,
        EntityPredicate? sourcePredicate = null
        )
    {
        ValidateAlias(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromAlias);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKeyField);
        joins.Add(new OneJoinFromSpec(alias, fromAlias, source, sourceKeyField, options, sourcePredicate));
        return this;
    }

    /// <summary>
    /// Adds a one-to-one join resolved from a previously hydrated CLR type at the authoring boundary.
    /// </summary>
    /// <param name="alias">The alias used to reference the joined observation in later joins and the final projection.</param>
    /// <param name="fromAlias">The alias whose joined observation provides the source key member.</param>
    /// <param name="source">The source from which joined observations are loaded.</param>
    /// <param name="sourceKey">Selects the joined member containing the next joined observation id.</param>
    /// <param name="options">Optional field selection applied when loading joined observations.</param>
    /// <param name="sourcePredicate">Optional predicate applied to joined observations.</param>
    /// <typeparam name="TSource">The CLR type used at the authoring boundary for the source alias.</typeparam>
    /// <typeparam name="TKey">The type of the source key member.</typeparam>
    /// <returns>The same builder so additional joins, filters, or projection can be configured.</returns>
    public QueryBuilder JoinOneFrom<TSource, TKey>(
        string alias,
        string fromAlias,
        QuerySource source,
        Expression<Func<TSource, TKey>> sourceKey,
        FieldSelection? options = null,
        EntityPredicate? sourcePredicate = null
        )
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        return JoinOneFrom(alias, fromAlias, source, MemberSelector.ResolveName(sourceKey), options, sourcePredicate);
    }

    /// <summary>
    /// Adds a one-to-many join resolved from a root field path and a joined foreign-key field.
    /// </summary>
    /// <param name="alias">The alias used to reference the joined observations in later joins and the final projection.</param>
    /// <param name="source">The source from which joined observations are loaded.</param>
    /// <param name="rootKeyPath">The root observation field path providing the join key or keys.</param>
    /// <param name="foreignKeyField">The joined observation field matched against the root key.</param>
    /// <param name="options">Optional field selection applied when loading joined observations.</param>
    /// <param name="sourcePredicate">Optional predicate applied to joined observations.</param>
    /// <returns>The same builder so additional joins, filters, or projection can be configured.</returns>
    public QueryBuilder JoinMany(
        string alias,
        QuerySource source,
        FieldPath rootKeyPath,
        string foreignKeyField,
        FieldSelection? options = null,
        EntityPredicate? sourcePredicate = null
        )
    {
        ValidateAlias(alias);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignKeyField);
        joins.Add(new ManyJoinSpec(alias, source, rootKeyPath, foreignKeyField, options, sourcePredicate));
        return this;
    }

    /// <summary>
    /// Adds a one-to-many join resolved from a root field and a joined foreign-key field.
    /// </summary>
    /// <param name="alias">The alias used to reference the joined observations in later joins and the final projection.</param>
    /// <param name="source">The source from which joined observations are loaded.</param>
    /// <param name="rootKeyField">The root observation field providing the join key.</param>
    /// <param name="foreignKeyField">The joined observation field matched against the root key.</param>
    /// <param name="options">Optional field selection applied when loading joined observations.</param>
    /// <param name="sourcePredicate">Optional predicate applied to joined observations.</param>
    /// <returns>The same builder so additional joins, filters, or projection can be configured.</returns>
    public QueryBuilder JoinMany(
        string alias,
        QuerySource source,
        string rootKeyField,
        string foreignKeyField,
        FieldSelection? options = null,
        EntityPredicate? sourcePredicate = null
        ) =>
        JoinMany(
            alias,
            source,
            FieldPath.Parse(rootKeyField),
            foreignKeyField,
            options,
            sourcePredicate);

    /// <summary>
    /// Adds a one-to-many join resolved from CLR selectors at the authoring boundary.
    /// </summary>
    /// <param name="alias">The alias used to reference the joined observations in later joins and the final projection.</param>
    /// <param name="source">The source from which joined observations are loaded.</param>
    /// <param name="rootKey">Selects the root member providing the join key.</param>
    /// <param name="foreignKey">Selects the joined member matched against the root key.</param>
    /// <param name="options">Optional field selection applied when loading joined observations.</param>
    /// <param name="sourcePredicate">Optional predicate applied to joined observations.</param>
    /// <typeparam name="TRoot">The CLR type used at the authoring boundary for root observations.</typeparam>
    /// <typeparam name="TRecord">The CLR type used at the authoring boundary for joined observations.</typeparam>
    /// <typeparam name="TKey">The type of the join key.</typeparam>
    /// <returns>The same builder so additional joins, filters, or projection can be configured.</returns>
    public QueryBuilder JoinMany<TRoot, TRecord, TKey>(
        string alias,
        QuerySource source,
        Expression<Func<TRoot, TKey>> rootKey,
        Expression<Func<TRecord, TKey>> foreignKey,
        FieldSelection? options = null,
        EntityPredicate? sourcePredicate = null
        )
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(foreignKey);
        return JoinMany(
            alias,
            source,
            FieldPath.FromField(MemberSelector.ResolveName(rootKey)),
            MemberSelector.ResolveName(foreignKey),
            options,
            sourcePredicate
            );
    }

    /// <summary>
    /// Adds a post-join predicate evaluated against the synthetic joined observation.
    /// </summary>
    /// <param name="predicate">The predicate to evaluate after joins have been hydrated.</param>
    /// <returns>The same builder so additional joins, filters, or projection can be configured.</returns>
    public QueryBuilder Where(EntityPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        resultPredicate = resultPredicate is null ? predicate : EntityPredicatePlanner.And(resultPredicate, predicate);
        return this;
    }

    /// <summary>
    /// Creates an executable query that projects into the given result type.
    /// </summary>
    /// <param name="projector">Projection function.</param>
    /// <param name="mappingContext">The shape mapping context to use for projecting observations.</param>
    /// <typeparam name="TResult">The final projected result type.</typeparam>
    /// <returns>An executable query that hydrates joins and returns projected results.</returns>
    /// <exception cref="InvalidOperationException">No root source or duplicate root sources.</exception>
    public ExecutableQuery<IReadOnlyList<TResult>> Select<TResult>(Func<JoinContext, TResult> projector, ShapeMappingContext? mappingContext = null)
    {
        ArgumentNullException.ThrowIfNull(projector);
        if (rootQuery is not null && roots is not null)
            throw new InvalidOperationException("Query cannot define both a root source and a list of roots.");
        
        ValidateJoins(joins);

        if (rootQuery is not null)
        {
            return new(async (context, repositoryRegistry) =>
            {
                var engine = new QueryExecutionEngine(repositoryRegistry);
                return await engine.ExecuteAsync<TResult>(context, new(RootQuery: rootQuery, Joins: joins, ResultPredicate: resultPredicate, Projector: projector), mappingContext).ConfigureAwait(false);
            });
        }
        
        if (roots is not null)
        {
            return new(async (context, repositoryRegistry) =>
            {
                var engine = new QueryExecutionEngine(repositoryRegistry);
                var filteredRoots = rootPredicate is null
                    ? roots
                    : [.. roots.Where(root => EntityPredicateEvaluator.Evaluate(root, rootPredicate))];
                return await engine.ExecuteAsync(context, filteredRoots, joins, projector, mappingContext).ConfigureAwait(false);
            });
        }
        
        throw new InvalidOperationException("Query must define a root source before selecting results.");
    }

    void ValidateAlias(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        if (joins.Any(join => string.Equals(join.Alias, alias, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Query already declares join alias '{alias}'.");
    }

    static void ValidateJoins(IReadOnlyList<JoinSpec> joins)
    {
        var byAlias = joins.ToDictionary(join => join.Alias, StringComparer.Ordinal);
        foreach (var join in joins)
        {
            if (join.FromAlias is null)
                continue;

            if (!byAlias.TryGetValue(join.FromAlias, out var parent))
                throw new InvalidOperationException($"Join '{join.Alias}' depends on unknown alias '{join.FromAlias}'.");

            if (parent.Cardinality != JoinCardinality.One)
                throw new InvalidOperationException($"Join '{join.Alias}' depends on '{join.FromAlias}', but nested joins can only depend on one-to-one joins.");
        }

        JoinScheduler.Schedule(joins);
    }
}

/// <summary>
/// Query builder accessor.
/// </summary>
public static class Query
{
    /// <summary>
    /// Creates a query builder rooted in the given objects and with the given predicate.
    /// </summary>
    /// <param name="source">The source queried for root observations.</param>
    /// <param name="predicate">The predicate evaluated against root observations.</param>
    /// <param name="fields">Optional field-selection options for root observations.</param>
    /// <param name="window">Optional result-window options such as limit, offset, and ordering.</param>
    /// <returns>A query builder rooted in the given source query.</returns>
    public static QueryBuilder From(
        QuerySource source,
        EntityPredicate predicate,
        FieldSelection? fields = null,
        ResultPageOptions? window = null
        ) =>
        new(rootQuery: new(source, Request: new(predicate, fields, window)));

    /// <summary>
    /// Creates a query builder rooted in the given values.
    /// </summary>
    /// <param name="roots">The roots.</param>
    /// <param name="predicate">An optional predicate on the roots.</param>
    /// <returns></returns>
    public static QueryBuilder From(IReadOnlyList<Observation> roots, EntityPredicate? predicate = null) => 
        new(roots: roots, rootPredicate: predicate);
    
    /// <summary>
    /// Creates a query builder rooted in the given objects.
    /// </summary>
    /// <param name="roots">The objects to which the joins will be rooted.</param>
    /// <param name="rootId">Gets the id for root objects.</param>
    /// <param name="mappingContext">Optional mapping context used when mapping roots and joined observations.</param>
    /// <param name="predicate">An optional predicate on root objects.</param>
    /// <typeparam name="TRoot">The type of root objects.</typeparam>
    /// <returns></returns>
    public static QueryBuilder From<TRoot>(IReadOnlyList<TRoot> roots, Func<TRoot, string> rootId, ShapeMappingContext? mappingContext = null, EntityPredicate? predicate = null) => 
        From(roots: ToObservations(roots, rootId: rootId, mappingContext: mappingContext), predicate: predicate);
    
    static IReadOnlyList<Observation> ToObservations<TRoot>(IReadOnlyList<TRoot> roots, Func<TRoot, string> rootId, ShapeMappingContext? mappingContext)
    {
        var mapping = mappingContext ?? ShapeMappingContext.Default;
        var shapeId = new ShapeId(typeof(TRoot).Name);
        return
        [
            .. roots.Select(root => mapping.Map(
                root,
                schemaId: shapeId,
                metadata: new() { Id = Guard.RequireNotNullOrWhiteSpace(rootId(root)) }
                )
            )
        ];
    }
}
