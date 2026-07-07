namespace Cohesive.Processes.Runtime;

/// <summary>
/// Wait adapter used by wait nodes.
/// </summary>
public interface IProcessWaitAdapter
{
    /// <summary>
    /// Waits for timer or external event.
    /// </summary>
    Task<object?> WaitAsync(
        OperationContext context,
        ProcessWaitType waitType,
        string key,
        TimeSpan? timeout
    );
}


/// <summary>
/// In-memory wait adapter supporting external events and timer delays.
/// </summary>
public sealed class InMemoryProcessWaitAdapter : IProcessWaitAdapter, IProcessSignalSink
{
    readonly Lock gate = new();
    readonly Dictionary<string, Queue<object?>> queuedExternalEvents = new(StringComparer.Ordinal);
    readonly Dictionary<string, Queue<TaskCompletionSource<object?>>> externalWaiters = new(StringComparer.Ordinal);

    /// <summary>
    /// Publishes an external event payload.
    /// </summary>
    public void PublishExternalEvent(string key, object? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        TaskCompletionSource<object?>? waiter = null;
        lock (gate)
        {
            if (externalWaiters.TryGetValue(key, out var waiters) && waiters.Count > 0)
            {
                waiter = waiters.Dequeue();
                if (waiters.Count == 0)
                    externalWaiters.Remove(key);
            }
            else
            {
                if (!queuedExternalEvents.TryGetValue(key, out var events))
                {
                    events = [];
                    queuedExternalEvents[key] = events;
                }

                events.Enqueue(payload);
            }
        }

        waiter?.TrySetResult(payload);
    }

    /// <inheritdoc />
    public Task PublishAsync(OperationContext context, string key, object? payload)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        PublishExternalEvent(key, payload);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<object?> WaitAsync(OperationContext context, ProcessWaitType waitType, string key, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        context.ThrowIfCancellationRequested();

        return waitType switch
        {
            ProcessWaitType.Timer => await WaitForTimerAsync(context, key, timeout).ConfigureAwait(false),
            ProcessWaitType.ExternalEvent => await WaitForExternalEventAsync(context, key, timeout).ConfigureAwait(false),
            _ => throw new SemanticRuleViolationException($"Unsupported wait type '{waitType}'.")
        };
    }

    static async Task<ProcessTimerFired> WaitForTimerAsync(OperationContext context, string key, TimeSpan? timeout)
    {
        if (timeout is { } delay && delay > TimeSpan.Zero)
            await Task.Delay(delay, context.TimeProvider, context.CancellationToken).ConfigureAwait(false);

        return new(Key: key, FiredAtUtc: context.UtcNow);
    }

    async Task<object?> WaitForExternalEventAsync(OperationContext context, string key, TimeSpan? timeout)
    {
        Task<object?> waitTask;
        TaskCompletionSource<object?> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (gate)
        {
            if (queuedExternalEvents.TryGetValue(key, out var events) && events.Count > 0)
            {
                var payload = events.Dequeue();
                if (events.Count == 0)
                    queuedExternalEvents.Remove(key);

                return payload;
            }

            if (!externalWaiters.TryGetValue(key, out var waiters))
            {
                waiters = [];
                externalWaiters[key] = waiters;
            }

            waiters.Enqueue(waiter);
            waitTask = waiter.Task;
        }

        if (timeout is not { } timeoutValue || timeoutValue <= TimeSpan.Zero)
        {
            using var registration = context.CancellationToken.Register(() => waiter.TrySetCanceled(context.CancellationToken));
            return await waitTask.ConfigureAwait(false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var delayTask = Task.Delay(timeoutValue, context.TimeProvider, timeoutCts.Token);
        var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
        if (completed == waitTask)
        {
            timeoutCts.Cancel();
            return await waitTask.ConfigureAwait(false);
        }

        RemoveWaiter(key, waiter);
        throw new TimeoutException($"Wait for external event '{key}' timed out after {timeoutValue}.");
    }

    void RemoveWaiter(string key, TaskCompletionSource<object?> waiter)
    {
        lock (gate)
        {
            if (!externalWaiters.TryGetValue(key, out var waiters) || waiters.Count == 0)
                return;

            Queue<TaskCompletionSource<object?>> retained = [];
            while (waiters.Count > 0)
            {
                var current = waiters.Dequeue();
                if (!ReferenceEquals(current, waiter))
                    retained.Enqueue(current);
            }

            if (retained.Count == 0)
            {
                externalWaiters.Remove(key);
                return;
            }

            externalWaiters[key] = retained;
        }
    }
}
