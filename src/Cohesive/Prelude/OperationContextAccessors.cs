using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.Extensions.AsyncState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cohesive.Prelude;

/// <summary>
/// Reads the current ambient operation context.
/// </summary>
public interface IOperationContextAccessor
{
    /// <summary>
    /// Gets the current operation context when available.
    /// </summary>
    OperationContext? Current { get; }

    /// <summary>
    /// Attempts to read the current operation context.
    /// </summary>
    bool TryGetCurrent(out OperationContext? context);
}

/// <summary>
/// Creates operation contexts with application defaults.
/// </summary>
public interface IOperationContextFactory
{
    /// <summary>
    /// Creates a new operation context.
    /// </summary>
    OperationContext Create(
        ClaimsPrincipal? principal = null,
        ActivityContext? traceContext = null,
        ImmutableDictionary<string, object?>? items = null, 
        CancellationToken cancellationToken = default
        );
}

/// <summary>
/// Pushes an operation context into an ambient async state for the lifetime of a scope.
/// </summary>
public interface IOperationContextScopeFactory
{
    /// <summary>
    /// Pushes the supplied context and restores the previous context when disposed.
    /// </summary>
    IDisposable Push(OperationContext context);
}

/// <summary>
/// Async-state-backed ambient operation context implementation.
/// </summary>
public sealed class AmbientOperationContextAccessor(
    IAsyncContext<OperationContext> asyncContext,
    IAsyncState asyncState,
    TimeProvider timeProvider
    ) : IOperationContextAccessor, IOperationContextFactory, IOperationContextScopeFactory
{
    readonly IAsyncContext<OperationContext> asyncContext = Guard.RequireNotNull(asyncContext);
    readonly IAsyncState asyncState = Guard.RequireNotNull(asyncState);
    readonly TimeProvider timeProvider = Guard.RequireNotNull(timeProvider);

    /// <inheritdoc />
    public OperationContext? Current =>
        asyncContext.TryGet(out var context) ? context : null;

    /// <inheritdoc />
    public bool TryGetCurrent(out OperationContext? context) =>
        asyncContext.TryGet(out context);

    /// <inheritdoc />
    public OperationContext Create(
        ClaimsPrincipal? principal = null,
        ActivityContext? traceContext = null,
        ImmutableDictionary<string, object?>? items = null, 
        CancellationToken cancellationToken = default
        ) => OperationContext.Create(timeProvider: timeProvider, principal: principal, traceContext: traceContext, items: items, cancellationToken: cancellationToken);

    /// <inheritdoc />
    public IDisposable Push(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var hadPrior = asyncContext.TryGet(out var prior);
        if (!hadPrior)
            asyncState.Initialize();

        asyncContext.Set(context);
        return new OperationContextScope(asyncContext, asyncState, hadPrior ? prior : null);
    }

    sealed class OperationContextScope(
        IAsyncContext<OperationContext> asyncContext,
        IAsyncState asyncState,
        OperationContext? prior
        ) : IDisposable
    {
        readonly IAsyncContext<OperationContext> asyncContext = Guard.RequireNotNull(asyncContext);
        readonly IAsyncState asyncState = Guard.RequireNotNull(asyncState);
        bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            if (prior is null)
                asyncState.Reset();
            else
                asyncContext.Set(prior);
        }
    }
}

/// <summary>
/// Dependency-injection registration helpers for ambient operation context services.
/// </summary>
public static class OperationContextServiceCollectionExtensions
{
    /// <summary>
    /// Registers async-state-backed ambient operation context services:<br />
    /// <see cref="IOperationContextFactory"/><br />
    /// <see cref="IOperationContextScopeFactory"/><br />
    /// <see cref="IOperationContextAccessor"/>
    /// </summary>
    public static IServiceCollection AddCohesiveOperationContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAsyncState();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AmbientOperationContextAccessor>();
        services.TryAddSingleton<IOperationContextAccessor>(static sp => sp.GetRequiredService<AmbientOperationContextAccessor>());
        services.TryAddSingleton<IOperationContextFactory>(static sp => sp.GetRequiredService<AmbientOperationContextAccessor>());
        services.TryAddSingleton<IOperationContextScopeFactory>(static sp => sp.GetRequiredService<AmbientOperationContextAccessor>());

        return services;
    }
    
    /// <summary>
    /// Throws a <see cref="OperationCanceledException"/> if <see cref="CancellationToken"/> has a cancellation requested. 
    /// </summary>
    /// <exception cref="OperationCanceledException">If the <see cref="CancellationToken"/> is canceled.</exception>
    public static void ThrowIfCancellationRequested(this ICancellationTokenContext context) => 
        context.CancellationToken.ThrowIfCancellationRequested();
}
