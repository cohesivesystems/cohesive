using System.Collections.ObjectModel;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Hydration;

/// <summary>
/// Hydrates root and related observations required for projection execution.
/// </summary>
public interface IRelationHydrator
{
    /// <summary>
    /// Hydrates observations for the provided projection and root ids.
    /// </summary>
    Task<IReadOnlyList<RootedObservation>> HydrateAsync(RelationDefinition definition, IReadOnlyList<string> rootIds, CancellationToken token = default);
}

/// <summary>
/// Default projection hydrator that performs explicit field selection.
/// </summary>
public sealed class RelationHydrator : IRelationHydrator
{
    readonly IObservationHydrationStore store;
    readonly RelationHydrationPlanner planner;
    readonly RelExpressionEvaluator evaluator = new();

    /// <summary>
    /// Creates a projection hydrator.
    /// </summary>
    public RelationHydrator(
        IObservationHydrationStore store,
        RelationHydrationPlanner? planner = null)
    {
        this.store = Guard.RequireNotNull(store);
        this.planner = planner ?? new();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RootedObservation>> HydrateAsync(
        RelationDefinition definition,
        IReadOnlyList<string> rootIds,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rootIds);
        token.ThrowIfCancellationRequested();

        var plan = planner.Plan(definition);
        var roots = await store.QueryAsync(
            new(
                Schema: plan.RootSchema,
                Fields: plan.RootFields,
                Keys: rootIds.Distinct(StringComparer.Ordinal).ToArray()),
            token);

        var rootedRoots = roots
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Select(static x => new RootedObservation(x, x.Id))
            .ToArray();
        if (rootedRoots.Length == 0 || plan.Related.Count == 0)
            return rootedRoots;

        var hydrated = new List<RootedObservation>(rootedRoots);
        var relatedBySchemaAndId = new Dictionary<string, IReadOnlyDictionary<string, Observation>>(StringComparer.Ordinal);

        foreach (var related in plan.Related)
        {
            token.ThrowIfCancellationRequested();
            var lookupIds = ResolveLookupIds(rootedRoots, related.LookupKeyExpressions);
            if (lookupIds.Count == 0)
                continue;

            var queried = await store.QueryAsync(
                new(
                    Schema: related.Schema,
                    Fields: related.Fields,
                    Keys: lookupIds.ToArray()),
                token);

            var relatedIndex = BuildRelatedIndex(queried);
            relatedBySchemaAndId[related.Schema.Value] = relatedIndex;
        }

        var emittedRelated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in rootedRoots)
        {
            token.ThrowIfCancellationRequested();
            foreach (var related in plan.Related)
            {
                if (!relatedBySchemaAndId.TryGetValue(related.Schema.Value, out var index))
                    continue;

                foreach (var lookupExpression in related.LookupKeyExpressions)
                {
                    var lookup = ResolveLookupId(root, lookupExpression);
                    if (lookup is null || !index.TryGetValue(lookup, out var relatedObservation))
                        continue;

                    var scoped = new RootedObservation(relatedObservation, root.RootId);
                    var dedupeKey = $"{scoped.RootId}|{scoped.ShapeId.Value}|{scoped.Id}";
                    if (emittedRelated.Add(dedupeKey))
                        hydrated.Add(scoped);
                }
            }
        }

        return hydrated
            .OrderBy(x => x.RootId, StringComparer.Ordinal)
            .ThenBy(x => x.ShapeId.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();
    }

    HashSet<string> ResolveLookupIds(
        IReadOnlyList<RootedObservation> roots,
        IReadOnlyList<Expr> lookupExpressions)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        foreach (var expression in lookupExpressions)
        {
            var lookup = ResolveLookupId(root, expression);
            if (lookup is not null)
                ids.Add(lookup);
        }

        return ids;
    }

    string? ResolveLookupId(RootedObservation root, Expr expr)
    {
        var scopedRoot = new RelationScopedObservation(root.Observation, root.RootId);
        var context = new RelationEvaluationContext(
            Root: scopedRoot,
            Related: [],
            Universe: [scopedRoot],
            SourceSet: [scopedRoot],
            CurrentObservation: null);
        var result = evaluator.Evaluate(expr, context).Value;
        return ConvertToString(result);
    }

    static IReadOnlyDictionary<string, Observation> BuildRelatedIndex(IReadOnlyList<Observation> related)
    {
        Dictionary<string, Observation> index = new(StringComparer.Ordinal);
        foreach (var observation in related)
            index[observation.Id] = observation;

        return new ReadOnlyDictionary<string, Observation>(index);
    }

    static string? ConvertToString(ObservationValue value)
    {
        var scalar = value.ToScalarString(
            formatProvider: System.Globalization.CultureInfo.InvariantCulture,
            bytesEncoding: ObservationBytesJsonEncoding.Base64String);
        return value.Kind == ObservationValueKind.String ? Normalize(scalar) : scalar;
    }

    static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
