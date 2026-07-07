using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Relations.Queries;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Storage;

/// <summary>
/// Object-mapping helpers over raw entity repositories.
/// </summary>
public static class EntityRepositoryMappingExtensions
{
    extension(IEntityRepository repository)
    {
        /// <summary>
        /// Attempts to load one persisted entity and materialize it as <typeparamref name="TEntity"/>.
        /// </summary>
        public async Task<TEntity?> TryGetEntity<TEntity>(OperationContext context,
            string id,
            EntityReadOptions? options = null,
            Action<ObservationObjectMapperBuilder<TEntity>>? configureObjectMapper = null,
            ShapeMappingContext? mappingContext = null
            ) where TEntity : notnull
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            var snapshot = await repository.TryGet(context, id, options).ConfigureAwait(false);
            return snapshot is null
                ? default
                : Materialize(snapshot.Entity, repository, configureObjectMapper, mappingContext);
        }

        /// <summary>
        /// Attempts to load one persisted entity and bind it to an authored semantic entity type.
        /// </summary>
        public async Task<EntitySnapshot<TEntity>?> TryGetSnapshot<TEntity>(OperationContext context,
            string id,
            EntityReadOptions? options = null
            ) where TEntity : Entity, new()
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            var snapshot = await repository.TryGet(context, id, options).ConfigureAwait(false);
            return snapshot is null ? null : Bind<TEntity>(repository, snapshot.Entity);
        }

        /// <summary>
        /// Maps an entity value into the repository's semantic entity definition and upserts it.
        /// </summary>
        public Task<EntitySnapshot> Upsert<TEntity>(OperationContext context,
            TEntity entity,
            EntityConcurrencyToken? expectedConcurrencyToken = null,
            Action<ObjectObservationMapperBuilder<TEntity>>? configureObjectMapper = null,
            ShapeMappingContext? mappingContext = null
            ) where TEntity : notnull
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(entity);
            var observation = CreateObservation(repository, entity, configureObjectMapper, mappingContext);
            var state = repository.EntityDefinition.CreateState(observation);
            return repository.Upsert(context, new(Entity: state.Observation, ExpectedConcurrencyToken: expectedConcurrencyToken));
        }
    }

    /// <summary>
    /// Streams row results from a materialized entity query response.
    /// </summary>
    public static async IAsyncEnumerable<EntitySnapshot> QueryStream(
        this IEntityQueryRepository repository,
        OperationContext context,
        EntityQuery query
        )
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);

        var response = await repository.Query(context, query).ConfigureAwait(false);
        foreach (var snapshot in response.Rows)
            yield return snapshot;
    }

    /// <summary>
    /// Returns entity values matching the given query.
    /// </summary>
    public static async IAsyncEnumerable<TEntity> QueryEntities<TEntity>(
        this IEntityQueryRepository repository,
        OperationContext context,
        EntityQuery query,
        Action<ObservationObjectMapperBuilder<TEntity>>? configureObjectMapper = null,
        ShapeMappingContext? mappingContext = null
        ) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);

        await foreach (var snapshot in repository.QueryStream(context, query).WithCancellation(context.CancellationToken))
            yield return Materialize(snapshot.Entity, repository, configureObjectMapper, mappingContext);
    }

    static Observation CreateObservation<TEntity>(
        IEntityRepository repository,
        TEntity entity,
        Action<ObjectObservationMapperBuilder<TEntity>>? configureObjectMapper,
        ShapeMappingContext? mappingContext
        ) where TEntity : notnull
    {
        var effectiveMappingContext = mappingContext ?? repository.MappingContext;
        var builder = effectiveMappingContext.ForObjectObservation<TEntity>(repository.EntityDefinition.Shape.Id);
        configureObjectMapper?.Invoke(builder);
        return builder.Build().Map(entity);
    }

    static TEntity Materialize<TEntity>(
        Observation observation,
        IEntityRepository repository,
        Action<ObservationObjectMapperBuilder<TEntity>>? configureObjectMapper,
        ShapeMappingContext? mappingContext
        ) where TEntity : notnull
    {
        var effectiveMappingContext = mappingContext ?? repository.MappingContext;
        return configureObjectMapper is null ? observation.Map<TEntity>(effectiveMappingContext) : effectiveMappingContext.Map(observation, configureObjectMapper);
    }

    static EntitySnapshot<TEntity> Bind<TEntity>(IEntityRepository repository, Observation observation) where TEntity : Entity, new()
    {
        var entity = new TEntity();
        if (!string.Equals(entity.Definition.Shape.Id.Value, repository.EntityDefinition.Shape.Id.Value, StringComparison.Ordinal))
            throw new InvalidOperationException($"Repository entity '{repository.EntityDefinition.Shape.Id.Value}' cannot be materialized as authored entity '{entity.Definition.Shape.Id.Value}'.");

        return new(entity, entity.Definition.CreateState(observation));
    }
}
