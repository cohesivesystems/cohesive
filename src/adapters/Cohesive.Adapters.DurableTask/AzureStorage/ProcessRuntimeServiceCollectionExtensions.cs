using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.DurableTask.AzureStorage;

/// <summary>
/// Reusable process runtime registration helpers for Azure Storage Durable Task execution.
/// </summary>
public static class ProcessRuntimeServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers an Azure Storage Durable Task-backed process runtime.
        /// </summary>
        public IServiceCollection AddAzureStorageDurableTaskProcessRuntime(
            object serviceKey,
            ProcessRuntimeCapabilities capabilities,
            Func<IServiceProvider, ProcessRuntimeServices> runtimeFactory,
            Action<AzureStorageDurableTaskWorkerBuilder> configureDurableTask,
            Action<IServiceProvider, ProcessRuntimeServices>? configureRuntime = null
            )
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(serviceKey);
            ArgumentNullException.ThrowIfNull(runtimeFactory);
            ArgumentNullException.ThrowIfNull(configureDurableTask);
            EnsureCapabilities(capabilities);

            switch (capabilities)
            {
                case ProcessRuntimeCapabilities.Engine:
                    return services.AddAzureStorageDurableTaskEngine(serviceKey, ConfigureBuilder);
                case ProcessRuntimeCapabilities.Worker:
                    return services.AddAzureStorageDurableTaskWorker(serviceKey, ConfigureBuilder);
                case ProcessRuntimeCapabilities.Engine | ProcessRuntimeCapabilities.Worker:
                    services.AddAzureStorageDurableTaskWorker(serviceKey, ConfigureBuilder);
                    services.AddKeyedSingleton<IProcessEngine>(serviceKey, static (sp, key) => sp.GetRequiredKeyedService<DurableTaskProcessHost>(key).Engine);
                    return services;
                default:
                    throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities, "Unsupported process runtime capability combination.");
            }

            void ConfigureBuilder(AzureStorageDurableTaskWorkerBuilder builder)
            {
                builder.UseRuntimeFactory(runtimeFactory);
                if (configureRuntime is not null)
                    builder.ConfigureRuntime(configureRuntime);
                configureDurableTask(builder);
            }
        }
    }

    static void EnsureCapabilities(ProcessRuntimeCapabilities capabilities)
    {
        if (capabilities is ProcessRuntimeCapabilities.None)
            throw new ArgumentOutOfRangeException(nameof(capabilities), "At least one process runtime capability must be enabled.");
    }
}
