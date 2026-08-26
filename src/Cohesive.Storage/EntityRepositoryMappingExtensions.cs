using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
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
            Action<ObservationMaterializerBuilder<TEntity>>? configureMaterializer = null
            ) where TEntity : notnull
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            var snapshot = await repository.TryGet(context, id, options).ConfigureAwait(false);
            return snapshot is null
                ? default
                : Materialize(snapshot.Entity.Observation, configureMaterializer);
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
            Func<TEntity, string>? selectEntityId = null,
            Func<TEntity, long>? selectVersion = null
            ) where TEntity : notnull
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(entity);
            var state = repository.EntityDefinition.CreateState(
                ResolveEntityId(entity, selectEntityId),
                entity,
                ResolveVersion(entity, selectVersion));
            return repository.Upsert(context, new(Entity: state.Snapshot, ExpectedConcurrencyToken: expectedConcurrencyToken));
        }
    }

    static TEntity Materialize<TEntity>(
        Observation observation,
        Action<ObservationMaterializerBuilder<TEntity>>? configureMaterializer
        ) where TEntity : notnull
    {
        if (configureMaterializer is null)
            return observation.Materialize<TEntity>();

        var builder = ObservationMaterializer.For<TEntity>(observation.ShapeId);
        configureMaterializer(builder);
        return builder.Compile().Materialize(observation);
    }

    static EntitySnapshot<TEntity> Bind<TEntity>(IEntityRepository repository, EntityObservationSnapshot snapshot) where TEntity : Entity, new()
    {
        var entity = new TEntity();
        if (entity.Definition.StateShape.QualifiedId != repository.EntityDefinition.StateShape.QualifiedId)
            throw new InvalidOperationException($"Repository entity '{repository.EntityDefinition.StateShape.QualifiedId}' cannot be materialized as authored entity '{entity.Definition.StateShape.QualifiedId}'.");

        return new(entity, entity.Definition.CreateState(snapshot));
    }

    static string ResolveEntityId<TEntity>(TEntity entity, Func<TEntity, string>? selector) where TEntity : notnull
    {
        if (selector is not null)
            return Guard.RequireNotNullOrWhiteSpace(selector(entity));

        var value = EntityObjectMetadata<TEntity>.IdProperty?.GetValue(entity)
            ?? throw new InvalidOperationException($"Type '{typeof(TEntity).Name}' does not expose an Id or Key property. Supply an explicit entity-id selector.");
        var id = value switch
        {
            EntityId entityId => entityId.Value,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        return Guard.RequireNotNullOrWhiteSpace(id);
    }

    static long ResolveVersion<TEntity>(TEntity entity, Func<TEntity, long>? selector) where TEntity : notnull
    {
        var version = selector is not null
            ? selector(entity)
            : EntityObjectMetadata<TEntity>.VersionProperty?.GetValue(entity) is { } value
                ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
                : 0;
        return version >= 0
            ? version
            : throw new InvalidOperationException("Entity-state version conventions cannot produce a negative value.");
    }

    static class EntityObjectMetadata<TEntity>
    {
        static readonly PropertyInfo[] Properties = typeof(TEntity).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        public static PropertyInfo? IdProperty { get; } = Resolve(["Id", "Key"], ["id", "key"]);

        public static PropertyInfo? VersionProperty { get; } = Resolve(["Version"], ["version", "_version"]);

        static PropertyInfo? Resolve(IReadOnlyList<string> clrNames, IReadOnlyList<string> jsonNames) =>
            Properties.FirstOrDefault(property => property.CanRead && clrNames.Contains(property.Name))
            ?? Properties.FirstOrDefault(property => property.CanRead
                && property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true) is { Name: var name }
                && jsonNames.Contains(name));
    }
}
