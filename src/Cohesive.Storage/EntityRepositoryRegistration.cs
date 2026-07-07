using Cohesive.Relations.Mapping;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Storage;

/// <summary>
/// Registration and resolution helpers for entity repositories.
/// </summary>
public static class EntityRepositoryRegistration
{
    static object ShapeServiceKey(EntityDefinition entity) => entity.Shape.Id.Value;

    static object TypeServiceKey<TEntity>() where TEntity : notnull => typeof(TEntity);

    static object TypeServiceKey(Type clrType) => clrType;

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers an entity repository for the specified CLR object type.
        /// </summary>
        public void RegisterEntityRepository<TEntity>(
            Func<IServiceProvider, EntityDefinition, IEntityRepository> repositoryFactory,
            Action<ObjectObservationMapperBuilder<TEntity>>? configureObjectMapper = null,
            ShapeMappingContext? mappingContext = null)
            where TEntity : notnull
        {
            ArgumentNullException.ThrowIfNull(repositoryFactory);

            var entity = ObjectEntityDefinition.For<TEntity>();
            var typeKey = TypeServiceKey<TEntity>();
            RegisterEntityRepositoryCore(
                services,
                entity,
                repositoryFactory: (sp, _) => repositoryFactory(sp, entity),
                additionalKeys: [typeKey]);
            RegisterTypedRepositories(services, typeKey, configureObjectMapper, mappingContext);
        }

        /// <summary>
        /// Registers an entity repository for the specified entity definition.
        /// </summary>
        public void RegisterEntityRepository(EntityDefinition entity, Func<IServiceProvider, object?, IEntityRepository> repositoryFactory)
        {
            ArgumentNullException.ThrowIfNull(entity);
            ArgumentNullException.ThrowIfNull(repositoryFactory);
            RegisterEntityRepositoryCore(services, entity, repositoryFactory, additionalKeys: []);
        }

        /// <summary>
        /// Registers an entity repository for the specified authored entity type.
        /// </summary>
        public void RegisterEntityRepository(Entity entity, Func<IServiceProvider, object?, IEntityRepository> repositoryFactory)
        {
            ArgumentNullException.ThrowIfNull(entity);
            ArgumentNullException.ThrowIfNull(repositoryFactory);
            RegisterEntityRepositoryCore(
                services,
                entity.Definition,
                repositoryFactory,
                additionalKeys: [TypeServiceKey(entity.GetType())]);
        }
    }

    extension(IServiceProvider sp)
    {
        /// <summary>
        /// Gets the entity repository for the specified entity.
        /// </summary>
        public IEntityRepository GetEntityRepository(Entity entity) => sp.GetEntityRepository(entity.Definition);

        /// <summary>
        /// Gets the entity repository for the specified entity definition.
        /// </summary>
        public IEntityRepository GetEntityRepository(EntityDefinition entity) =>
            sp.GetRequiredKeyedService<IEntityRepository>(serviceKey: ShapeServiceKey(entity));

        /// <summary>
        /// Gets the entity repository for the specified registered CLR type.
        /// </summary>
        public IEntityRepository GetEntityRepository<TEntity>() where TEntity : notnull =>
            sp.GetRequiredKeyedService<IEntityRepository>(serviceKey: TypeServiceKey<TEntity>());

        /// <summary>
        /// Gets the query repository for the specified entity definition.
        /// </summary>
        public IEntityQueryRepository GetEntityQueryRepository(EntityDefinition entity) =>
            sp.GetRequiredKeyedService<IEntityQueryRepository>(serviceKey: ShapeServiceKey(entity));

        /// <summary>
        /// Gets the query repository for the specified entity.
        /// </summary>
        public IEntityQueryRepository GetEntityQueryRepository(Entity entity) =>
            sp.GetEntityQueryRepository(entity.Definition);

        /// <summary>
        /// Gets the query repository for the specified registered CLR type.
        /// </summary>
        public IEntityQueryRepository GetEntityQueryRepository<TEntity>() where TEntity : notnull =>
            sp.GetRequiredKeyedService<IEntityQueryRepository>(serviceKey: TypeServiceKey<TEntity>());

        /// <summary>
        /// Gets the outbox repository for the specified entity definition.
        /// </summary>
        public IEntityOutboxRepository GetEntityOutboxRepository(EntityDefinition entity) =>
            sp.GetRequiredKeyedService<IEntityOutboxRepository>(serviceKey: ShapeServiceKey(entity));

        /// <summary>
        /// Gets the outbox repository for the specified registered CLR type.
        /// </summary>
        public IEntityOutboxRepository GetEntityOutboxRepository<TEntity>() where TEntity : notnull =>
            sp.GetRequiredKeyedService<IEntityOutboxRepository>(serviceKey: TypeServiceKey<TEntity>());

        /// <summary>
        /// Gets the strongly typed entity repository for the specified CLR object type.
        /// </summary>
        public IEntityRepository<TEntity> GetTypedEntityRepository<TEntity>() where TEntity : notnull =>
            sp.GetRequiredService<IEntityRepository<TEntity>>();

        /// <summary>
        /// Gets the strongly typed query repository for the specified CLR object type.
        /// </summary>
        public IEntityQueryRepository<TEntity> GetTypedEntityQueryRepository<TEntity>() where TEntity : notnull =>
            sp.GetRequiredService<IEntityQueryRepository<TEntity>>();

        /// <summary>
        /// Gets the strongly typed outbox repository for the specified CLR object type.
        /// </summary>
        public IEntityOutboxRepository<TEntity> GetTypedEntityOutboxRepository<TEntity>() where TEntity : notnull =>
            sp.GetRequiredService<IEntityOutboxRepository<TEntity>>();
    }

    static void RegisterEntityRepositoryCore(
        IServiceCollection services,
        EntityDefinition entity,
        Func<IServiceProvider, object?, IEntityRepository> repositoryFactory,
        IReadOnlyCollection<object> additionalKeys)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var keys = new HashSet<object> { ShapeServiceKey(entity) };
        foreach (var additionalKey in additionalKeys)
            keys.Add(additionalKey);

        var shapeKey = ShapeServiceKey(entity);
        services.AddKeyedSingleton<IEntityRepository>(shapeKey, repositoryFactory);

        foreach (var key in keys.Where(key => !Equals(key, shapeKey)))
        {
            services.AddKeyedSingleton<IEntityRepository>(
                key,
                (sp, _) => sp.GetRequiredKeyedService<IEntityRepository>(shapeKey));
        }

        RegisterDerivedRepositories(services, keys);
    }

    static void RegisterTypedRepositories<TEntity>(
        IServiceCollection services,
        object serviceKey,
        Action<ObjectObservationMapperBuilder<TEntity>>? configureObjectMapper,
        ShapeMappingContext? mappingContext)
        where TEntity : notnull
    {
        services.AddSingleton<IEntityRepository<TEntity>>(sp => new TypedEntityRepository<TEntity>(
            repository: sp.GetRequiredKeyedService<IEntityRepository>(serviceKey),
            configureObjectMapper: configureObjectMapper,
            mappingContext: mappingContext
            ));
        services.AddSingleton<IEntityQueryRepository<TEntity>>(sp => new TypedEntityQueryRepository<TEntity>(
            repository: sp.GetRequiredService<IEntityRepository<TEntity>>(),
            queryRepository: sp.GetRequiredKeyedService<IEntityQueryRepository>(serviceKey)
            ));
        services.AddSingleton<IEntityOutboxRepository<TEntity>>(sp => new TypedEntityOutboxRepository<TEntity>(
            repository: sp.GetRequiredService<IEntityRepository<TEntity>>(),
            outboxRepository: sp.GetRequiredKeyedService<IEntityOutboxRepository>(serviceKey)
            ));
    }

    static void RegisterDerivedRepositories(IServiceCollection services, IEnumerable<object> serviceKeys)
    {
        foreach (var serviceKey in serviceKeys)
        {
            services.AddKeyedSingleton<IEntityQueryRepository>(
                serviceKey,
                (sp, key) => sp.GetRequiredKeyedService<IEntityRepository>(key) as IEntityQueryRepository
                    ?? throw new InvalidOperationException($"Repository registered for entity '{key}' does not implement '{nameof(IEntityQueryRepository)}'.")
                );
            services.AddKeyedSingleton<IEntityOutboxRepository>(
                serviceKey,
                (sp, key) => sp.GetRequiredKeyedService<IEntityRepository>(key) as IEntityOutboxRepository
                    ?? throw new InvalidOperationException($"Repository registered for entity '{key}' does not implement '{nameof(IEntityOutboxRepository)}'.")
                );
        }
    }
}
