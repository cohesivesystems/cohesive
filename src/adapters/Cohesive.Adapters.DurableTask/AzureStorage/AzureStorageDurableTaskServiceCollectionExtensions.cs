using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.DurableTask.AzureStorage;

/// <summary>
/// Registration helpers for Azure Storage-backed durable workers.
/// </summary>
public static class AzureStorageDurableTaskServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a keyed Azure Storage-backed durable host.
        /// </summary>
        public IServiceCollection AddAzureStorageDurableTaskHost(object serviceKey, Action<AzureStorageDurableTaskWorkerBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(serviceKey);
            ArgumentNullException.ThrowIfNull(configure);

            var builder = new AzureStorageDurableTaskWorkerBuilder();
            configure(builder);
            if (builder.HostName is null)
                builder.WithHostName(hostName: serviceKey.ToString() ?? "");

            services.AddKeyedSingleton<DurableTaskProcessHost>(serviceKey: serviceKey, (sp, _) => builder.Build(sp));
            return services;
        }

        /// <summary>
        /// Registers a keyed Azure Storage-backed durable worker and hosted-service wrapper.
        /// </summary>
        public IServiceCollection AddAzureStorageDurableTaskWorker(object serviceKey, Action<AzureStorageDurableTaskWorkerBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(serviceKey);
            ArgumentNullException.ThrowIfNull(configure);
            services.AddAzureStorageDurableTaskHost(serviceKey: serviceKey, configure);
            services.AddKeyedSingleton<IProcessExecutionRepository>(serviceKey, static (sp, key) => sp.GetRequiredKeyedService<DurableTaskProcessHost>(key).ProcessExecutionRepository);
            services.AddSingleton<IHostedService>(sp => new AzureStorageDurableTaskWorkerHostedService(
                host: sp.GetRequiredKeyedService<DurableTaskProcessHost>(serviceKey: serviceKey),
                logger: sp.GetRequiredService<ILogger<AzureStorageDurableTaskWorkerHostedService>>()
            ));
            return services;
        }

        /// <summary>
        /// Registers a keyed durable host, exposes its process engine, and initializes the host during application startup.
        /// </summary>
        public IServiceCollection AddAzureStorageDurableTaskEngine(object serviceKey, Action<AzureStorageDurableTaskWorkerBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(serviceKey);
            ArgumentNullException.ThrowIfNull(configure);
            services.AddAzureStorageDurableTaskHost(serviceKey, configure);
            services.AddKeyedSingleton<IProcessEngine>(serviceKey, static (sp, key) => sp.GetRequiredKeyedService<DurableTaskProcessHost>(key).Engine);
            services.AddKeyedSingleton<IProcessExecutionRepository>(serviceKey, static (sp, key) => sp.GetRequiredKeyedService<DurableTaskProcessHost>(key).ProcessExecutionRepository);
            services.AddSingleton<IHostedService>(sp => new DurableTaskProcessHostInitializationHostedService(
                host: sp.GetRequiredKeyedService<DurableTaskProcessHost>(serviceKey),
                logger: sp.GetRequiredService<ILogger<DurableTaskProcessHostInitializationHostedService>>()
            ));
            return services;
        }
    }
}
