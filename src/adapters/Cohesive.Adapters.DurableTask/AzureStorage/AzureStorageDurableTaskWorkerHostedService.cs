using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.DurableTask.AzureStorage;

/// <summary>
/// Hosted service that starts and stops a durable worker.
/// </summary>
public sealed class AzureStorageDurableTaskWorkerHostedService(
    DurableTaskProcessHost host,
    ILogger<AzureStorageDurableTaskWorkerHostedService> logger
    ) : IHostedService, IAsyncDisposable
{
    public async Task StartAsync(CancellationToken ct)
    {
        await host.CreateIfNotExistsAsync();
        await host.StartAsync();

        logger.LogInformation(
            "Started {HostName} durable worker for task hub {TaskHubName}.",
            host.HostName,
            host.TaskHubName
            );
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await host.StopAsync();

        logger.LogInformation(
            "Stopped {HostName} durable worker.",
            host.HostName
            );
    }

    public ValueTask DisposeAsync() => host.DisposeAsync();
}
