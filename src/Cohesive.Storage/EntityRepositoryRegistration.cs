using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        /// <summary>Registers one immutable entity-backed canonical relation/query source.</summary>
        /// <param name="registration">Exact source, reader, shape, selector, capability, and limit registration.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registration"/> or the target service collection is <see langword="null"/>.
        /// </exception>
        public void RegisterEntityRelationQuerySource(EntityRelationQuerySourceRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(registration);
            services.AddSingleton(registration);
            EnsureEntityRelationQuerySourceCatalog(services);
        }

        /// <summary>Registers a dependency-injection factory for one entity-backed canonical relation/query source.</summary>
        /// <param name="registrationFactory">Factory producing the immutable source registration.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registrationFactory"/> or the target service collection is <see langword="null"/>.
        /// </exception>
        public void RegisterEntityRelationQuerySource(
            Func<IServiceProvider, EntityRelationQuerySourceRegistration> registrationFactory)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(registrationFactory);
            services.AddSingleton(registrationFactory);
            EnsureEntityRelationQuerySourceCatalog(services);
        }

        /// <summary>Registers the canonical evaluator over the immutable entity-source catalog.</summary>
        /// <param name="physicalPlanningPolicy">Explicit bounded physical-planning policy.</param>
        /// <param name="interpreter">Canonical interpreter, or <see langword="null"/> for the shared default.</param>
        /// <param name="requirementGapPolicy">
        /// Runtime requirement-gap policy, or <see langword="null"/> for the conventional policy.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="physicalPlanningPolicy"/> or the target service collection is <see langword="null"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// An <see cref="IRelationQueryEvaluator"/> registration already exists. Evaluator gateway precedence must
        /// be selected explicitly rather than relying on container registration order.
        /// </exception>
        public void RegisterEntityRelationQueryEvaluator(
            RelationQueryPhysicalPlanningPolicy physicalPlanningPolicy,
            IRelationQueryInterpreter? interpreter = null,
            IRelationRequirementGapPolicy? requirementGapPolicy = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(physicalPlanningPolicy);
            if (services.Any(static descriptor => descriptor.ServiceType == typeof(IRelationQueryEvaluator)))
            {
                throw new InvalidOperationException(
                    "An IRelationQueryEvaluator registration already exists; select one canonical evaluator gateway explicitly.");
            }
            EnsureEntityRelationQuerySourceCatalog(services);
            services.AddSingleton<IRelationQueryEvaluator>(provider =>
                provider.GetRequiredService<EntityRelationQuerySourceCatalog>().CreateEvaluator(
                    physicalPlanningPolicy,
                    interpreter,
                    requirementGapPolicy));
        }

        /// <summary>
        /// Registers an entity repository for the specified CLR object type.
        /// </summary>
        public void RegisterEntityRepository<TEntity>(
            Func<IServiceProvider, EntityDefinition, IEntityRepository> repositoryFactory,
            Func<TEntity, string>? selectEntityId = null,
            Func<TEntity, long>? selectVersion = null,
            Action<ObservationMaterializerBuilder<TEntity>>? configureMaterializer = null)
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
            RegisterTypedRepositories(
                services,
                typeKey,
                selectEntityId,
                selectVersion,
                configureMaterializer);
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
        /// <summary>Gets the immutable catalog of explicitly registered canonical entity sources.</summary>
        /// <returns>The singleton source catalog.</returns>
        /// <exception cref="ArgumentNullException">The service provider is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// No catalog is registered, or registered source factories produce an invalid catalog snapshot.
        /// </exception>
        public EntityRelationQuerySourceCatalog GetEntityRelationQuerySourceCatalog()
        {
            ArgumentNullException.ThrowIfNull(sp);
            return sp.GetRequiredService<EntityRelationQuerySourceCatalog>();
        }

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

        RegisterOutboxRepositories(services, keys);
    }

    static void EnsureEntityRelationQuerySourceCatalog(IServiceCollection services) =>
        services.TryAddSingleton(static provider => new EntityRelationQuerySourceCatalog(
            provider.GetServices<EntityRelationQuerySourceRegistration>()));

    static void RegisterTypedRepositories<TEntity>(
        IServiceCollection services,
        object serviceKey,
        Func<TEntity, string>? selectEntityId,
        Func<TEntity, long>? selectVersion,
        Action<ObservationMaterializerBuilder<TEntity>>? configureMaterializer)
        where TEntity : notnull
    {
        services.AddSingleton<IEntityRepository<TEntity>>(sp => new TypedEntityRepository<TEntity>(
            repository: sp.GetRequiredKeyedService<IEntityRepository>(serviceKey),
            selectEntityId: selectEntityId,
            selectVersion: selectVersion,
            configureMaterializer: configureMaterializer
            ));
        services.AddSingleton<IEntityOutboxRepository<TEntity>>(sp => new TypedEntityOutboxRepository<TEntity>(
            repository: sp.GetRequiredService<IEntityRepository<TEntity>>(),
            outboxRepository: sp.GetRequiredKeyedService<IEntityOutboxRepository>(serviceKey)
            ));
    }

    static void RegisterOutboxRepositories(IServiceCollection services, IEnumerable<object> serviceKeys)
    {
        foreach (var serviceKey in serviceKeys)
        {
            services.AddKeyedSingleton<IEntityOutboxRepository>(
                serviceKey,
                (sp, key) => sp.GetRequiredKeyedService<IEntityRepository>(key) as IEntityOutboxRepository
                    ?? throw new InvalidOperationException($"Repository registered for entity '{key}' does not implement '{nameof(IEntityOutboxRepository)}'.")
                );
        }
    }
}
