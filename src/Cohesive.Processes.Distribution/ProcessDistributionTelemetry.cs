using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cohesive.Processes.Distribution;

/// <summary>Stable tracing and metric contract for portable Process distribution.</summary>
/// <remarks>
/// Metric dimensions are bounded lifecycle values plus configured pool and capacity names. Logical work, dispatch,
/// worker-incarnation, and fence identities are trace-only attributes so individual jobs cannot create metric-cardinality
/// growth. Telemetry is a best-effort interpretation of canonical ledger evidence and never changes distribution
/// behavior when an observer fails.
/// </remarks>
public static class ProcessDistributionTelemetry
{
    /// <summary>Activity-source name emitted by portable Process distribution.</summary>
    public const string ActivitySourceName = "Cohesive.Processes.Distribution";

    /// <summary>Meter name emitted by portable Process distribution.</summary>
    public const string MeterName = "Cohesive.Processes.Distribution";

    /// <summary>Activity name for one bounded competing-consumer turn.</summary>
    public const string WorkerTurnActivityName = "cohesive.processes.distribution.worker.turn";

    /// <summary>Activity name for local interpretation of one fenced canonical work claim.</summary>
    public const string WorkExecutionActivityName = "cohesive.processes.distribution.work.execute";

    /// <summary>Counter of bounded distribution operations.</summary>
    public const string OperationsInstrumentName = "cohesive.processes.distribution.operations";

    /// <summary>Histogram of bounded distribution operation durations in seconds.</summary>
    public const string OperationDurationInstrumentName = "cohesive.processes.distribution.operation.duration";

    /// <summary>Histogram of admission-to-claim queue delay in seconds.</summary>
    public const string QueueDurationInstrumentName = "cohesive.processes.distribution.queue.duration";

    /// <summary>Histogram of physical attempt ordinals observed at execution.</summary>
    public const string AttemptsInstrumentName = "cohesive.processes.distribution.work.attempt";

    /// <summary>Counter of worker and claim lease-renewal outcomes.</summary>
    public const string LeaseEventsInstrumentName = "cohesive.processes.distribution.lease.events";

    /// <summary>Counter of exact distribution-store retries after provider exceptions.</summary>
    public const string StoreRetriesInstrumentName = "cohesive.processes.distribution.store.retries";

    /// <summary>Counter of final work outcomes committed by a worker runtime.</summary>
    public const string TerminalOutcomesInstrumentName = "cohesive.processes.distribution.work.terminal";

    /// <summary>Gauge of work counts by current pool lifecycle status.</summary>
    public const string PoolWorkInstrumentName = "cohesive.processes.distribution.pool.work";

    /// <summary>Gauge of worker counts by current pool health status.</summary>
    public const string PoolWorkersInstrumentName = "cohesive.processes.distribution.pool.workers";

    /// <summary>Gauge of currently reserved pool capacity by configured resource.</summary>
    public const string PoolReservedCapacityInstrumentName = "cohesive.processes.distribution.pool.capacity.reserved";

    /// <summary>Operation tag identifying the bounded distribution operation.</summary>
    public const string OperationTagName = "cohesive.processes.distribution.operation";

    /// <summary>Disposition tag identifying the bounded ledger or worker outcome.</summary>
    public const string DispositionTagName = "cohesive.processes.distribution.disposition";

    /// <summary>Configured logical worker-pool tag.</summary>
    public const string PoolTagName = "cohesive.processes.distribution.pool";

    /// <summary>Lifecycle-status tag used by work and worker snapshot instruments.</summary>
    public const string StatusTagName = "cohesive.processes.distribution.status";

    /// <summary>Configured resource-name tag used by capacity instruments.</summary>
    public const string ResourceTagName = "cohesive.processes.distribution.resource";

    /// <summary>Configured capacity-unit tag.</summary>
    public const string CapacityUnitTagName = "cohesive.processes.distribution.capacity.unit";

    /// <summary>Lease-kind tag distinguishing worker and claim renewals.</summary>
    public const string LeaseKindTagName = "cohesive.processes.distribution.lease.kind";

    /// <summary>Trace-only logical work identity attribute.</summary>
    public const string WorkIdTagName = "cohesive.processes.distribution.work.id";

    /// <summary>Trace-only worker-incarnation identity attribute.</summary>
    public const string WorkerIdTagName = "cohesive.processes.distribution.worker.id";

    /// <summary>Trace-only physical dispatch identity attribute.</summary>
    public const string DispatchIdTagName = "cohesive.processes.distribution.dispatch.id";

    /// <summary>Trace-only monotonic work-fence attribute.</summary>
    public const string FenceTagName = "cohesive.processes.distribution.fence";

    /// <summary>Records one provider or runtime distribution operation using bounded dimensions.</summary>
    /// <param name="operation">Stable operation name, preferably one of this catalog's activity names.</param>
    /// <param name="disposition">Observable ledger disposition.</param>
    /// <param name="duration">Non-negative elapsed duration.</param>
    /// <param name="pool">Optional configured pool identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="disposition"/> is unsupported or <paramref name="duration"/> is negative.
    /// </exception>
    public static void RecordOperation(
        string operation,
        ProcessDistributionDisposition disposition,
        TimeSpan duration,
        ProcessWorkerPoolId? pool = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!Enum.IsDefined(disposition) || disposition == ProcessDistributionDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "A distribution disposition is required.");
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Operation duration cannot be negative.");

        RecordOperationCore(operation, Disposition(disposition), duration, pool);
    }

    /// <summary>Records safe queue, worker-health, and reserved-capacity observations from a canonical pool snapshot.</summary>
    /// <param name="snapshot">Canonical pool snapshot to project.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static void RecordPoolSnapshot(ProcessWorkerPoolSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            var pool = snapshot.Pool.Id.Value;
            Runtime.PoolWork?.Record(snapshot.Queued, Tags(PoolTagName, pool, StatusTagName, "queued"));
            Runtime.PoolWork?.Record(snapshot.Claimed, Tags(PoolTagName, pool, StatusTagName, "claimed"));
            Runtime.PoolWork?.Record(
                snapshot.ReconciliationRequired,
                Tags(PoolTagName, pool, StatusTagName, "reconciliation_required"));
            Runtime.PoolWork?.Record(snapshot.Terminal, Tags(PoolTagName, pool, StatusTagName, "terminal"));
            Runtime.PoolWorkers?.Record(
                snapshot.HealthyWorkers,
                Tags(PoolTagName, pool, StatusTagName, "healthy"));
            Runtime.PoolWorkers?.Record(
                snapshot.DrainingWorkers,
                Tags(PoolTagName, pool, StatusTagName, "draining"));
            Runtime.PoolWorkers?.Record(
                snapshot.ExpiredWorkers,
                Tags(PoolTagName, pool, StatusTagName, "expired"));
            foreach (var capacity in snapshot.ReservedCapacity)
            {
                TagList tags = default;
                tags.Add(PoolTagName, pool);
                tags.Add(ResourceTagName, capacity.Resource);
                tags.Add(CapacityUnitTagName, capacity.Unit);
                Runtime.PoolReservedCapacity?.Record(capacity.Units, tags);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Synchronous metric observers are application extensions and cannot alter ledger semantics.
        }
    }

    internal static long StartTimer() => Runtime.IsEnabled ? Stopwatch.GetTimestamp() : 0L;

    internal static Activity? StartWorkerTurn(
        OperationContext context,
        ProcessWorkerPoolId pool,
        ProcessWorkerIncarnationId worker)
    {
        var activity = StartActivity(context, WorkerTurnActivityName, ActivityKind.Consumer);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(PoolTagName, pool.Value);
            activity.SetTag(WorkerIdTagName, worker.Value);
        }
        return activity;
    }

    internal static Activity? StartExecution(OperationContext context, ProcessWorkClaim claim)
    {
        var activity = StartActivity(context, WorkExecutionActivityName, ActivityKind.Consumer);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(PoolTagName, claim.Submission.Requirements.Pool.Value);
            activity.SetTag(WorkIdTagName, claim.Submission.Id.Value);
            activity.SetTag(WorkerIdTagName, claim.Worker.Value);
            activity.SetTag(DispatchIdTagName, claim.Dispatch.Value);
            activity.SetTag(FenceTagName, claim.Fence.Ordinal);
        }

        try
        {
            var queueDuration = claim.ClaimedAtUtc - claim.Submission.SubmittedAtUtc;
            var poolTags = Tags(PoolTagName, claim.Submission.Requirements.Pool.Value);
            Runtime.QueueDuration?.Record(Math.Max(0D, queueDuration.TotalSeconds), poolTags);
            Runtime.Attempts?.Record(claim.Attempt, poolTags);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Telemetry is best effort and cannot change claim execution.
        }
        return activity;
    }

    internal static void CompleteWorkerTurn(
        Activity? activity,
        long started,
        ProcessWorkerExecutionResult? result,
        Exception? exception)
    {
        var disposition = exception is not null
            ? "exception"
            : result is null
                ? "unknown"
                : WorkerDisposition(result.Disposition);
        CompleteActivity(
            activity,
            started,
            WorkerTurnActivityName,
            disposition,
            result?.Claim?.Submission.Requirements.Pool,
            exception);
    }

    internal static void CompleteExecution(
        Activity? activity,
        long started,
        ProcessWorkClaim claim,
        ProcessWorkerExecutionResult? result,
        Exception? exception)
    {
        var disposition = exception is not null
            ? "exception"
            : result is null
                ? "unknown"
                : WorkerDisposition(result.Disposition);
        CompleteActivity(
            activity,
            started,
            WorkExecutionActivityName,
            disposition,
            claim.Submission.Requirements.Pool,
            exception);

        if (result?.Ledger?.Work is not { } work || !IsTerminal(work.Status))
            return;
        try
        {
            Runtime.TerminalOutcomes?.Add(
                1,
                Tags(
                    PoolTagName,
                    claim.Submission.Requirements.Pool.Value,
                    StatusTagName,
                    WorkStatus(work.Status)));
        }
        catch (Exception telemetryException) when (IsRecoverable(telemetryException))
        {
            // Synchronous observers cannot alter terminal settlement.
        }
    }

    internal static void RecordLease(string kind, ProcessDistributionDisposition disposition, ProcessWorkerPoolId pool)
    {
        try
        {
            TagList tags = default;
            tags.Add(LeaseKindTagName, kind);
            tags.Add(DispositionTagName, Disposition(disposition));
            tags.Add(PoolTagName, pool.Value);
            Runtime.LeaseEvents?.Add(1, tags);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Lease evidence remains authoritative when a metric observer fails.
        }
    }

    internal static void RecordStoreRetry(int failedAttempt)
    {
        try
        {
            Runtime.StoreRetries?.Add(1, new KeyValuePair<string, object?>("retry.attempt", failedAttempt));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Provider retry behavior remains independent of metric observers.
        }
    }

    static Activity? StartActivity(OperationContext context, string name, ActivityKind kind)
    {
        try
        {
            return context.TraceContext is { } parent
                ? Runtime.Source?.StartActivity(name, kind, parent)
                : Runtime.Source?.StartActivity(name, kind);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    static void CompleteActivity(
        Activity? activity,
        long started,
        string operation,
        string disposition,
        ProcessWorkerPoolId? pool,
        Exception? exception)
    {
        try
        {
            activity?.SetTag(OperationTagName, operation);
            activity?.SetTag(DispositionTagName, disposition);
            if (exception is not null)
            {
                activity?.SetTag("error.type", exception.GetType().FullName);
                activity?.SetStatus(ActivityStatusCode.Error);
            }
            RecordOperationCore(
                operation,
                disposition,
                started == 0L ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started),
                pool);
        }
        catch (Exception telemetryException) when (IsRecoverable(telemetryException))
        {
            // Activity and metrics are subordinate to the operation being observed.
        }
        finally
        {
            try
            {
                activity?.Dispose();
            }
            catch (Exception telemetryException) when (IsRecoverable(telemetryException))
            {
                // Stop observers are best effort.
            }
        }
    }

    static void RecordOperationCore(
        string operation,
        string disposition,
        TimeSpan duration,
        ProcessWorkerPoolId? pool)
    {
        try
        {
            TagList tags = default;
            tags.Add(OperationTagName, operation);
            tags.Add(DispositionTagName, disposition);
            if (pool is { } poolId)
                tags.Add(PoolTagName, poolId.Value);
            Runtime.Operations?.Add(1, tags);
            Runtime.OperationDuration?.Record(duration.TotalSeconds, tags);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Synchronous observers cannot alter the observed distribution operation.
        }
    }

    static TagList Tags(string firstName, object? firstValue, string? secondName = null, object? secondValue = null)
    {
        TagList tags = default;
        tags.Add(firstName, firstValue);
        if (secondName is not null)
            tags.Add(secondName, secondValue);
        return tags;
    }

    static string Disposition(ProcessDistributionDisposition disposition) => disposition switch
    {
        ProcessDistributionDisposition.Applied => "applied",
        ProcessDistributionDisposition.Replayed => "replayed",
        ProcessDistributionDisposition.NotFound => "not_found",
        ProcessDistributionDisposition.IdentityConflict => "identity_conflict",
        ProcessDistributionDisposition.InvalidState => "invalid_state",
        ProcessDistributionDisposition.WorkerUnavailable => "worker_unavailable",
        ProcessDistributionDisposition.NoEligibleWork => "no_eligible_work",
        ProcessDistributionDisposition.StaleFence => "stale_fence",
        ProcessDistributionDisposition.LeaseExpired => "lease_expired",
        ProcessDistributionDisposition.Incompatible => "incompatible",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported disposition.")
    };

    static string WorkerDisposition(ProcessWorkerExecutionDisposition disposition) => disposition switch
    {
        ProcessWorkerExecutionDisposition.Idle => "idle",
        ProcessWorkerExecutionDisposition.Settled => "settled",
        ProcessWorkerExecutionDisposition.Fenced => "fenced",
        ProcessWorkerExecutionDisposition.WorkerUnavailable => "worker_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported worker disposition.")
    };

    static string WorkStatus(ProcessWorkStatus status) => status switch
    {
        ProcessWorkStatus.Succeeded => "succeeded",
        ProcessWorkStatus.Failed => "failed",
        ProcessWorkStatus.Cancelled => "cancelled",
        ProcessWorkStatus.Poisoned => "poisoned",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A terminal status is required.")
    };

    static bool IsTerminal(ProcessWorkStatus status) => status is ProcessWorkStatus.Succeeded
        or ProcessWorkStatus.Failed
        or ProcessWorkStatus.Cancelled
        or ProcessWorkStatus.Poisoned;

    static bool IsRecoverable(Exception exception) => exception is not (
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);

    static class Runtime
    {
        internal static readonly ActivitySource? Source;
        internal static readonly Meter? Meter;
        internal static readonly Counter<long>? Operations;
        internal static readonly Histogram<double>? OperationDuration;
        internal static readonly Histogram<double>? QueueDuration;
        internal static readonly Histogram<long>? Attempts;
        internal static readonly Counter<long>? LeaseEvents;
        internal static readonly Counter<long>? StoreRetries;
        internal static readonly Counter<long>? TerminalOutcomes;
        internal static readonly Gauge<long>? PoolWork;
        internal static readonly Gauge<long>? PoolWorkers;
        internal static readonly Gauge<long>? PoolReservedCapacity;

        static Runtime()
        {
            try
            {
                Source = new(ActivitySourceName);
                Meter = new(MeterName);
                Operations = Meter.CreateCounter<long>(OperationsInstrumentName, "{operation}");
                OperationDuration = Meter.CreateHistogram<double>(OperationDurationInstrumentName, "s");
                QueueDuration = Meter.CreateHistogram<double>(QueueDurationInstrumentName, "s");
                Attempts = Meter.CreateHistogram<long>(AttemptsInstrumentName, "{attempt}");
                LeaseEvents = Meter.CreateCounter<long>(LeaseEventsInstrumentName, "{event}");
                StoreRetries = Meter.CreateCounter<long>(StoreRetriesInstrumentName, "{retry}");
                TerminalOutcomes = Meter.CreateCounter<long>(TerminalOutcomesInstrumentName, "{work}");
                PoolWork = Meter.CreateGauge<long>(PoolWorkInstrumentName, "{work}");
                PoolWorkers = Meter.CreateGauge<long>(PoolWorkersInstrumentName, "{worker}");
                PoolReservedCapacity = Meter.CreateGauge<long>(PoolReservedCapacityInstrumentName, "{unit}");
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Instrument publication is an application extension and cannot prevent package initialization.
            }
        }

        internal static bool IsEnabled => (Source?.HasListeners() ?? false)
            || (Operations?.Enabled ?? false)
            || (OperationDuration?.Enabled ?? false)
            || (QueueDuration?.Enabled ?? false)
            || (Attempts?.Enabled ?? false)
            || (LeaseEvents?.Enabled ?? false)
            || (StoreRetries?.Enabled ?? false)
            || (TerminalOutcomes?.Enabled ?? false)
            || (PoolWork?.Enabled ?? false)
            || (PoolWorkers?.Enabled ?? false)
            || (PoolReservedCapacity?.Enabled ?? false);
    }
}
