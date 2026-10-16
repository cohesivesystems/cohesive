using System.Collections.Concurrent;
using ProcessStartResult = Cohesive.Execution.ProcessStartResult;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;
using Cohesive.Api.Execution;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Runtime;
using Cohesive.Storage.Processes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cohesive.Host.Services;

/// <summary>Bounded, host-owned background execution over native in-memory admission and process checkpoints.</summary>
/// <remarks>Admission, deduplication and results are lost when this host exits. There is one sequential worker;
/// no retry, timer scheduling, cross-host coordination or restart recovery. Capacity counts retained executions,
/// including completed executions; a full host refuses new admissions instead of silently evicting retry history.</remarks>
public sealed class HostProcessWorker : BackgroundService, IProcessExecutionValueRepository, IProcessExecutionCompletionWaiter
{
    readonly PreparedServiceProcess prepared;
    readonly InMemoryExecutionControlApiAdapter registry;
    readonly ExecutionControlApiCatalog catalog = ExecutionControlApiCatalog.Create();
    readonly ExecutionProcessStartDispatcher execute;
    readonly IProcessExecutionValueRepository values;
    readonly Channel<Work> queue;
    readonly ConcurrentDictionary<(InteractionAuthorityScope, ProcessInstanceId), Work> retained = new();
    readonly SemaphoreSlim admission = new(1);
    readonly int capacity;
    ILogger<HostProcessWorker> logger;
    int running;

    /// <summary>Prepares a finite in-memory execution provider; construction does not start background work.</summary>
    /// <param name="prepared">Exact compiled process and native operation bindings.</param>
    /// <param name="maximumExecutions">Maximum retained instances across all scopes; replay remains available at capacity.</param>
    /// <param name="logger">Host-owned operator logging.</param>
    public HostProcessWorker(PreparedServiceProcess prepared, int maximumExecutions, ILogger<HostProcessWorker>? logger = null)
    {
        this.prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
        this.logger = logger ?? NullLogger<HostProcessWorker>.Instance;
        if (maximumExecutions <= 0) throw new ArgumentOutOfRangeException(nameof(maximumExecutions));
        capacity = maximumExecutions;
        // Reject unsupported finite-host constructs before admitting anything.
        _ = new EphemeralProcessExecutor(prepared.Plan);
        registry = new(prepared.Contracts, catalog);
        var local = registry.CreateInMemoryProcessBindings([prepared.Plan], prepared.Host,
            new("host-process-worker", TimeSpan.FromMinutes(5)), receipt => new(
                receipt.Request.Context.Authorization.AuthorityScope, new(receipt.Request.Context.ProcessInstanceId.Value),
                new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal),
                receipt.Request.Context.Provenance));
        execute = local.Start;
        values = local.Values;
        queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = true });
    }

    internal void AttachLogger(ILogger<HostProcessWorker> value) => logger = value;

    /// <summary>Binds all admission and result operations for this exact process, requiring host lifetime.</summary>
    /// <param name="declaration">Complete service declaration.</param><param name="authority">Execution authority.</param>
    /// <param name="authorization">Independent invocation authorization for each operation.</param>
    /// <returns>A fully covered runtime backed by this worker.</returns>
    public ServiceRuntime Bind(ExecutionDefinitionDocument declaration, string authority, IServiceInvocationAuthorization authorization)
    {
        var bindings = declaration.GetDefinition<ServiceDefinition>().Operations.Select(operation => operation switch
        {
            ServiceProcessOperation => (ServiceBinding)new ServiceProcessBinding(operation.Id, prepared.Plan, authority, AdmitAsync, ProcessExecutionLifetime.Host),
            ServiceProcessResultOperation => new ServiceProcessResultBinding(operation.Id, prepared.Plan, authority, this),
            _ => throw ServiceImplementationPlan.Error("services.binding.executionUnsupported", "This worker binds process admission and result operations only.")
        }).ToArray();
        return new(declaration, bindings, authorization);
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Worker already started.");
        return base.StartAsync(cancellationToken);
    }

    async ValueTask<ProcessStartResult> AdmitAsync(OperationContext context, ProcessStartRequest request, ExecutionApiInvocationContext invocation)
    {
        await admission.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref running) != 1) throw new InvalidOperationException("The process worker is not accepting work.");
            var key = (invocation.Authorization.AuthorityScope, request.Context.ProcessInstanceId);
            if (retained.Count >= capacity && !retained.ContainsKey(key))
                throw new InvalidOperationException("The host's retained execution capacity is full; no work was admitted.");
            context.ThrowIfCancellationRequested();
            var result = (ProcessStartResult)(await registry.DispatchAsync(context, catalog.Start, request, invocation).ConfigureAwait(false)).Body!;
            if (result.IsConflict || retained.ContainsKey(key)) return result;
            var work = new Work(context.WithCancellationToken(), request, invocation);
            retained[key] = work;
            // Receipt and queue publication are serialized with shutdown; no request token owns execution.
            if (!queue.Writer.TryWrite(work)) throw new InvalidOperationException("Host queue admission invariant failed.");
            return result;
        }
        finally { admission.Release(); }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await execute(work.Context.WithCancellationToken(stoppingToken), work.Request, work.Invocation).ConfigureAwait(false);
                var result = await values.GetValuesAsync(work.Context.WithCancellationToken(stoppingToken),
                    work.Invocation.Authorization.AuthorityScope, work.Request.Context.ProcessInstanceId).ConfigureAwait(false);
                if (result.State != ProcessExecutionValueReadState.Available)
                    throw new InvalidOperationException("Finite host execution did not reach a retained terminal outcome.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                work.Failed = true;
                logger.LogError(exception, "Host process execution stopped without a qualified terminal result; no retry was attempted.");
            }
            finally { work.Completed.TrySetResult(); }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Volatile.Write(ref running, 2); queue.Writer.TryComplete(); }
        finally { admission.Release(); }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var work in retained.Values)
            if (!work.Completed.Task.IsCompleted)
            {
                work.Failed = true;
                work.Completed.TrySetResult();
            }
    }

    /// <inheritdoc />
    public async ValueTask<ProcessExecutionValueReadResult> GetValuesAsync(OperationContext context, InteractionAuthorityScope authorityScope, ProcessInstanceId processInstanceId)
    {
        context.ThrowIfCancellationRequested();
        if (!retained.TryGetValue((authorityScope, processInstanceId), out var work)) return ProcessExecutionValueReadResult.NotFound();
        if (work.Failed) return ProcessExecutionValueReadResult.TerminalArtifactUnavailable(new(prepared.Plan.DefinitionReference, processInstanceId));
        var result = await values.GetValuesAsync(context, authorityScope, processInstanceId).ConfigureAwait(false);
        return result.State == ProcessExecutionValueReadState.NotFound ? ProcessExecutionValueReadResult.InProgress(new(prepared.Plan.DefinitionReference, processInstanceId)) : result;
    }

    /// <inheritdoc />
    public async ValueTask<bool> WaitForCompletionAsync(OperationContext context, InteractionAuthorityScope authorityScope, ProcessInstanceId processInstanceId, TimeSpan maximumWait)
    {
        if (!retained.TryGetValue((authorityScope, processInstanceId), out var work)) return false;
        try { await work.Completed.Task.WaitAsync(maximumWait, context.CancellationToken).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    sealed class Work(OperationContext context, ProcessStartRequest request, ExecutionApiInvocationContext invocation)
    {
        public OperationContext Context { get; } = context;
        public ProcessStartRequest Request { get; } = request;
        public ExecutionApiInvocationContext Invocation { get; } = invocation;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Failed;
    }
}

/// <summary>Generic host ownership of a finite process worker.</summary>
public static class HostProcessWorkerRegistration
{
    /// <summary>Registers one host-owned worker without starting it or building a second service provider.</summary>
    /// <param name="services">The owning host services.</param>
    /// <param name="prepared">Prepared native process bindings.</param>
    /// <param name="maximumExecutions">Bound on retained admissions, including completed instances.</param>
    /// <returns>The worker to bind to the exact service declaration before host startup.</returns>
    public static HostProcessWorker AddCohesiveHostProcessWorker(this IServiceCollection services,
        PreparedServiceProcess prepared, int maximumExecutions)
    {
        ArgumentNullException.ThrowIfNull(services);
        var worker = new HostProcessWorker(prepared, maximumExecutions);
        services.AddLogging();
        services.AddSingleton<IHostedService>(provider =>
        {
            worker.AttachLogger(provider.GetRequiredService<ILogger<HostProcessWorker>>());
            return worker;
        });
        return worker;
    }
}
