using Cohesive.Api.Execution.Services;
using Cohesive.Host.Services;
using Cohesive.Storage;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.AspNet.Services;

/// <summary>Prepares model-owned storage and API bindings from the application's existing DI container.</summary>
public static class ServiceApiDependencyInjection
{
    /// <summary>Registers one API model using its storage model's native composition.</summary>
    /// <typeparam name="TBindings">API binding model type, unique within this host.</typeparam>
    /// <param name="services">Host services, including keyed native clients.</param>
    /// <param name="bindings">Offline API binding model.</param><param name="storage">Offline storage composition.</param>
    /// <returns>The original service collection.</returns>
    /// <remarks>No resources are resolved during registration. Preparation follows the storage model’s explicit validation or creation policy.</remarks>
    public static IServiceCollection AddCohesiveServices<TBindings>(this IServiceCollection services,
        TBindings bindings, StorageModel storage) where TBindings : ApiBindingModel
    {
        ArgumentNullException.ThrowIfNull(storage);
        var composition = storage.RequireComposition<IServiceStorageComposition>();
        return services.AddCohesiveServices(bindings, composition.BindAsync);
    }

    /// <summary>Registers asynchronous native initialization and preparation with explicit application policy.</summary>
    /// <typeparam name="TBindings">API binding model type, unique within this host.</typeparam>
    /// <param name="services">Owning host services.</param><param name="bindings">Offline API binding model.</param>
    /// <param name="prepareStorage">Once-per-host preparation borrowing clients from this provider. Any schema mutation must be explicit here.</param>
    /// <returns>The original service collection.</returns>
    /// <exception cref="InvalidOperationException">This binding type is already registered.</exception>
    public static IServiceCollection AddCohesiveServices<TBindings>(this IServiceCollection services,
        TBindings bindings, Func<IServiceProvider, CancellationToken, ValueTask<IServicePersistence>> prepareStorage)
        where TBindings : ApiBindingModel
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(bindings); ArgumentNullException.ThrowIfNull(prepareStorage);
        if (services.Any(item => item.ServiceType == typeof(ServiceApiRuntime<TBindings>)))
            throw new InvalidOperationException("This API binding model is already registered in the host.");
        bindings.Builder.Register(services);
        services.AddLogging();
        services.AddSingleton(provider => new ServiceApiRuntime<TBindings>(provider, bindings, prepareStorage));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ServiceApiRuntime<TBindings>>());
        return services;
    }

    /// <summary>Prepares once and maps the API before the host starts accepting traffic.</summary>
    /// <typeparam name="TBindings">Registered API binding model.</typeparam>
    /// <param name="routes">Native route builder with the final host provider.</param>
    /// <param name="cancellationToken">Cancels the initial preparation. Failures are retained for this host; rebuild to retry.</param>
    /// <returns>The original route builder after complete mapping.</returns>
    public static async Task<IEndpointRouteBuilder> MapServicesAsync<TBindings>(this IEndpointRouteBuilder routes,
        CancellationToken cancellationToken = default) where TBindings : ApiBindingModel
    {
        ArgumentNullException.ThrowIfNull(routes);
        var runtime = routes.ServiceProvider.GetRequiredService<ServiceApiRuntime<TBindings>>();
        routes.MapServices(await runtime.PrepareAsync(cancellationToken).ConfigureAwait(false));
        runtime.MarkMapped();
        return routes;
    }
}

internal sealed class ServiceApiRuntime<TBindings>(IServiceProvider provider, TBindings bindings,
    Func<IServiceProvider, CancellationToken, ValueTask<IServicePersistence>> prepareStorage) : IHostedService, IAsyncDisposable
    where TBindings : ApiBindingModel
{
    readonly object gate = new();
    readonly ServiceProcessLifetime lifetime = new(provider.GetRequiredService<ILoggerFactory>());
    Task<ServiceApiBindings>? preparation;
    bool disposed;
    bool mapped;
    public void MarkMapped() { lock (gate) mapped = true; }
    public Task<ServiceApiBindings> PrepareAsync(CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return preparation ??= PrepareCore(token);
        }
    }
    async Task<ServiceApiBindings> PrepareCore(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var persistence = await prepareStorage(provider, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return bindings.Builder.Build(lifetime, persistence);
        }
        catch { lifetime.Dispose(); throw; }
    }
    public async Task StartAsync(CancellationToken token)
    {
        lock (gate)
            if (!mapped) throw new InvalidOperationException($"Call MapServicesAsync<{typeof(TBindings).Name}> before starting the host.");
        await PrepareAsync(token).ConfigureAwait(false);
        await lifetime.StartAsync(token).ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken token) => lifetime.StopAsync(token);
    public async ValueTask DisposeAsync()
    {
        Task<ServiceApiBindings>? pending;
        lock (gate) { if (disposed) return; disposed = true; pending = preparation; }
        if (pending is not null) { try { await pending.ConfigureAwait(false); } catch { /* Preparation already owns its failure; release only. */ } }
        lifetime.Dispose();
    }
}
