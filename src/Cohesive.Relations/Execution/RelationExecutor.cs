using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Execution;

/// <summary>
/// Options for projection execution.
/// </summary>
public sealed record RelationExecutionOptions
{
    /// <summary>
    /// Optional explainability callback.
    /// </summary>
    public IRelationExecutionListener? Listener { get; init; }
}

/// <summary>
/// Thrown when a projection invariant fails.
/// </summary>
public sealed class ProjectionInvariantViolationException(string message) : Exception(message);

/// <summary>
/// Deterministic relation executor with rooted incremental caching.
/// </summary>
public sealed class RelationExecutor : IRelationExecutor
{
    readonly RelationCompiler compiler;
    readonly IRelationExecutionListener? listener;
    readonly ConcurrentDictionary<string, RootCacheEntry> rootCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a projection executor.
    /// </summary>
    public RelationExecutor(
        RelationCompiler? compiler = null,
        RelationExecutionOptions? options = null
        )
    {
        this.compiler = compiler ?? new();
        listener = options?.Listener;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Observation>> ExecuteAsync(RelationDefinition relation, IReadOnlyList<Observation> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var rootedInputs = inputs.Select(static x => new RootedObservation(x, x.Id)).ToArray();
        return ExecuteAsync(relation, rootedInputs, ct);
    }
    
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Observation>> ExecuteAsync(RelationDefinition relation, IReadOnlyList<RootedObservation> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(inputs);
        ct.ThrowIfCancellationRequested();

        var compiled = compiler.Compile(relation);
        var planFingerprint = RelationCompiler.ComputeFingerprint(compiled);
        var universe = inputs
            .Select(static x => new RelationScopedObservation(x.Observation, rootId: x.RootId))
            .OrderBy(x => x.ShapeId.Value, StringComparer.Ordinal)
            .ThenBy(x => x.RootId, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();

        var rootShapeId = relation.RootSourceShapeId;
        var roots = universe.Where(x => x.ShapeId == rootShapeId).OrderBy(x => x.RootId, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (roots.Length == 0)
            return ValueTask.FromResult<IReadOnlyList<Observation>>([]);

        var rootedMappings = relation.Mappings.Where(x => x.Scope == MappingScope.Rooted).ToArray();
        var setMappings = relation.Mappings.Where(x => x.Scope == MappingScope.Set).ToArray();

        List<Observation> outputs = [];

        if (setMappings.Length > 0)
        {
            var related = universe
                .Where(x => x.ShapeId != rootShapeId)
                .OrderBy(x => x.ShapeId.Value, StringComparer.Ordinal)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .ToArray();
            var setContext = new RelationEvaluationContext(
                Root: roots[0],
                Related: related,
                Universe: universe,
                SourceSet: roots,
                CurrentObservation: null
                );
            var setEmitted = ExecuteMappings(
                relation: relation,
                mappings: setMappings,
                context: setContext,
                ct: ct
                );
            ValidateInvariants(relation.Invariants, setContext, setEmitted);
            outputs.AddRange(setEmitted.Select(static x => x.Observation));
        }

        if (rootedMappings.Length > 0)
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();

                var related = universe
                    .Where(x => string.Equals(x.RootId, root.RootId, StringComparison.Ordinal))
                    .OrderBy(x => x.ShapeId.Value, StringComparer.Ordinal)
                    .ThenBy(x => x.Id, StringComparer.Ordinal)
                    .ToArray();

                var cacheSlotKey = $"{planFingerprint}|{root.RootId}|{root.Id}";
                var sourceFingerprint = BuildSourceFingerprint(related);
                if (rootCache.TryGetValue(cacheSlotKey, out var cached) && string.Equals(cached.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal))
                {
                    outputs.AddRange(cached.Observations);
                    continue;
                }

                var rootContext = new RelationEvaluationContext(
                    Root: root,
                    Related: related,
                    Universe: universe,
                    SourceSet: [root],
                    CurrentObservation: null
                    );

                var emitted = ExecuteMappings(relation: relation, mappings: rootedMappings, context: rootContext, ct: ct);

                ValidateInvariants(relation.Invariants, rootContext, emitted);
                var materialized = emitted.Select(static x => x.Observation).ToArray();
                rootCache[cacheSlotKey] = new(sourceFingerprint, materialized);
                outputs.AddRange(materialized);
            }
        }

        var deterministic = outputs
            .OrderBy(x => x.ShapeId.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();
        
        return ValueTask.FromResult<IReadOnlyList<Observation>>(deterministic);
    }

    IReadOnlyList<RelationScopedObservation> ExecuteMappings(RelationDefinition relation, IReadOnlyList<MappingDefinition> mappings, RelationEvaluationContext context, CancellationToken ct)
    {
        var evaluator = new RelExpressionEvaluator();
        List<RelationScopedObservation> emitted = [];

        foreach (var mapping in mappings)
        {
            ct.ThrowIfCancellationRequested();
            var mappingId = mapping.Id.Value;
            var eachItems = ResolveForEach(mapping.ForEach, evaluator, context);
            var emitIndex = 0;

            foreach (var item in eachItems)
            {
                ct.ThrowIfCancellationRequested();
                var iterationContext = context with
                {
                    CurrentObservation = RelExpressionEvaluator.ToCurrentObservation(context.Root.Observation, item)
                };

                if (relation.Filter is not null)
                {
                    var relationPredicate = evaluator.Evaluate(relation.Filter, iterationContext);
                    if (!ToBoolean(relationPredicate.Value))
                        continue;
                }

                if (mapping.Predicate is not null)
                {
                    var mappingPredicate = evaluator.Evaluate(mapping.Predicate, iterationContext);
                    if (!ToBoolean(mappingPredicate.Value))
                        continue;
                }

                var observation = EmitMapping(
                    mapping: mapping,
                    mappingId: mappingId,
                    emitIndex: emitIndex,
                    context: iterationContext,
                    evaluator: evaluator
                    );
                
                emitIndex++;
                emitted.Add(observation);
            }
        }

        return emitted;
    }

    RelationScopedObservation EmitMapping(
        MappingDefinition mapping,
        string mappingId,
        int emitIndex,
        RelationEvaluationContext context,
        RelExpressionEvaluator evaluator
        )
    {
        Dictionary<string, ObservationValue> fields = new(StringComparer.Ordinal);
        List<FieldLineage> lineage = [];

        foreach (var assignment in mapping.Assignments)
        {
            var evaluation = evaluator.Evaluate(assignment.Expr, context);
            fields[assignment.TargetField] = evaluation.Value;

            var nodeId = assignment.Id ?? $"{mappingId}.{assignment.TargetField}";
            var contribution = new LineageContribution(
                nodeId: nodeId,
                sourcePaths: evaluation.SourcePaths,
                expression: assignment.Expr,
                reason: $"Assignment '{nodeId}' evaluated deterministically."
                );
            lineage.Add(new(targetField: assignment.TargetField, [contribution]));

            listener?.OnAssignment(new(
                RuleId: mappingId,
                TargetField: assignment.TargetField,
                SourcePaths: evaluation.SourcePaths,
                Expression: assignment.Expr,
                ObservationKey: context.Root.Id
                )
            );
        }

        var id = mapping.Key is null
            ? $"{context.Root.Id}:{mappingId}:{emitIndex.ToString(CultureInfo.InvariantCulture)}"
            : evaluator.Evaluate(mapping.Key, context).Value.ToScalarString(formatProvider: CultureInfo.InvariantCulture, bytesEncoding: ObservationBytesJsonEncoding.Base64String)
              ?? $"{context.Root.Id}:{mappingId}:{emitIndex.ToString(CultureInfo.InvariantCulture)}";

        var logicalEntityId = mapping.Entity is null
            ? context.Root.LogicalEntityId
            : evaluator.Evaluate(mapping.Entity, context).Value.ToScalarString(
                formatProvider: CultureInfo.InvariantCulture,
                bytesEncoding: ObservationBytesJsonEncoding.Base64String)
              ?? context.Root.LogicalEntityId;

        var observation = new Observation(
            shapeId: mapping.TargetShapeId,
            id: id,
            fields: fields,
            version: context.Root.Version,
            lineage: new(lineage)
            );
        return new(observation, context.Root.RootId, logicalEntityId);
    }

    static IReadOnlyList<ObservationValue> ResolveForEach(Expr? forEach, RelExpressionEvaluator evaluator, RelationEvaluationContext context)
    {
        if (forEach is null)
            return [ObservationValue.Null];

        var evaluated = evaluator.Evaluate(forEach, context).Value;
        var items = RelExpressionEvaluator.ToEnumerable(evaluated).ToArray();
        return items.Length == 0 ? [ObservationValue.Null] : items;
    }

    static void ValidateInvariants(IReadOnlyList<InvariantDefinition> invariants, RelationEvaluationContext rootContext, IReadOnlyList<RelationScopedObservation> emitted)
    {
        if (invariants.Count == 0 || emitted.Count == 0)
            return;

        var evaluator = new RelExpressionEvaluator();
        foreach (var invariant in invariants)
        {
            if (invariant.Entity is null)
                throw new InvalidOperationException($"Relation invariant '{invariant.Name}' must declare an entity id.");

            var entityId = invariant.Entity.Value.Value;
            var candidates = emitted.Where(x => string.Equals(x.LogicalEntityId, entityId, StringComparison.Ordinal)).ToArray();
            foreach (var candidate in candidates)
            {
                var context = rootContext with
                {
                    CurrentObservation = candidate.Observation
                };

                var passed = evaluator.Evaluate(invariant.Expression, context);
                if (!ToBoolean(passed.Value))
                {
                    var message = string.IsNullOrWhiteSpace(invariant.Message) ? $"Invariant '{invariant.Name}' failed." : invariant.Message;
                    throw new ProjectionInvariantViolationException($"Invariant failed for entity '{candidate.LogicalEntityId}' on id '{candidate.Id}': {message}");
                }
            }
        }
    }

    static string BuildSourceFingerprint(IReadOnlyList<RelationScopedObservation> related)
    {
        StringBuilder builder = new();
        foreach (var observation in related)
        {
            builder.Append(observation.ShapeId.Value)
                .Append('|')
                .Append(observation.RootId)
                .Append('|')
                .Append(observation.Id)
                .Append('|')
                .Append(observation.Version)
                .Append('|');
        }

        return builder.ToString();
    }

    static bool ToBoolean(ObservationValue value)
    {
        return value.Kind switch
        {
            ObservationValueKind.Undefined => false,
            ObservationValueKind.Null => false,
            ObservationValueKind.Bool => value.GetBoolean(),
            ObservationValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ when value.TryGetDouble(out var numeric) => Math.Abs(numeric) > double.Epsilon,
            _ => throw new InvalidOperationException($"Value '{value}' cannot be interpreted as boolean.")
        };
    }

    sealed record RootCacheEntry(string SourceFingerprint, IReadOnlyList<Observation> Observations);
}
