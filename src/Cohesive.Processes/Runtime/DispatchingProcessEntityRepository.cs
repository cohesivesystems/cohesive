namespace Cohesive.Processes.Runtime;

/// <summary>
/// Entity repository that dispatches load and commit operations by process entity type.
/// </summary>
public sealed class DispatchingProcessEntityRepository : IProcessEntityRepository
{
    readonly Dictionary<string, IProcessEntityRepository> repositoryByShapeId = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> shapeIdByEntityType = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a repository for the supplied entity definition.
    /// </summary>
    public DispatchingProcessEntityRepository Register(EntityDefinition entity, IProcessEntityRepository repository)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(repository);

        var entityType = entity.Name.Value;
        var shapeId = entity.Shape.Id.Value;
        if (!repositoryByShapeId.TryAdd(shapeId, repository))
            throw new SemanticRuleViolationException($"A process entity repository is already registered for shape id '{shapeId}'.");
        if (!shapeIdByEntityType.TryAdd(entityType, shapeId))
        {
            repositoryByShapeId.Remove(shapeId);
            throw new SemanticRuleViolationException($"A process entity repository is already registered for entity type '{entityType}'.");
        }

        return this;
    }

    /// <inheritdoc />
    public Task<ProcessEntitySnapshot> Create(OperationContext context, ProcessEntityRef entity, EntityState state, string processId) =>
        Resolve(entity).Create(context, entity, state, processId);

    /// <inheritdoc />
    public Task<ProcessEntitySnapshot> Get(OperationContext context, ProcessEntityRef entity, ProcessEntityReadOptions? options = null) =>
        Resolve(entity).Get(context, entity, options);

    /// <inheritdoc />
    public Task Update(
        OperationContext context,
        ProcessEntityRef entity,
        TransitionResult transition,
        string processId,
        ProcessEntityWriteOptions options
        ) =>
        Resolve(entity).Update(context, entity, transition, processId, options);

    IProcessEntityRepository Resolve(ProcessEntityRef entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (repositoryByShapeId.TryGetValue(entity.EntityType, out var repository))
            return repository;
        if (shapeIdByEntityType.TryGetValue(entity.EntityType, out var shapeId)
            && repositoryByShapeId.TryGetValue(shapeId, out repository))
            return repository;
        throw new SemanticRuleViolationException($"No process entity repository is registered for entity type '{entity.EntityType}'.");
    }
}
