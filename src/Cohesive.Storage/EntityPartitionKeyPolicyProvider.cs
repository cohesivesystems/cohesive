using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Storage;

/// <summary>
/// Selects the repository partition-key policy for a semantic entity definition.
/// </summary>
/// <remarks>
/// Selection is separate from policy execution so products can choose placement policies from
/// semantic metadata, deployment configuration, or a future Cohesive.Configuration dependency
/// selection module while repositories keep a concrete <see cref="EntityPartitionKeyPolicy"/>.
/// </remarks>
public interface IEntityPartitionKeyPolicyProvider
{
    /// <summary>
    /// Gets the partition-key policy for the supplied entity definition.
    /// </summary>
    /// <param name="entity">Semantic entity whose repository placement policy is required.</param>
    EntityPartitionKeyPolicy GetPartitionKeyPolicy(EntityDefinition entity);
}

/// <summary>
/// Default partition-key policy provider that uses the observation id as the partition key.
/// </summary>
public sealed class DefaultEntityPartitionKeyPolicyProvider : IEntityPartitionKeyPolicyProvider
{
    /// <summary>
    /// Shared default provider instance.
    /// </summary>
    public static DefaultEntityPartitionKeyPolicyProvider Instance { get; } = new();

    /// <inheritdoc />
    public EntityPartitionKeyPolicy GetPartitionKeyPolicy(EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return EntityPartitionKeyPolicy.ObservationId;
    }
}

/// <summary>
/// Partition-key policy provider backed by a resolver delegate.
/// </summary>
public sealed class DelegatingEntityPartitionKeyPolicyProvider : IEntityPartitionKeyPolicyProvider
{
    readonly Func<EntityDefinition, EntityPartitionKeyPolicy> resolvePartitionKeyPolicy;

    /// <summary>
    /// Creates a provider backed by a resolver delegate.
    /// </summary>
    /// <param name="resolvePartitionKeyPolicy">Function that selects a non-null policy for each entity definition.</param>
    public DelegatingEntityPartitionKeyPolicyProvider(Func<EntityDefinition, EntityPartitionKeyPolicy> resolvePartitionKeyPolicy)
    {
        this.resolvePartitionKeyPolicy = Guard.RequireNotNull(resolvePartitionKeyPolicy);
    }

    /// <inheritdoc />
    public EntityPartitionKeyPolicy GetPartitionKeyPolicy(EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return resolvePartitionKeyPolicy(entity)
               ?? throw new InvalidOperationException($"Partition-key policy provider returned null for entity '{entity.Name}'.");
    }
}

/// <summary>
/// Service registration and resolution helpers for entity partition-key policy providers.
/// </summary>
public static class EntityPartitionKeyPolicyProviderRegistration
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a singleton entity partition-key policy provider.
        /// </summary>
        /// <param name="provider">Provider instance to register.</param>
        public IServiceCollection AddEntityPartitionKeyPolicyProvider(IEntityPartitionKeyPolicyProvider provider)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(provider);
            services.AddSingleton<IEntityPartitionKeyPolicyProvider>(provider);
            return services;
        }

        /// <summary>
        /// Registers a singleton entity partition-key policy provider factory.
        /// </summary>
        /// <param name="providerFactory">Factory used to create the provider from the service provider.</param>
        public IServiceCollection AddEntityPartitionKeyPolicyProvider(Func<IServiceProvider, IEntityPartitionKeyPolicyProvider> providerFactory)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(providerFactory);
            services.AddSingleton<IEntityPartitionKeyPolicyProvider>(providerFactory);
            return services;
        }
    }

    extension(IServiceProvider sp)
    {
        /// <summary>
        /// Gets the configured partition-key policy for the specified entity, or the default policy when no provider is registered.
        /// </summary>
        /// <param name="entity">Authored entity whose repository placement policy is required.</param>
        public EntityPartitionKeyPolicy GetEntityPartitionKeyPolicy(Entity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            return sp.GetEntityPartitionKeyPolicy(entity.Definition);
        }

        /// <summary>
        /// Gets the configured partition-key policy for the specified entity definition, or the default policy when no provider is registered.
        /// </summary>
        /// <param name="entity">Entity definition whose repository placement policy is required.</param>
        public EntityPartitionKeyPolicy GetEntityPartitionKeyPolicy(EntityDefinition entity)
        {
            ArgumentNullException.ThrowIfNull(sp);
            ArgumentNullException.ThrowIfNull(entity);
            var provider = sp.GetService<IEntityPartitionKeyPolicyProvider>() ?? DefaultEntityPartitionKeyPolicyProvider.Instance;
            return provider.GetPartitionKeyPolicy(entity);
        }
    }
}
