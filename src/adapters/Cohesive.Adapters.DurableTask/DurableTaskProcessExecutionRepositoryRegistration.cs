using Cohesive.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Configures the application-facing projections of one canonical Durable Task Process execution repository.
/// </summary>
public sealed class DurableTaskProcessExecutionRepositoryBuilder
{
    readonly IServiceCollection services;

    internal DurableTaskProcessExecutionRepositoryBuilder(IServiceCollection services) =>
        this.services = services;

    /// <summary>
    /// Replaces the default payload-free execution projection with one application-owned decorator while preserving
    /// the underlying Durable Task repository as the sole value and trace authority.
    /// </summary>
    /// <typeparam name="TRepository">Application execution-repository projection type.</typeparam>
    /// <param name="factory">
    /// Factory receiving the service provider and the canonical Durable Task repository being decorated.
    /// </param>
    /// <returns>This builder for composition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="TRepository"/> was already registered.</exception>
    public DurableTaskProcessExecutionRepositoryBuilder DecorateExecutionRepository<TRepository>(
        Func<IServiceProvider, DurableTaskProcessExecutionRepository, TRepository> factory)
        where TRepository : class, IProcessExecutionRepository
    {
        ArgumentNullException.ThrowIfNull(factory);
        RequireAbsent(typeof(TRepository));

        services.AddSingleton<TRepository>(sp =>
            factory(sp, sp.GetRequiredService<DurableTaskProcessExecutionRepository>())
            ?? throw new InvalidOperationException(
                $"The Durable Task Process execution repository decorator factory returned null for '{typeof(TRepository).FullName}'."));
        services.Replace(ServiceDescriptor.Singleton<IProcessExecutionRepository>(static sp =>
            sp.GetRequiredService<TRepository>()));
        return this;
    }

    /// <summary>
    /// Adds canonical execution explanations composed from the repository's retained evidence and one exact deployed
    /// Process plan catalog.
    /// </summary>
    /// <param name="planCatalogFactory">Factory for the immutable exact plan catalog deployed to the worker.</param>
    /// <returns>This builder for composition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planCatalogFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An explanation repository is already registered.</exception>
    public DurableTaskProcessExecutionRepositoryBuilder AddExecutionExplainRepository(
        Func<IServiceProvider, DurableTaskSequentialProcessPlanCatalog> planCatalogFactory)
    {
        ArgumentNullException.ThrowIfNull(planCatalogFactory);
        RequireAbsent(typeof(DurableTaskProcessExecutionExplainRepository));
        RequireAbsent(typeof(IProcessExecutionExplainRepository));

        services.AddSingleton(sp => new DurableTaskProcessExecutionExplainRepository(
            sp.GetRequiredService<DurableTaskProcessExecutionRepository>(),
            planCatalogFactory(sp)
            ?? throw new InvalidOperationException(
                "The Durable Task Process execution explain plan-catalog factory returned null.")));
        services.AddSingleton<IProcessExecutionExplainRepository>(static sp =>
            sp.GetRequiredService<DurableTaskProcessExecutionExplainRepository>());
        return this;
    }

    void RequireAbsent(Type serviceType)
    {
        if (services.Any(descriptor => descriptor.ServiceType == serviceType))
        {
            throw new InvalidOperationException(
                $"Service '{serviceType.FullName}' is already registered; Durable Task Process execution composition requires one authority.");
        }
    }
}

/// <summary>Registers canonical Durable Task Process execution repository capabilities.</summary>
public static class DurableTaskProcessExecutionRepositoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers one current Durable Task repository as the payload-free execution authority and the opt-in canonical
    /// value and trace authority.
    /// </summary>
    /// <param name="services">Application service collection.</param>
    /// <param name="repositoryFactory">Factory for the repository bound to one physical task-hub deployment.</param>
    /// <returns>A builder for application execution decoration and exact-plan explanation composition.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Any repository capability already has a registered authority.</exception>
    public static DurableTaskProcessExecutionRepositoryBuilder AddCohesiveDurableTaskProcessExecutionRepository(
        this IServiceCollection services,
        Func<IServiceProvider, DurableTaskProcessExecutionRepository> repositoryFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        RequireAbsent(services, typeof(DurableTaskProcessExecutionRepository));
        RequireAbsent(services, typeof(IProcessExecutionRepository));
        RequireAbsent(services, typeof(IProcessExecutionValueRepository));
        RequireAbsent(services, typeof(IProcessExecutionTraceRepository));

        services.AddSingleton(sp =>
            repositoryFactory(sp)
            ?? throw new InvalidOperationException(
                "The Durable Task Process execution repository factory returned null."));
        services.AddSingleton<IProcessExecutionRepository>(static sp =>
            sp.GetRequiredService<DurableTaskProcessExecutionRepository>());
        services.AddSingleton<IProcessExecutionValueRepository>(static sp =>
            sp.GetRequiredService<DurableTaskProcessExecutionRepository>());
        services.AddSingleton<IProcessExecutionTraceRepository>(static sp =>
            sp.GetRequiredService<DurableTaskProcessExecutionRepository>());

        return new(services);
    }

    static void RequireAbsent(IServiceCollection services, Type serviceType)
    {
        if (services.Any(descriptor => descriptor.ServiceType == serviceType))
        {
            throw new InvalidOperationException(
                $"Service '{serviceType.FullName}' is already registered; Durable Task Process execution composition requires one authority.");
        }
    }
}
