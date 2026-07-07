using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Processes;

/// <summary>
/// Reusable process runtime registration helpers for backend-neutral execution modes.
/// </summary>
public static class ProcessRuntimeRegistration
{
    extension(IServiceProvider sp)
    {
        public IProcessEngine ResolveProcessEngine(object? serviceKey) =>
            serviceKey is null
                ? sp.GetRequiredService<IProcessEngine>()
                : sp.GetRequiredKeyedService<IProcessEngine>(serviceKey);
        
        public TypedProcessDefinition<TInput, TOutput> ResolveProcessDefinition<TProcess, TInput, TOutput>(string? processName) 
            where TProcess : class, IProcessDefinition<TInput, TOutput> =>
            ActivatorUtilities.GetServiceOrCreateInstance<TProcess>(sp).Define(processName: processName);
    }
    
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers an in-memory process runtime. Worker-only hosts are a no-op because in-memory execution is driven by the engine in-process.
        /// </summary>
        public IServiceCollection AddInMemoryProcessRuntime(
            object serviceKey,
            ProcessRuntimeCapabilities capabilities,
            Func<IServiceProvider, ProcessRuntimeServices> runtimeFactory,
            Action<IServiceProvider, ProcessRuntimeServices>? configureRuntime = null
            )
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(serviceKey);
            ArgumentNullException.ThrowIfNull(runtimeFactory);

            if (capabilities is ProcessRuntimeCapabilities.None)
                throw new ArgumentOutOfRangeException(nameof(capabilities), "At least one process runtime capability must be enabled.");

            if (!capabilities.HasFlag(ProcessRuntimeCapabilities.Engine))
                return services;

            return services.AddKeyedSingleton<IProcessEngine>(serviceKey, (sp, _) =>
            {
                var runtime = runtimeFactory(sp);
                configureRuntime?.Invoke(sp, runtime);
                return new ProcessEngine(runtime);
            });
        }
    }
}
