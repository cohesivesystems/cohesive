using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Hosted service that initializes a durable process host before requests are served.
/// </summary>
sealed class DurableTaskProcessHostInitializationHostedService(
    DurableTaskProcessHost host,
    ILogger<DurableTaskProcessHostInitializationHostedService> logger
    ) : IHostedService, IAsyncDisposable
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await host.CreateIfNotExistsAsync();

        logger.LogInformation(
            "Initialized {HostName} durable host for task hub {TaskHubName}.",
            host.HostName,
            host.TaskHubName
            );
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => host.DisposeAsync();
}
