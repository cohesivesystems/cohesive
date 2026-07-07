using System.Collections.Concurrent;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Facade that composes relation execution and DTO/observed-shape mapping ergonomics.
/// </summary>
public sealed class RelationMappingRuntime
{
    readonly ConcurrentDictionary<Type, IObjectInputMapper> objectInputMappers = [];

    /// <summary>
    /// Creates a mapping runtime.
    /// </summary>
    public RelationMappingRuntime(
        ShapeMappingContext? mappingContext = null,
        IRelationExecutor? relationExecutor = null
        )
    {
        MappingContext = mappingContext ?? ShapeMappingContext.Default;
        RelationExecutor = relationExecutor ?? new RelationExecutor();
    }

    /// <summary>
    /// Mapping context used for DTO/observed-shape conversions.
    /// </summary>
    public ShapeMappingContext MappingContext { get; }

    /// <summary>
    /// Relation executor used for relation mapping evaluation.
    /// </summary>
    public IRelationExecutor RelationExecutor { get; }

    /// <summary>
    /// Executes relation mappings over observed-shape inputs.
    /// </summary>
    public ValueTask<IReadOnlyList<Observation>> ExecuteObservedAsync(
        RelationDefinition definition,
        IReadOnlyList<Observation> inputs,
        CancellationToken token = default
        ) => RelationExecutor.ExecuteAsync(definition, inputs, token);

    /// <summary>
    /// Executes relation mappings over mixed DTO/observed-shape inputs.
    /// </summary>
    public ValueTask<IReadOnlyList<Observation>> ExecuteObservedAsync(
        RelationDefinition definition,
        IReadOnlyList<RelationRuntimeInput> inputs,
        CancellationToken token = default
        )
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(inputs);
        return RelationExecutor.ExecuteAsync(definition, ToObservedInputs(inputs), token);
    }

    /// <summary>
    /// Executes relation mappings over mixed DTO/observed-shape inputs.
    /// </summary>
    public ValueTask<IReadOnlyList<Observation>> ExecuteObservedAsync(
        RelationDefinition definition,
        IReadOnlyList<object> inputs,
        CancellationToken token = default
        )
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var runtimeInputs = inputs.Select(x => RelationRuntimeInput.From(x)).ToArray();
        return ExecuteObservedAsync(definition, runtimeInputs, token);
    }

    /// <summary>
    /// Executes relation mappings and maps outputs to DTOs.
    /// </summary>
    public async ValueTask<IReadOnlyList<TOutput>> ExecuteAsync<TOutput>(
        RelationDefinition definition,
        IReadOnlyList<Observation> inputs,
        CancellationToken token = default
        )
    {
        var observed = await ExecuteObservedAsync(definition, inputs, token);
        return observed.Select(MappingContext.Map<TOutput>).ToArray();
    }

    /// <summary>
    /// Executes relation mappings over mixed DTO/observed-shape inputs and maps outputs to DTOs.
    /// </summary>
    public async ValueTask<IReadOnlyList<TOutput>> ExecuteAsync<TOutput>(
        RelationDefinition definition,
        IReadOnlyList<RelationRuntimeInput> inputs,
        CancellationToken token = default
        )
    {
        var observed = await ExecuteObservedAsync(definition, inputs, token);
        return [..observed.Select(MappingContext.Map<TOutput>)];
    }

    /// <summary>
    /// Executes relation mappings over mixed DTO/observed-shape inputs and maps outputs to DTOs.
    /// </summary>
    public ValueTask<IReadOnlyList<TOutput>> ExecuteAsync<TOutput>(
        RelationDefinition definition,
        IReadOnlyList<object> inputs,
        CancellationToken token = default
        )
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return ExecuteAsync<TOutput>(
            definition, 
            [..inputs.Select(static x => RelationRuntimeInput.From(x))], 
            token
            );
    }

    IReadOnlyList<Observation> ToObservedInputs(IReadOnlyList<RelationRuntimeInput> inputs)
    {
        if (inputs.Count == 0)
            return [];

        List<Observation> observed = new(inputs.Count);
        foreach (var input in inputs)
        {
            if (input.Value is Observation observation)
            {
                observed.Add(observation);
                continue;
            }

            var mapper = objectInputMappers.GetOrAdd(
                input.Value.GetType(),
                static type => CreateObjectInputMapper(type)
                );
            observed.Add(mapper.Map(input.Value, input.SchemaId, input.Metadata, MappingContext));
        }

        return observed;
    }

    static IObjectInputMapper CreateObjectInputMapper(Type type)
    {
        var mapperType = typeof(ObjectInputMapper<>).MakeGenericType(type);
        return (IObjectInputMapper)Activator.CreateInstance(mapperType)!;
    }

    interface IObjectInputMapper
    {
        Observation Map(
            object value,
            ShapeId? schemaId,
            ObjectObservationMetadata? metadata,
            ShapeMappingContext context);
    }

    sealed class ObjectInputMapper<T> : IObjectInputMapper
    {
        public Observation Map(
            object value,
            ShapeId? schemaId,
            ObjectObservationMetadata? metadata,
            ShapeMappingContext context)
        {
            return schemaId is null
                ? context.Map((T)value, metadata)
                : context.Map((T)value, schemaId.Value, metadata);
        }
    }
}
