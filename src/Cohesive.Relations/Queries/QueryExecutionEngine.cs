using System.Globalization;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Queries;

/// <summary>
/// Executes structured observation queries by reading roots, hydrating joins, applying post-join predicates, and projecting results.
/// </summary>
public sealed class QueryExecutionEngine(IReadRepositoryRegistry repositoryRegistry)
{
    /// <summary>
    /// Executes the supplied query plan.
    /// </summary>
    /// <exception cref="NotSupportedException">A <see cref="IQueryRepository"/> is required to execute the query.</exception>
    public async Task<IReadOnlyList<TResult>> ExecuteAsync<TResult>(OperationContext context, QueryPlan<TResult> plan, ShapeMappingContext? mappingContext = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.ThrowIfCancellationRequested();

        var repository = repositoryRegistry.GetRequired(plan.RootQuery.Source);
        var readOptions = EnsureRootReadFields(plan.RootQuery.Request.Fields, plan.Joins);
        var roots = await LoadRootsAsync(context, repository, plan.RootQuery.Request with { Fields = readOptions }).ConfigureAwait(false);
        var joinContexts = await HydrateAsync(context, roots, plan.Joins, mappingContext).ConfigureAwait(false);

        IEnumerable<JoinContext> filtered = joinContexts;
        if (plan.ResultPredicate is not null)
            filtered = filtered.Where(ctx => EntityPredicateEvaluator.Evaluate(CreateJoinObservation(ctx, plan.Joins), plan.ResultPredicate));

        return [.. filtered.Select(plan.Projector)];
    }

    static async Task<IReadOnlyList<Observation>> LoadRootsAsync(
        OperationContext context,
        IReadRepository repository,
        EntityQuery query)
    {
        if (repository is IQueryRepository queryRepository)
            return (await queryRepository.Query(context, query).ConfigureAwait(false)).Rows;

        if (!TryResolvePointReadIds(query, out var ids))
        {
            throw new NotSupportedException(
                $"Root source '{repository.GetType().Name}' requires '{nameof(IQueryRepository)}' to execute '{nameof(QueryPlan<>)}' unless the root query can be satisfied by point reads.");
        }

        var loaded = await repository
            .GetByIds(context, ids, query.Fields)
            .ConfigureAwait(false);

        var roots = query.Predicate is null
            ? loaded.Values
            : loaded.Values.Where(root => EntityPredicateEvaluator.Evaluate(root, query.Predicate));

        roots = ApplyWindow(roots, query.Window);
        return [.. roots];
    }

    static Observation CreateJoinObservation(JoinContext context, IReadOnlyList<JoinSpec> joins)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(joins);

        Dictionary<string, ObservationValue> fields = new(StringComparer.Ordinal)
        {
            ["root"] = ObservationValue.FromObject(context.Root.Fields)
        };

        foreach (var join in joins)
        {
            fields[join.Alias] = join.Cardinality switch
            {
                JoinCardinality.One => context.One(join.Alias) is { } one
                    ? ObservationValue.FromObject(one.Fields)
                    : ObservationValue.Null,
                JoinCardinality.Many => ObservationValue.FromArray([.. context.Many(join.Alias).Select(static item => ObservationValue.FromObject(item.Fields))]),
                _ => throw new InvalidOperationException($"Unsupported join cardinality '{join.Cardinality}'.")
            };
        }

        return new(
            shapeId: new("JoinedObservation"),
            id: context.Root.Id,
            fields: fields,
            version: context.Root.Version,
            lineage: context.Root.Lineage
            );
    }
    
    /// <summary>
    /// Executes the supplied join plan against the provided roots.
    /// </summary>
    public async Task<IReadOnlyList<TResult>> ExecuteAsync<TResult>(OperationContext context, IReadOnlyList<Observation> roots, IReadOnlyList<JoinSpec> joins, Func<JoinContext, TResult> projector, ShapeMappingContext? mappingContext = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(joins);
        ArgumentNullException.ThrowIfNull(projector);
        context.ThrowIfCancellationRequested();

        var joinContexts = await HydrateAsync(context, roots, joins, mappingContext).ConfigureAwait(false);
        return [.. joinContexts.Select(projector)];
    }

    async Task<JoinContext[]> HydrateAsync(
        OperationContext context,
        IReadOnlyList<Observation> roots,
        IReadOnlyList<JoinSpec> joins,
        ShapeMappingContext? mappingContext = null
        )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(joins);
        context.ThrowIfCancellationRequested();

        var joinContexts = roots
            .Select(root => new JoinContext(root, mappingContext))
            .ToArray();

        if (joins.Count == 0)
            return joinContexts;

        var schedule = JoinScheduler.Schedule(joins);
        foreach (var stage in schedule.Stages)
        {
            foreach (var join in stage.Joins)
                await ExecuteJoinAsync(context, join, joinContexts).ConfigureAwait(false);
        }

        return joinContexts;
    }

    static FieldSelection? EnsureRootReadFields(FieldSelection? read, IReadOnlyList<JoinSpec> joins)
    {
        if (read?.Fields is null || read.Fields.Count == 0)
            return read;

        HashSet<string> fields = new(read.Fields, StringComparer.Ordinal);
        foreach (var field in joins.OfType<OneJoinSpec>().Select(static join => join.RootKeyField))
        {
            fields.Add(field);
        }

        foreach (var path in joins.OfType<ManyJoinSpec>().Select(static join => join.RootKeyPath))
        {
            if (TryGetTopLevelField(path, out var field))
                fields.Add(field);
        }

        return fields.SetEquals(read.Fields) ? read : new(fields);
    }

    Task ExecuteJoinAsync(OperationContext context, JoinSpec join, JoinContext[] joinContexts) => join switch
    {
        OneJoinSpec one => ExecOneFromRoot(context, one, joinContexts),
        OneJoinFromSpec oneFrom => ExecOneFromAlias(context, oneFrom, joinContexts),
        ManyJoinSpec many => ExecManyFromRoot(context, many, joinContexts),
        _ => throw new InvalidOperationException($"Projection join type '{join.GetType().FullName}' is not supported.")
    };

    async Task ExecOneFromRoot(OperationContext context, OneJoinSpec join, JoinContext[] joinContexts)
    {
        var keys = joinContexts
            .Select(joinContext => TryGetId(joinContext.Root, join.RootKeyField, out var key) ? key : null)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (keys.Length == 0)
        {
            foreach (var joinContext in joinContexts)
                joinContext.SetOne(join.Alias, null);

            return;
        }

        var repository = repositoryRegistry.GetRequired(join.Source);
        var records = await LoadOneJoinCandidatesAsync(context, repository, keys, join.Options, join.SourcePredicate).ConfigureAwait(false);

        foreach (var joinContext in joinContexts)
        {
            if (!TryGetId(joinContext.Root, join.RootKeyField, out var key) || !records.TryGetValue(key, out var record))
            {
                joinContext.SetOne(join.Alias, null);
                continue;
            }

            joinContext.SetOne(join.Alias, record);
        }
    }

    async Task ExecOneFromAlias(OperationContext context, OneJoinFromSpec join, JoinContext[] joinContexts)
    {
        var keys = joinContexts
            .Select(joinContext => joinContext.One(join.FromAlias!))
            .Where(static source => source is not null)
            .Select(source => TryGetId(source!, join.SourceKeyField, out var key) ? key : null)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (keys.Length == 0)
        {
            foreach (var joinContext in joinContexts)
                joinContext.SetOne(join.Alias, null);

            return;
        }

        var repository = repositoryRegistry.GetRequired(join.Source);
        var records = await LoadOneJoinCandidatesAsync(
            context,
            repository,
            keys,
            join.Options,
            join.SourcePredicate).ConfigureAwait(false);

        foreach (var joinContext in joinContexts)
        {
            var source = joinContext.One(join.FromAlias!);
            if (source is null || !TryGetId(source, join.SourceKeyField, out var key) || !records.TryGetValue(key, out var record))
            {
                joinContext.SetOne(join.Alias, null);
                continue;
            }

            joinContext.SetOne(join.Alias, record);
        }
    }

    async Task ExecManyFromRoot(OperationContext context, ManyJoinSpec join, JoinContext[] joinContexts)
    {
        var keysByContext = joinContexts
            .Select(joinContext => ResolveJoinKeys(joinContext.Root, join.RootKeyPath))
            .ToArray();

        var keys = keysByContext
            .SelectMany(static keys => keys)
            .Distinct()
            .ToArray();

        if (keys.Length == 0)
        {
            foreach (var joinContext in joinContexts)
                joinContext.SetMany(join.Alias, []);

            return;
        }

        var repository = repositoryRegistry.GetRequired(join.Source);
        var grouped = await LoadManyJoinCandidatesAsync(context, repository, join.ForeignKeyField, keys, join.Options, join.SourcePredicate).ConfigureAwait(false);

        for (var i = 0; i < joinContexts.Length; i++)
        {
            var joinContext = joinContexts[i];
            var rootKeys = keysByContext[i];
            if (rootKeys.Length == 0)
            {
                joinContext.SetMany(join.Alias, []);
                continue;
            }

            List<Observation> records = [];
            HashSet<string> seenIds = new(StringComparer.Ordinal);
            foreach (var key in rootKeys)
            {
                if (!grouped.TryGetValue(key, out var candidates))
                    continue;

                foreach (var candidate in candidates)
                {
                    if (seenIds.Add(candidate.Id))
                        records.Add(candidate);
                }
            }

            joinContext.SetMany(join.Alias, records);
        }
    }

    static bool TryGetId(Observation observation, string fieldName, out string id)
    {
        if (TryGetFieldValue(observation, fieldName, out var value))
        {
            try
            {
                var scalar = value.ToScalarString(CultureInfo.InvariantCulture, ObservationBytesJsonEncoding.Base64String);
                if (!string.IsNullOrWhiteSpace(scalar))
                {
                    id = scalar;
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        id = string.Empty;
        return false;
    }

    static bool TryGetFieldValue(Observation observation, string fieldName, out ObservationValue value)
    {
        if (!observation.TryGetField(fieldName, out value))
            return false;

        return value.Kind is not ObservationValueKind.Null and not ObservationValueKind.Undefined;
    }

    static bool TryResolvePointReadIds(EntityQuery query, out string[] ids)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Predicate is null)
        {
            ids = [];
            return false;
        }

        if (query.Predicate.Scope is not null)
        {
            ids = [];
            return false;
        }

        if (!TryResolvePointReadIds(query.Predicate.Predicate.Normalize(), out ids))
            return false;

        ids = [.. ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)];
        return ids.Length > 0;
    }

    static bool TryResolvePointReadIds(BoolExpr<FieldPredicate> predicate, out string[] ids)
    {
        switch (predicate)
        {
            case Atom<FieldPredicate> { Term: var fieldPredicate }:
                return TryResolvePointReadIds(fieldPredicate, out ids);
            case Or<FieldPredicate> disjunction:
            {
                List<string> resolved = [];
                foreach (var term in disjunction.Terms)
                {
                    if (!TryResolvePointReadIds(term, out var termIds))
                    {
                        ids = [];
                        return false;
                    }

                    resolved.AddRange(termIds);
                }

                ids = [.. resolved];
                return ids.Length > 0;
            }
            default:
                ids = [];
                return false;
        }
    }

    static bool TryResolvePointReadIds(FieldPredicate predicate, out string[] ids)
    {
        if (!predicate.Field.Matches("Id"))
        {
            ids = [];
            return false;
        }

        return TryResolvePointReadIds(predicate.Predicate.Normalize(), out ids);
    }

    static bool TryResolvePointReadIds(BoolExpr<ValuePredicate> predicate, out string[] ids)
    {
        switch (predicate)
        {
            case Atom<ValuePredicate> { Term: ExactValuePredicate { CaseSensitive: true } exact }:
                ids = [exact.Value];
                return true;
            case Atom<ValuePredicate> { Term: InValuePredicate set }:
            {
                ids = [.. set.Values.OfType<string>()];
                return ids.Length == set.Values.Count;
            }
            case Or<ValuePredicate> disjunction:
            {
                List<string> resolved = [];
                foreach (var term in disjunction.Terms)
                {
                    if (!TryResolvePointReadIds(term, out var termIds))
                    {
                        ids = [];
                        return false;
                    }

                    resolved.AddRange(termIds);
                }

                ids = [.. resolved];
                return ids.Length > 0;
            }
            default:
                ids = [];
                return false;
        }
    }

    static ObservationValue[] ResolveJoinKeys(Observation observation, FieldPath path)
    {
        IEnumerable<ObservationValue> current = [ObservationValue.FromObject(observation.Fields)];
        foreach (var segment in path.Segments)
        {
            current = segment.Kind switch
            {
                SegmentKind.Field => ResolveObjectSegment(current, segment.Segment!),
                SegmentKind.Element => ResolveArrayElements(current),
                _ => throw new InvalidOperationException($"Unsupported field-path segment kind '{segment.Kind}'.")
            };
        }

        return [.. current
            .Where(static value => value.Kind is not ObservationValueKind.Null and not ObservationValueKind.Undefined)
            .Distinct()];
    }

    static IEnumerable<ObservationValue> ResolveObjectSegment(IEnumerable<ObservationValue> values, string fieldName)
    {
        foreach (var value in values)
        {
            if (value.Kind != ObservationValueKind.Object || value.Fields is null)
                continue;

            if (value.Fields.TryGetValue(fieldName, out var nested))
                yield return nested;
        }
    }

    static IEnumerable<ObservationValue> ResolveArrayElements(IEnumerable<ObservationValue> values)
    {
        foreach (var value in values)
        {
            if (value.Kind != ObservationValueKind.Array || value.Array is null)
                continue;

            foreach (var element in value.Array)
                yield return element;
        }
    }

    static bool TryGetTopLevelField(FieldPath path, out string field)
    {
        foreach (var segment in path.Segments)
        {
            if (!segment.TryGetFieldIdentity(out field))
                continue;

            return true;
        }

        field = string.Empty;
        return false;
    }

    static async Task<IReadOnlyDictionary<string, Observation>> LoadOneJoinCandidatesAsync(
        OperationContext context,
        IReadRepository repository,
        IReadOnlyCollection<string> ids,
        FieldSelection? read,
        EntityPredicate? sourcePredicate
        )
    {
        var records = await repository
            .GetByIds(context, ids, EnsureReadFields(read, sourcePredicate))
            .ConfigureAwait(false);

        if (sourcePredicate is null)
            return records;

        return records
            .Where(pair => EntityPredicateEvaluator.Evaluate(pair.Value, sourcePredicate))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    }

    static async Task<IReadOnlyDictionary<ObservationValue, IReadOnlyList<Observation>>> LoadManyJoinCandidatesAsync(
        OperationContext context,
        IReadRepository repository,
        string foreignKeyField,
        IReadOnlyCollection<ObservationValue> keys,
        FieldSelection? read,
        EntityPredicate? sourcePredicate
        )
    {
        if (repository is IQueryRepository queryRepository)
        {
            var predicate = sourcePredicate is null
                ? CreateSetMembershipQuery(foreignKeyField, keys)
                : EntityPredicatePlanner.And(CreateSetMembershipQuery(foreignKeyField, keys), sourcePredicate);
            
            var queried = await queryRepository
                .Query(context, new(Predicate: predicate, Fields: EnsureReadFields(read, sourcePredicate, foreignKeyField)))
                .ConfigureAwait(false);

            return GroupByField(queried.Rows, foreignKeyField);
        }

        if (!string.Equals(foreignKeyField, "Id", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Repository '{repository.GetType().Name}' requires '{nameof(IQueryRepository)}' to load many-join records by field '{foreignKeyField}'.");
        }

        var ids = keys
            .Select(TryGetId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var byId = await repository
            .GetByIds(context, ids, EnsureReadFields(read, sourcePredicate))
            .ConfigureAwait(false);

        IEnumerable<Observation> records = byId.Values;
        if (sourcePredicate is not null)
            records = records.Where(record => EntityPredicateEvaluator.Evaluate(record, sourcePredicate));

        return records
            .GroupBy(static record => ObservationValue.FromString(record.Id))
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<Observation>)[.. group],
                EqualityComparer<ObservationValue>.Default);
    }

    static IReadOnlyDictionary<ObservationValue, IReadOnlyList<Observation>> GroupByField(IEnumerable<Observation> records, string fieldName)
    {
        Dictionary<ObservationValue, IReadOnlyList<Observation>> grouped = [];
        foreach (var record in records)
        {
            var hasKey = string.Equals(fieldName, "Id", StringComparison.OrdinalIgnoreCase)
                ? TryGetIdValue(record, out var key)
                : record.TryGetField(fieldName, out key);
            if (!hasKey)
                continue;

            if (!grouped.TryGetValue(key, out var items))
                items = [];

            grouped[key] = [.. items, record];
        }

        return grouped;
    }

    static string? TryGetId(ObservationValue value)
    {
        try
        {
            var id = value.ToScalarString(CultureInfo.InvariantCulture, ObservationBytesJsonEncoding.Base64String);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    static bool TryGetIdValue(Observation observation, out ObservationValue value)
    {
        if (string.IsNullOrWhiteSpace(observation.Id))
        {
            value = default;
            return false;
        }

        value = ObservationValue.FromString(observation.Id);
        return true;
    }

    static EntityPredicate CreateSetMembershipQuery(string fieldName, IReadOnlyCollection<ObservationValue> values)
    {
        var normalized = values.Distinct().ToArray();
        if (normalized.Length == 0)
            throw new InvalidOperationException("Set-membership queries require at least one value.");

        BoolExpr<ValuePredicate> predicate = normalized.Length == 1
            ? CreateEqualityQuery(normalized[0])
            : new Or<ValuePredicate>([.. normalized.Select(CreateEqualityQuery)]);

        return new(new FieldPredicate(FieldPath.FromField(fieldName), predicate));
    }

    static ValuePredicate CreateEqualityQuery(ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.String => new ExactValuePredicate(value.String ?? string.Empty),
        ObservationValueKind.DateTimeOffset => new DateValuePredicate(value.GetDateTimeOffset()),
        ObservationValueKind.Int64 => new LongValuePredicate(value.Int64),
        ObservationValueKind.Double => new DoubleValuePredicate(value.Double),
        ObservationValueKind.Bool => new BoolValuePredicate(value.Bool),
        _ => throw new NotSupportedException($"Join pushdown does not support key values of kind '{value.Kind}'.")
    };

    static FieldSelection? EnsureReadFields(FieldSelection? read, EntityPredicate? sourcePredicate, params string[] requiredFields)
    {
        if (read?.Fields is null || read.Fields.Count == 0)
            return read;

        HashSet<string> fields = new(read.Fields, StringComparer.Ordinal);
        foreach (var field in requiredFields.Where(static field => !string.IsNullOrWhiteSpace(field)))
            fields.Add(field);

        if (sourcePredicate is not null)
        {
            foreach (var field in EntityPredicatePlanner.GetRequiredTopLevelFields(sourcePredicate))
                fields.Add(field);
        }

        return fields.SetEquals(read.Fields) ? read : new(fields);
    }

    static IEnumerable<Observation> ApplyWindow(IEnumerable<Observation> source, ResultPageOptions? window)
    {
        var results = source;

        if (window?.OrderBy is { Length: > 0 } orderBy)
        {
            IOrderedEnumerable<Observation>? ordered = null;
            foreach (var order in orderBy)
            {
                ordered = ordered is null
                    ? ApplyPrimaryOrdering(results, order)
                    : ApplySecondaryOrdering(ordered, order);
            }

            results = ordered ?? results;
        }

        if (window?.Cursor is not null)
            throw new NotSupportedException("In-process query execution does not yet support cursor page resumption.");

        if (window?.EffectiveMode == ResultPaginationMode.Offset && window.Offset is { } offset and > 0)
            results = results.Skip(offset);

        if (window?.Limit is { } limit)
        {
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(window), limit, "Read-query limit must be non-negative.");

            results = results.Take(limit);
        }

        return results;
    }

    static IOrderedEnumerable<Observation> ApplyPrimaryOrdering(IEnumerable<Observation> source, QueryOrderBy order) =>
        order.Descending
            ? source.OrderByDescending(record => record, ObservationOrderingComparer.ForField(order.Path))
            : source.OrderBy(record => record, ObservationOrderingComparer.ForField(order.Path));

    static IOrderedEnumerable<Observation> ApplySecondaryOrdering(IOrderedEnumerable<Observation> source, QueryOrderBy order) =>
        order.Descending
            ? source.ThenByDescending(record => record, ObservationOrderingComparer.ForField(order.Path))
            : source.ThenBy(record => record, ObservationOrderingComparer.ForField(order.Path));

    sealed class ObservationOrderingComparer(FieldPath path) : IComparer<Observation>
    {
        public int Compare(Observation? x, Observation? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            return EntityPredicateEvaluator.CompareFieldValues(x, y, path);
        }

        public static ObservationOrderingComparer ForField(FieldPath path) => new(path);
    }
}
