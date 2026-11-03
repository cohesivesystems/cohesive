using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cohesive.Host.Services;

/// <summary>Lifecycle attachment used when preparing API bindings before or after host construction.</summary>
public interface IServiceProcessLifetime
{
    /// <summary>Admits a process to the host's registered durable execution provider.</summary>
    /// <param name="process">Prepared process.</param><param name="declaration">Exact service contract.</param>
    /// <param name="authority">Logical execution authority.</param><param name="authorization">Invocation authorization.</param>
    /// <returns>Provider-backed service runtime.</returns>
    ServiceRuntime Durable(PreparedServiceProcess process, ExecutionDefinitionDocument declaration,
        string authority, IServiceInvocationAuthorization authorization);
    /// <summary>Creates an owned worker; no execution begins during preparation.</summary>
    /// <param name="process">Prepared process.</param><param name="maximumExecutions">Retained execution bound.</param>
    /// <returns>The worker to bind to the service contract.</returns>
    HostProcessWorker Worker(PreparedServiceProcess process, int maximumExecutions);
    /// <summary>Opts a prepared process into scoped private logging.</summary><param name="process">Failure source.</param>
    void Log(PreparedServiceProcess process);
    /// <summary>Opts an ephemeral process into scoped private logging.</summary><param name="process">Failure source.</param>
    void Log(HostedServiceProcess process);
}

/// <summary>Adapts explicit preparation to normal generic-host registrations.</summary>
/// <param name="services">The host's service collection, before building its provider.</param>
/// <param name="durableProvider">Optional already constructed durable provider, borrowing native clients.</param>
public sealed class ServiceCollectionProcessLifetime(IServiceCollection services, IDurableServiceProcessProvider? durableProvider = null) : IServiceProcessLifetime
{
    /// <inheritdoc />
    public ServiceRuntime Durable(PreparedServiceProcess process, ExecutionDefinitionDocument declaration,
        string authority, IServiceInvocationAuthorization authorization) =>
        (durableProvider ?? throw new InvalidOperationException("Supply a durable provider when preparing bindings before host construction."))
            .Bind(process, declaration, authority, authorization);
    readonly IServiceCollection registrations = services ?? throw new ArgumentNullException(nameof(services));
    /// <inheritdoc />
    public HostProcessWorker Worker(PreparedServiceProcess process, int maximumExecutions) => registrations.AddCohesiveHostProcessWorker(process, maximumExecutions);
    /// <inheritdoc />
    public void Log(PreparedServiceProcess process) => registrations.AddCohesiveTransitionFailureLogging(process);
    /// <inheritdoc />
    public void Log(HostedServiceProcess process) => registrations.AddCohesiveTransitionFailureLogging(process);
}

/// <summary>Owns workers and logging subscriptions prepared from DI resources; never owns those resources.</summary>
/// <remarks>Prepare before StartAsync. Shutdown stops workers before releasing subscriptions. Dispose also
/// releases preparation that never started. Lifecycle calls are serialized by the generic host.</remarks>
public sealed class ServiceProcessLifetime : IServiceProcessLifetime, IHostedService, IDisposable
{
    readonly ILoggerFactory loggers;
    readonly IDurableServiceProcessProvider? durableProvider;
    readonly List<HostProcessWorker> workers = [];
    readonly TransitionFailureLoggingOptions logging = new();
    readonly TransitionFailureLoggingService logger;
    bool started;
    bool disposed;
    /// <summary>Creates an unstarted owner using the host's logger factory.</summary><param name="loggers">Borrowed logger factory.</param>
    /// <param name="durableProvider">Optional host-owned durable provider.</param>
    public ServiceProcessLifetime(ILoggerFactory loggers, IDurableServiceProcessProvider? durableProvider = null)
    {
        this.durableProvider = durableProvider;
        this.loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
        logger = new(Options.Create(logging), loggers);
    }
    void RequirePreparation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) throw new InvalidOperationException("Process preparation is complete.");
    }
    /// <inheritdoc />
    public ServiceRuntime Durable(PreparedServiceProcess process, ExecutionDefinitionDocument declaration,
        string authority, IServiceInvocationAuthorization authorization)
    {
        RequirePreparation();
        return (durableProvider ?? throw new InvalidOperationException("Register a durable service process provider before mapping services."))
            .Bind(process, declaration, authority, authorization);
    }
    /// <inheritdoc />
    public HostProcessWorker Worker(PreparedServiceProcess process, int maximumExecutions)
    {
        RequirePreparation();
        var worker = new HostProcessWorker(process, maximumExecutions, loggers.CreateLogger<HostProcessWorker>());
        workers.Add(worker);
        return worker;
    }
    /// <inheritdoc />
    public void Log(PreparedServiceProcess process) { ArgumentNullException.ThrowIfNull(process); RequirePreparation(); logging.Processes.TryAdd(process, process.SubscribeTransitionFailures); }
    /// <inheritdoc />
    public void Log(HostedServiceProcess process) { ArgumentNullException.ThrowIfNull(process); RequirePreparation(); logging.Processes.TryAdd(process, process.SubscribeTransitionFailures); }
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RequirePreparation();
        started = true;
        var count = 0;
        try
        {
            await logger.StartAsync(cancellationToken).ConfigureAwait(false);
            for (; count < workers.Count; count++) await workers[count].StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            for (var index = count - 1; index >= 0; index--) await workers[index].StopAsync(CancellationToken.None).ConfigureAwait(false);
            logger.Dispose();
            throw;
        }
    }
    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var index = workers.Count - 1; index >= 0; index--) await workers[index].StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { await logger.StopAsync(cancellationToken).ConfigureAwait(false); }
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var worker in workers) worker.Dispose();
        logger.Dispose();
    }
}
