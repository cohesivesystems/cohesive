using Cohesive.Host.Composition;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <summary>Collects API binding and storage models exposed by public properties on the activated root.</summary>
    /// <param name="services">Host services owning native clients.</param>
    /// <param name="model">Activated application with explicit API and storage contributions.</param>
    /// <returns>The same collection; no native resource is resolved at registration.</returns>
    /// <exception cref="InvalidOperationException">Service bindings are already registered.</exception>
    /// <exception cref="ServiceBindingValidationException">Endpoint or storage ownership is ambiguous or queries lack storage.</exception>
    public static IServiceCollection AddCohesiveServices(this IServiceCollection services, ApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(model);
        if (services.Any(item => item.ServiceType == typeof(ServiceApiRuntime) ||
            item.ServiceType.IsGenericType && item.ServiceType.GetGenericTypeDefinition() == typeof(ServiceApiRuntime<>)))
            throw new InvalidOperationException("Application service bindings must be registered once, without overlapping explicit registrations.");
        var endpoints = model.Components<ApiBindingModel>();
        var storage = new ServiceStorageComposition(model.Components<StorageModel>()
            .Select(item => (item.GetType().FullName!, item.RequireComposition<IServiceStorageComposition>())));
        // Validate the complete surface before installing DI registrations or resolving any clients.
        var ids = new HashSet<Cohesive.Api.ApiEndpointId>();
        foreach (var endpoint in endpoints.SelectMany(item => item.Plan.Definition.Endpoints))
            if (!ids.Add(endpoint.Id))
                throw ServiceImplementationPlan.Error("services.api.duplicateEndpoint", $"Endpoint '{endpoint.Id}' is contributed more than once.");
        storage.Validate(endpoints.Select(item => item.Plan));
        foreach (var endpoint in endpoints) endpoint.Builder.Register(services);
        services.AddLogging();
        services.AddSingleton(provider => new ServiceApiRuntime(provider, endpoints, storage.BindAsync));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ServiceApiRuntime>());
        return services;
    }

    /// <summary>Prepares and atomically maps all API contributions registered from an application.</summary>
    /// <param name="routes">Final native host route builder.</param>
    /// <param name="cancellationToken">Initial preparation cancellation; failure is retained for this host.</param>
    /// <returns>The same route builder.</returns>
    public static async Task<IEndpointRouteBuilder> MapServicesAsync(this IEndpointRouteBuilder routes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var runtime = routes.ServiceProvider.GetRequiredService<ServiceApiRuntime>();
        routes.MapServices(await runtime.PrepareAsync(cancellationToken).ConfigureAwait(false));
        runtime.MarkMapped();
        return routes;
    }

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
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(bindings);
        var group = services.FirstOrDefault(item => item.ServiceType == typeof(ExplicitStorageRegistrations))?.ImplementationInstance
            as ExplicitStorageRegistrations ?? new();
        var entries = group.Entries.Append((Bindings: (ApiBindingModel)bindings, Storage: storage)).ToArray();
        var admission = new ServiceStorageComposition(entries.Select(item =>
            (item.Storage.GetType().FullName!, item.Storage.RequireComposition<IServiceStorageComposition>())));
        admission.Validate(entries.Select(item => item.Bindings.Plan));
        services.AddCohesiveServices(bindings, (provider, token) =>
            new(provider.GetRequiredService<ServiceStorageRuntimeCache>().Prepare(group.Admission!, provider, token)));
        group.Entries = entries;
        group.Admission = admission;
        services.TryAddSingleton(group);
        services.TryAddSingleton<ServiceStorageRuntimeCache>();
        return services;
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
        if (services.Any(item => item.ServiceType == typeof(ServiceApiRuntime<TBindings>) || item.ServiceType == typeof(ServiceApiRuntime)))
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
    Func<IServiceProvider, CancellationToken, ValueTask<IServicePersistence>> prepareStorage)
    : ServiceApiRuntime(provider, [bindings], prepareStorage, $"MapServicesAsync<{typeof(TBindings).Name}>") where TBindings : ApiBindingModel;

internal class ServiceApiRuntime(IServiceProvider provider, IReadOnlyList<ApiBindingModel> bindings,
    Func<IServiceProvider, CancellationToken, ValueTask<IServicePersistence>> prepareStorage, string mappingMethod = "MapServicesAsync") : IHostedService, IAsyncDisposable
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
            return ServiceApiBindings.Combine(bindings.Select(binding => binding.Builder.Build(lifetime, persistence)));
        }
        catch { lifetime.Dispose(); throw; }
    }
    public async Task StartAsync(CancellationToken token)
    {
        lock (gate)
            if (!mapped) throw new InvalidOperationException($"Call {mappingMethod} before starting the host.");
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

internal sealed class ServiceStorageRuntimeCache
{
    readonly Dictionary<IServiceStorageComposition, Task<IServicePersistence>> prepared = new(ReferenceEqualityComparer.Instance);
    public Task<IServicePersistence> Prepare(IServiceStorageComposition composition, IServiceProvider provider, CancellationToken token)
    {
        lock (prepared)
        {
            if (!prepared.TryGetValue(composition, out var result))
                prepared.Add(composition, result = Bind(composition, provider, provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
            return result.WaitAsync(token);
        }
    }
    static async Task<IServicePersistence> Bind(IServiceStorageComposition composition, IServiceProvider provider, CancellationToken token) =>
        await composition.BindAsync(provider, token).ConfigureAwait(false);
}

internal sealed class ExplicitStorageRegistrations
{
    public (ApiBindingModel Bindings, StorageModel Storage)[] Entries = [];
    public ServiceStorageComposition? Admission;
}
