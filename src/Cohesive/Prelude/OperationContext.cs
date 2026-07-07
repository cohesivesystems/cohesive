using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Claims;

namespace Cohesive.Prelude;

/// <summary>
/// Immutable operation-scoped context for cancellation, time, identity, tracing, services, and extensible metadata.
/// </summary>
/// <param name="TimeProvider">The time provider associated with the context.</param>
/// <param name="StartedUtc">The UTC time at which the operation started.</param>
/// <param name="Principal">The principal associated with the context.</param>
/// <param name="TraceContext">The trace associated with the context.</param>
/// <param name="Items">An immutable dictionary of items associated with the context.</param>
/// <param name="CancellationToken">The cancellation token associated with the context.</param>
public sealed record OperationContext(
    TimeProvider TimeProvider,
    DateTimeOffset StartedUtc,
    ClaimsPrincipal Principal,
    ActivityContext? TraceContext,
    ImmutableDictionary<string, object?> Items,
    CancellationToken CancellationToken
    ) : ICancellationTokenContext
{
    static readonly ImmutableDictionary<string, object?> EmptyItems = [];

    /// <summary>
    /// Creates a new operation context with sensible defaults.
    /// </summary>
    public static OperationContext Create(
        TimeProvider? timeProvider = null,
        ClaimsPrincipal? principal = null,
        ActivityContext? traceContext = null,
        ImmutableDictionary<string, object?>? items = null,
        CancellationToken cancellationToken = default
        )
    {
        timeProvider ??= TimeProvider.System;
        return new(
            TimeProvider: timeProvider,
            StartedUtc: timeProvider.GetUtcNow(),
            CancellationToken: cancellationToken,
            Principal: principal ?? new ClaimsPrincipal(),
            TraceContext: traceContext ?? Activity.Current?.Context,
            Items: items ?? EmptyItems
            );
    }

    /// <summary>
    /// Returns the current UTC timestamp from the configured time provider.
    /// </summary>
    public DateTimeOffset UtcNow => TimeProvider.GetUtcNow();

    /// <summary>
    /// Returns a copy of the context with a different cancellation token.
    /// </summary>
    public OperationContext WithCancellationToken(CancellationToken cancellationToken = default) =>
        this with { CancellationToken = cancellationToken };

    /// <summary>
    /// Returns a copy of the context with a different principal.
    /// </summary>
    public OperationContext WithPrincipal(ClaimsPrincipal principal) =>
        this with { Principal = Guard.RequireNotNull(principal) };

    /// <summary>
    /// Returns a copy of the context with a different trace context.
    /// </summary>
    public OperationContext WithTraceContext(ActivityContext? traceContext) =>
        this with { TraceContext = traceContext };

    /// <summary>
    /// Returns a copy of the context with an updated metadata item.
    /// </summary>
    public OperationContext WithItem(string key, object? value) =>
        this with { Items = Items.SetItem(Guard.RequireNotNullOrWhiteSpace(key), value) };

    /// <summary>
    /// Returns a copy of the context without the named metadata item.
    /// </summary>
    public OperationContext WithoutItem(string key) =>
        this with { Items = Items.Remove(Guard.RequireNotNullOrWhiteSpace(key)) };

    /// <summary>
    /// Tries to read a typed metadata item.
    /// </summary>
    public bool TryGetItem<T>(string key, out T? value) => 
        Items.TryGetValue(key, out value);
    
    /// <summary>
    /// Tries to read a typed metadata item.
    /// </summary>
    public bool TryGetFirstItem<T>(ReadOnlySpan<string> keys, out T? value) => 
        Items.TryGetFirstValue(keys, out value);
}

/// <summary>
/// A context that exposes a cancellation token.
/// </summary>
public interface ICancellationTokenContext
{
    /// <summary>
    /// The cancellation token.
    /// </summary>
    CancellationToken CancellationToken { get; }
}

/// <summary>
/// A cancellation token context.
/// </summary>
/// <param name="CancellationToken"></param>
public readonly record struct CancellationTokenContext(CancellationToken CancellationToken) : ICancellationTokenContext;