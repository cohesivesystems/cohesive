using Cohesive.Api.Execution.Services;
using Cohesive.Storage.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cohesive.Adapters.AspNet.Services;

/// <summary>Explicit, host-owned logging of private transition failure evidence.</summary>
public static class TransitionFailureLoggingExtensions
{
    /// <summary>Logs private transition failures at Debug while the native host is running.</summary>
    /// <param name="services">Native host services; the container owns subscription disposal.</param>
    /// <param name="process">Prepared process whose private failures may be sent to protected operator logs.</param>
    /// <returns>The service collection for further native registration.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <remarks>Registering the same process repeatedly subscribes once per host. Subscription begins at
    /// host startup and ends on stop or container disposal, including disposal without StopAsync.
    /// An unstarted host does not subscribe. Logger category is Cohesive.Storage.Processes.TransitionFailures.
    /// Messages contain native identities, tokens and provider details; configure only protected sinks.
    /// Disabled Debug logging does not construct message arguments. No process is resolved or compiled here.</remarks>
    public static IServiceCollection AddCohesiveTransitionFailureLogging(this IServiceCollection services, HostedServiceProcess process)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(process);
        services.AddLogging();
        services.Configure<TransitionFailureLoggingOptions>(options => options.Processes.Add(process));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TransitionFailureLoggingService>());
        return services;
    }
}

internal sealed class TransitionFailureLoggingOptions
{
    public HashSet<HostedServiceProcess> Processes { get; } = new(ReferenceEqualityComparer.Instance);
}

internal sealed class TransitionFailureLoggingService(
    IOptions<TransitionFailureLoggingOptions> options, ILoggerFactory loggers) : IHostedService, IDisposable
{
    readonly object gate = new();
    readonly ILogger logger = loggers.CreateLogger("Cohesive.Storage.Processes.TransitionFailures");
    List<IDisposable>? subscriptions;
    bool disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (subscriptions is not null) return Task.CompletedTask;
            subscriptions = [];
            try
            {
                foreach (var process in options.Value.Processes)
                    subscriptions.Add(process.SubscribeTransitionFailures(Log));
            }
            catch
            {
                ReleaseSubscriptions();
                throw;
            }
        }
        return Task.CompletedTask;
    }

    void Log(EntityTransitionFailureDiagnostic failure)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        foreach (var diagnostic in failure.Result.Diagnostics)
            logger.LogDebug("Transition storage failure {Code} at {Location}: {Detail}; trace {TraceId}",
                diagnostic.Code, diagnostic.Location, diagnostic.Message, failure.TraceContext?.TraceId);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) ReleaseSubscriptions();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            ReleaseSubscriptions();
        }
    }

    void ReleaseSubscriptions()
    {
        if (subscriptions is null) return;
        foreach (var subscription in subscriptions) subscription.Dispose();
        subscriptions = null;
    }
}
