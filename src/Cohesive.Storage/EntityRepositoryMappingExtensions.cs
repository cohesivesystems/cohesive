using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;

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
            return repository.Upsert(context, CreateWriteRequest(repository, entity, expectedConcurrencyToken, selectEntityId, selectVersion));
        }

        /// <summary>Maps an ordered typed batch and dispatches it through the repository's native batch contract.</summary>
        /// <param name="context">Operation context and cancellation.</param>
        /// <param name="entities">Complete typed candidates in write order.</param>
        /// <param name="atomicity">Required atomicity, preserved when dispatching the canonical batch.</param>
        /// <param name="selectEntityId">Optional identity selector; otherwise use existing object-mapping conventions.</param>
        /// <param name="selectVersion">Optional semantic version selector; otherwise use existing object-mapping conventions.</param>
        /// <returns>The native batch result, including ordered snapshots and realized atomicity.</returns>
        /// <exception cref="ArgumentNullException">A required argument or typed candidate is null.</exception>
        /// <exception cref="OperationCanceledException">Cancellation is observed during mapping or by the underlying repository.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The atomicity enum is unknown.</exception>
        /// <exception cref="NotSupportedException">The repository cannot satisfy the requested batch contract.</exception>
        /// <exception cref="SemanticRuleViolationException">A candidate does not satisfy the entity definition.</exception>
        /// <exception cref="InvalidOperationException">Object-mapping conventions cannot resolve a valid identity or version.</exception>
        public Task<EntityBatchWriteResult> UpsertBatch<TEntity>(OperationContext context,
            IReadOnlyList<TEntity> entities,
            EntityBatchAtomicity atomicity = EntityBatchAtomicity.None,
            Func<TEntity, string>? selectEntityId = null,
            Func<TEntity, long>? selectVersion = null) where TEntity : notnull
            => MapBatch(repository, context, entities, atomicity,
                entity => CreateWriteRequest(repository, entity, expectedConcurrencyToken: null, selectEntityId, selectVersion));

        /// <summary>Maps typed candidates and their per-write concurrency fences into one canonical batch.</summary>
        /// <typeparam name="TEntity">CLR candidate type.</typeparam>
        /// <param name="context">Operation context and cancellation.</param>
        /// <param name="request">Typed writes, per-write storage tokens, and required native atomicity.</param>
        /// <param name="selectEntityId">Optional identity selector.</param>
        /// <param name="selectVersion">Optional semantic-version selector, independent of storage tokens.</param>
        /// <returns>The native batch result in input order.</returns>
        /// <exception cref="ArgumentNullException">A required argument, write, or candidate is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The atomicity enum is unknown.</exception>
        /// <exception cref="NotSupportedException">The requested batch guarantees or item count are unsupported.</exception>
        /// <exception cref="SemanticRuleViolationException">A candidate violates the entity definition.</exception>
        /// <exception cref="InvalidOperationException">Mapping cannot resolve a valid identity or version.</exception>
        /// <exception cref="ObservationConcurrencyConflictException">An expected token is stale or its target is absent.</exception>
        /// <exception cref="OperationCanceledException">Cancellation is observed during mapping or by the repository.</exception>
        public Task<EntityBatchWriteResult> UpsertBatch<TEntity>(OperationContext context,
            EntityBatchWriteRequest<TEntity> request,
            Func<TEntity, string>? selectEntityId = null,
            Func<TEntity, long>? selectVersion = null) where TEntity : notnull
        {
            ArgumentNullException.ThrowIfNull(request);
            return MapBatch(repository, context, request.Writes, request.Atomicity, write =>
            {
                ArgumentNullException.ThrowIfNull(write);
                return CreateWriteRequest(repository, write.Entity, write.ExpectedConcurrencyToken, selectEntityId, selectVersion);
            });
        }
    }

    static Task<EntityBatchWriteResult> MapBatch<TItem>(IEntityRepository repository, OperationContext context,
        IReadOnlyList<TItem> items, EntityBatchAtomicity atomicity, Func<TItem, EntityWriteRequest> map)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(items);
        context.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(atomicity))
            throw new ArgumentOutOfRangeException(nameof(atomicity), atomicity, "Unknown entity batch atomicity.");
        var capabilities = repository.BatchCapabilities;
        if (!capabilities.SupportsAtomicity(atomicity)
            || capabilities.MaxItemsPerBatch is { } maximum && items.Count > maximum)
            throw new NotSupportedException($"Repository '{repository.EntityType}' cannot satisfy the requested batch atomicity or item limit.");
        var writes = new EntityWriteRequest[items.Count];
        for (var index = 0; index < writes.Length; index++)
        {
            context.ThrowIfCancellationRequested();
            writes[index] = map(items[index]);
        }
        return repository.UpsertBatch(context, new EntityBatchWriteRequest(writes, atomicity));
    }

    static EntityWriteRequest CreateWriteRequest<TEntity>(IEntityRepository repository, TEntity entity,
        EntityConcurrencyToken? expectedConcurrencyToken, Func<TEntity, string>? selectEntityId,
        Func<TEntity, long>? selectVersion) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(entity);
        var state = repository.EntityDefinition.CreateState(ResolveEntityId(entity, selectEntityId), entity, ResolveVersion(entity, selectVersion));
        return new(Entity: state.Snapshot, ExpectedConcurrencyToken: expectedConcurrencyToken);
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
