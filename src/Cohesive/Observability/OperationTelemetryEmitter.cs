using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cohesive.Observability;

/// <summary>
/// Emits one native .NET activity and duration/failure measurements while isolating the observed operation from
/// synchronous diagnostic-observer failures.
/// </summary>
/// <remarks>
/// The instrumentation owner retains authority for source, instrument, operation, status, and tag semantics. This
/// component does not create or own the supplied <see cref="ActivitySource"/> or instruments, select a collection
/// pipeline, or abstract their native contracts. The duration histogram must represent elapsed seconds. Callers must
/// keep metric tags bounded and low-cardinality and must complete every returned activity exactly once.
/// </remarks>
public sealed class OperationTelemetryEmitter
{
    readonly ActivitySource? activitySource;
    readonly Histogram<double>? duration;
    readonly Counter<long>? failures;

    /// <summary>Creates an emitter over caller-owned native diagnostic objects.</summary>
    /// <param name="activitySource">
    /// Caller-owned activity source, or <see langword="null"/> when tracing registration failed or is unavailable.
    /// </param>
    /// <param name="duration">
    /// Caller-owned elapsed-duration histogram in seconds, or <see langword="null"/> when duration instrumentation
    /// registration failed or is unavailable.
    /// </param>
    /// <param name="failures">
    /// Optional caller-owned failure counter. A value is added when <see cref="CompleteOperation"/> receives
    /// <see cref="ActivityStatusCode.Error"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="duration"/> is non-null and does not declare elapsed seconds with unit <c>s</c>.
    /// </exception>
    public OperationTelemetryEmitter(
        ActivitySource? activitySource,
        Histogram<double>? duration,
        Counter<long>? failures = null)
    {
        if (duration is not null && !string.Equals(duration.Unit, "s", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An operation-duration histogram must declare elapsed seconds with unit 's'.",
                nameof(duration));
        }
        this.activitySource = activitySource;
        this.duration = duration;
        this.failures = failures;
    }

    /// <summary>Whether any supplied activity source or instrument currently has a listener.</summary>
    public bool IsEnabled => (activitySource?.HasListeners() ?? false)
        || duration?.Enabled == true
        || failures?.Enabled == true;

    /// <summary>Starts a native activity beneath the ambient parent when a listener requests it.</summary>
    /// <param name="name">Stable operation name owned by the instrumenting library.</param>
    /// <param name="kind">Native activity kind describing the operation boundary.</param>
    /// <returns>
    /// A started activity, or <see langword="null"/> when no listener requests one or an observer fails during start.
    /// The caller transfers disposal to <see cref="CompleteOperation"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal) =>
        StartActivity(name, kind, parentContext: null);

    /// <summary>Starts a native activity beneath an explicit parent when a listener requests it.</summary>
    /// <param name="name">Stable operation name owned by the instrumenting library.</param>
    /// <param name="kind">Native activity kind describing the operation boundary.</param>
    /// <param name="parentContext">
    /// Explicit distributed-trace parent. Pass <see langword="null"/> to use the ambient parent.
    /// </param>
    /// <returns>
    /// A started activity, or <see langword="null"/> when no listener requests one or an observer fails during start.
    /// The caller transfers disposal to <see cref="CompleteOperation"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public Activity? StartActivity(
        string name,
        ActivityKind kind,
        ActivityContext? parentContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported activity kind.");

        try
        {
            return parentContext is { } parent
                ? activitySource?.StartActivity(name, kind, parent)
                : activitySource?.StartActivity(name, kind);
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Captures a monotonic duration timestamp only when the supplied duration histogram is enabled.</summary>
    /// <returns>
    /// A timestamp accepted by <see cref="CompleteOperation"/>, or zero when duration measurement is unavailable or
    /// disabled. The value has no wall-clock interpretation.
    /// </returns>
    public long StartTimer() => duration?.Enabled == true ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>
    /// Completes and disposes an activity and records duration and failure measurements with caller-owned tags.
    /// </summary>
    /// <param name="activity">Activity returned by <see cref="StartActivity(string, ActivityKind)"/>.</param>
    /// <param name="started">Timestamp returned by <see cref="StartTimer"/>.</param>
    /// <param name="status">
    /// Native terminal status. <see cref="ActivityStatusCode.Unset"/> preserves the activity default.
    /// </param>
    /// <param name="tags">
    /// Owner-defined activity attributes and bounded metric dimensions. Exact or unbounded identities should remain
    /// trace-only and must not be included here.
    /// </param>
    /// <param name="exception">
    /// Propagated failure, or <see langword="null"/>. Only its runtime type is added to the activity; its message and
    /// stack are not emitted. A non-null value requires <see cref="ActivityStatusCode.Error"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="exception"/> is non-null and <paramref name="status"/> is not
    /// <see cref="ActivityStatusCode.Error"/>.
    /// </exception>
    public void CompleteOperation(
        Activity? activity,
        long started,
        ActivityStatusCode status,
        in TagList tags,
        Exception? exception = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported activity status.");
        if (exception is not null && status != ActivityStatusCode.Error)
        {
            throw new ArgumentException(
                "An observed exception requires an error activity status.",
                nameof(exception));
        }

        try
        {
            if (activity?.IsAllDataRequested == true)
            {
                foreach (var tag in tags)
                    activity.SetTag(tag.Key, tag.Value);
                if (exception is not null)
                    activity.SetTag("error.type", exception.GetType().FullName);
            }
            if (activity is not null && status != ActivityStatusCode.Unset)
                activity.SetStatus(status);
        }
        catch (Exception telemetryException) when (IsRecoverableObservabilityFailure(telemetryException))
        {
            // Activity listeners are application extensions and cannot alter the observed operation.
        }

        if (started != 0L && duration is not null)
        {
            try
            {
                duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            }
            catch (Exception telemetryException) when (IsRecoverableObservabilityFailure(telemetryException))
            {
                // Synchronous metric observers cannot alter the observed operation.
            }
        }

        if (status == ActivityStatusCode.Error && failures is not null)
        {
            try
            {
                failures.Add(1, tags);
            }
            catch (Exception telemetryException) when (IsRecoverableObservabilityFailure(telemetryException))
            {
                // Failure-metric observers are isolated independently from duration observers.
            }
        }

        try
        {
            activity?.Dispose();
        }
        catch (Exception telemetryException) when (IsRecoverableObservabilityFailure(telemetryException))
        {
            // Activity stop listeners are best effort for the same reason as recording listeners.
        }
    }

    static bool IsRecoverableObservabilityFailure(Exception exception) => exception is not (
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);
}
