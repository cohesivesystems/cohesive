namespace Cohesive.Processes.Runtime;

/// <summary>
/// Entity transition decision host used by process runtime.
/// </summary>
public interface IProcessTransitionHost
{
    /// <summary>
    /// Executes entity transition decision semantics against the supplied entity state snapshot.
    /// </summary>
    Task<TransitionResult> DecideAsync(OperationContext context, ProcessEntityRef entity, EntityState state, long version, string transitionName, IReadOnlyDictionary<string, ObservationValue> input);
}

/// <summary>
/// Declarative transition-host adapter backed by entity definitions.
/// </summary>
public sealed class DeclarativeTransitionHost : IProcessTransitionHost
{
    readonly Dictionary<string, DeclarativeEntityRuntime> runtimeByEntityType = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers an entity definition.
    /// </summary>
    public DeclarativeTransitionHost Register(EntityDefinition entityDefinition)
    {
        ArgumentNullException.ThrowIfNull(entityDefinition);
        return Register(entityType: entityDefinition.Name.Value, runtime: new(entityDefinition));
    }

    /// <summary>
    /// Registers a runtime for a specific entity type.
    /// </summary>
    public DeclarativeTransitionHost Register(string entityType, DeclarativeEntityRuntime runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(runtime);

        if (!runtimeByEntityType.TryAdd(entityType, runtime))
            throw new SemanticRuleViolationException($"A transition runtime is already registered for entity type '{entityType}'.");

        return this;
    }

    /// <inheritdoc />
    public Task<TransitionResult> DecideAsync(
        OperationContext context,
        ProcessEntityRef entity,
        EntityState state,
        long version,
        string transitionName,
        IReadOnlyDictionary<string, ObservationValue> input
        )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionName);
        ArgumentNullException.ThrowIfNull(input);
        context.ThrowIfCancellationRequested();

        if (!runtimeByEntityType.TryGetValue(entity.EntityType, out var runtime))
            throw new SemanticRuleViolationException($"No transition runtime is registered for entity type '{entity.EntityType}'.");

        return Task.FromResult(runtime.Apply(
            entityId: entity.EntityId,
            state: state,
            version: version,
            transitionName: transitionName,
            input: ObservationValue.FromObject(input)
            )
        );
    }
}
