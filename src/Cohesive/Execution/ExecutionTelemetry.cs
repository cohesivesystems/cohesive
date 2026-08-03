using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cohesive.Execution;

/// <summary>Bounded execution activity families.</summary>
public enum ExecutionTelemetryActivityKind
{
    /// <summary>One finite Transition or Process activation.</summary>
    Activation = 0,

    /// <summary>Admission to or resumption from a durable wait.</summary>
    Wait = 1,

    /// <summary>Admission or disposition of a typed execution input.</summary>
    Signal = 2,

    /// <summary>One bounded physical retry attempt.</summary>
    Retry = 3,

    /// <summary>One durable checkpoint commit.</summary>
    Checkpoint = 4,

    /// <summary>One pure Control evaluation.</summary>
    ControlDecision = 5,

    /// <summary>One fenced Control actuation attempt.</summary>
    ControlActuation = 6,

    /// <summary>One materialization-status observation.</summary>
    Materialization = 7
}

/// <summary>Bounded terminal outcome used by execution activities and metrics.</summary>
public enum ExecutionTelemetryOutcome
{
    /// <summary>Existing state was observed without claiming a new semantic result.</summary>
    Observed = 0,

    /// <summary>The operation completed successfully.</summary>
    Succeeded = 1,

    /// <summary>The operation returned or propagated failure.</summary>
    Failed = 2,

    /// <summary>The operation observed cooperative cancellation.</summary>
    Cancelled = 3,

    /// <summary>The operation rejected input without applying it.</summary>
    Rejected = 4,

    /// <summary>The operation intentionally deferred work.</summary>
    Deferred = 5,

    /// <summary>The operation reused an existing durable result.</summary>
    Replayed = 6,

    /// <summary>The operation produced work that remains pending.</summary>
    Pending = 7
}

/// <summary>Stable, exporter-neutral tracing and metric contract for Cohesive execution.</summary>
/// <remarks>
/// Metric dimensions are closed and low-cardinality. Definition, instance, attempt, activation, generation,
/// physical-resource, payload, and evidence-reference identities are never metric tags. Exact explain and normalized
/// trace fingerprints are trace-only correlation attributes.
/// </remarks>
public static class ExecutionTelemetry
{
    const int Sha256HexLength = 64;

    /// <summary>Process-lifetime activity-source name owned by the execution kernel.</summary>
    public const string ActivitySourceName = "Cohesive.Execution";

    /// <summary>Process-lifetime meter name owned by the execution kernel.</summary>
    public const string MeterName = "Cohesive.Execution";

    /// <summary>Completed execution-status observation counter.</summary>
    public const string StatusObservationsInstrumentName = "cohesive.execution.status.observations";

    /// <summary>Observed completed-activation count histogram.</summary>
    public const string ActivationsInstrumentName = "cohesive.execution.activations";

    /// <summary>Observed durable-wait count histogram.</summary>
    public const string WaitsInstrumentName = "cohesive.execution.waits";

    /// <summary>Observed retained-input count histogram.</summary>
    public const string SignalsInstrumentName = "cohesive.execution.signals";

    /// <summary>Observed bounded-retry count histogram.</summary>
    public const string RetriesInstrumentName = "cohesive.execution.retries";

    /// <summary>Completed durable-checkpoint counter.</summary>
    public const string CheckpointsInstrumentName = "cohesive.execution.checkpoints";

    /// <summary>Observed ready or delayed backlog histogram.</summary>
    public const string BacklogInstrumentName = "cohesive.execution.backlog";

    /// <summary>Observed materialization lag histogram in seconds.</summary>
    public const string LagInstrumentName = "cohesive.execution.materialization.lag";

    /// <summary>Observed Control evidence counter.</summary>
    public const string ControlEventsInstrumentName = "cohesive.execution.control.events";

    /// <summary>Observed materialization shard count histogram.</summary>
    public const string ShardsInstrumentName = "cohesive.execution.materialization.shards";

    /// <summary>Observed materialization generation count histogram.</summary>
    public const string GenerationsInstrumentName = "cohesive.execution.materialization.generations";

    /// <summary>Activity and metric tag identifying a bounded terminal outcome.</summary>
    public const string OutcomeTagName = "cohesive.execution.outcome";

    /// <summary>Metric tag identifying a bounded snapshot category.</summary>
    public const string KindTagName = "cohesive.execution.kind";

    /// <summary>Metric tag identifying operational health.</summary>
    public const string HealthTagName = "cohesive.execution.health";

    /// <summary>Metric tag identifying current readiness.</summary>
    public const string ReadinessTagName = "cohesive.execution.readiness";

    /// <summary>Metric tag identifying protocol-neutral lifecycle-control mode.</summary>
    public const string ControlModeTagName = "cohesive.execution.control.mode";

    /// <summary>Metric tag distinguishing measured, recommended, and applied Control evidence.</summary>
    public const string AuthorityTagName = "cohesive.execution.authority";

    /// <summary>Trace-only attribute containing deterministic explain identity.</summary>
    public const string ExplainFingerprintTagName = "cohesive.execution.explain.fingerprint";

    /// <summary>Trace-only attribute containing normalized semantic-trace identity.</summary>
    public const string TraceFingerprintTagName = "cohesive.execution.trace.fingerprint";

    /// <summary>Trace-only attribute containing a sanitized failure type.</summary>
    public const string ErrorTypeTagName = "error.type";

    static readonly string? InstrumentationVersion =
        typeof(ExecutionTelemetry).Assembly.GetName().Version?.ToString();
    static readonly ActivitySource? Activities = CreateActivitySource();
    static readonly Meter Meter = new(MeterName, InstrumentationVersion);
    static readonly Counter<long>? StatusObservations = CreateCounter(
        StatusObservationsInstrumentName,
        "{observation}",
        "Safe execution-status observations projected from authoritative runtime state.");
    static readonly Histogram<long>? Activations = CreateHistogram(
        ActivationsInstrumentName,
        "{activation}",
        "Completed activation count observed in one execution status.");
    static readonly Histogram<long>? Waits = CreateHistogram(
        WaitsInstrumentName,
        "{wait}",
        "Durable wait count observed in one execution status.");
    static readonly Histogram<long>? Signals = CreateHistogram(
        SignalsInstrumentName,
        "{input}",
        "Retained typed-input count observed at a durable checkpoint.");
    static readonly Histogram<long>? Retries = CreateHistogram(
        RetriesInstrumentName,
        "{retry}",
        "Bounded retry count observed in authoritative execution state.");
    static readonly Counter<long>? Checkpoints = CreateCounter(
        CheckpointsInstrumentName,
        "{checkpoint}",
        "Completed durable execution checkpoints.");
    static readonly Histogram<long>? Backlog = CreateHistogram(
        BacklogInstrumentName,
        "{item}",
        "Ready, delayed, or durable-operation work observed in authoritative state.");
    static readonly Histogram<double>? Lag = CreateDoubleHistogram(
        LagInstrumentName,
        "s",
        "Materialization lag observed from provider or runtime evidence.");
    static readonly Counter<long>? ControlEvents = CreateCounter(
        ControlEventsInstrumentName,
        "{event}",
        "Measured, recommended, or applied Control evidence.");
    static readonly Histogram<long>? Shards = CreateHistogram(
        ShardsInstrumentName,
        "{shard}",
        "Materialization source-feed shards observed in status.");
    static readonly Histogram<long>? Generations = CreateHistogram(
        GenerationsInstrumentName,
        "{generation}",
        "Materialization generations observed in status.");

    /// <summary>Whether any execution activity or metric listener is currently enabled.</summary>
    public static bool IsEnabled => (Activities?.HasListeners() ?? false)
        || StatusObservations?.Enabled == true
        || Activations?.Enabled == true
        || Waits?.Enabled == true
        || Signals?.Enabled == true
        || Retries?.Enabled == true
        || Checkpoints?.Enabled == true
        || Backlog?.Enabled == true
        || Lag?.Enabled == true
        || ControlEvents?.Enabled == true
        || Shards?.Enabled == true
        || Generations?.Enabled == true;

    /// <summary>Starts one bounded execution activity and adds payload-free correlation when sampled.</summary>
    /// <param name="kind">Stable activity family.</param>
    /// <param name="explain">Optional deterministic explain artifact to correlate.</param>
    /// <param name="trace">Optional normalized semantic trace to correlate.</param>
    /// <returns>A started activity, or <see langword="null"/> when no listener requests one or an observer fails.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public static Activity? StartActivity(
        ExecutionTelemetryActivityKind kind,
        ExecutionExplainArtifact? explain = null,
        NormalizedExecutionTrace? trace = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported execution activity kind.");
        }

        if (!(Activities?.HasListeners() ?? false))
        {
            return null;
        }

        Activity? activity;
        try
        {
            activity = Activities.StartActivity(GetActivityName(kind), GetActivityKind(kind));
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            return null;
        }

        CorrelateActivity(activity, explain, trace);
        return activity;
    }

    /// <summary>Adds payload-free deterministic explain or normalized-trace correlation to a sampled activity.</summary>
    /// <remarks>
    /// This supports operations whose normalized trace exists only after interpretation. Invalid or mismatched
    /// correlation evidence is ignored, and observer failures cannot alter the observed execution.
    /// </remarks>
    /// <param name="activity">Started execution activity, or <see langword="null"/>.</param>
    /// <param name="explain">Optional deterministic explain artifact to correlate.</param>
    /// <param name="trace">Optional normalized semantic trace to correlate.</param>
    public static void CorrelateActivity(
        Activity? activity,
        ExecutionExplainArtifact? explain = null,
        NormalizedExecutionTrace? trace = null)
    {
        if (activity?.IsAllDataRequested != true)
        {
            return;
        }

        try
        {
            if (explain is not null)
            {
                TrySetFingerprint(activity, ExplainFingerprintTagName, explain.Fingerprint.Value);
            }

            if (trace is not null
                && (explain is null || trace.Definition == explain.Definition.Definition))
            {
                TrySetFingerprint(
                    activity,
                    TraceFingerprintTagName,
                    ExecutionTraceFingerprinter.ComputeSemantic(trace).Value);
            }
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            // Correlation is supplemental telemetry and cannot alter execution semantics.
        }
    }

    /// <summary>Completes and disposes one activity without recording failure messages or stack traces.</summary>
    /// <remarks>
    /// Failed and rejected outcomes set the activity error status. Cooperative cancellation remains a distinct
    /// bounded outcome without being reclassified as an execution failure.
    /// </remarks>
    /// <param name="activity">Activity returned by <see cref="StartActivity"/>, or <see langword="null"/>.</param>
    /// <param name="outcome">Bounded terminal outcome.</param>
    /// <param name="exception">Optional propagated failure; only its runtime type is recorded.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is unsupported.</exception>
    public static void CompleteActivity(
        Activity? activity,
        ExecutionTelemetryOutcome outcome,
        Exception? exception = null)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported execution telemetry outcome.");
        }

        if (activity is null)
        {
            return;
        }

        try
        {
            activity.SetTag(OutcomeTagName, GetOutcomeValue(outcome));
            if (exception is not null)
            {
                activity.SetTag(ErrorTypeTagName, exception.GetType().FullName);
            }

            activity.SetStatus(outcome is ExecutionTelemetryOutcome.Failed or ExecutionTelemetryOutcome.Rejected
                ? ActivityStatusCode.Error
                : ActivityStatusCode.Ok);
        }
        catch (Exception telemetryException) when (IsRecoverableObservabilityFailure(telemetryException))
        {
            // Telemetry listeners cannot alter the observed execution.
        }

        try
        {
            activity.Dispose();
        }
        catch (Exception telemetryException) when (IsRecoverableObservabilityFailure(telemetryException))
        {
            // Stop listeners are best effort for the same reason as recording listeners.
        }
    }

    /// <summary>Records bounded snapshot metrics from one safe execution-status observation.</summary>
    /// <param name="status">Existing safe execution-status authority.</param>
    /// <exception cref="ArgumentNullException"><paramref name="status"/> is <see langword="null"/>.</exception>
    public static void RecordStatus(ExecutionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!(StatusObservations?.Enabled == true
            || Activations?.Enabled == true
            || Waits?.Enabled == true
            || Retries?.Enabled == true
            || Backlog?.Enabled == true))
        {
            return;
        }

        var health = GetHealthValue(status.Runtime.Health);
        var readiness = GetReadinessValue(ExecutionHealthProjector.GetReadiness(status));
        if (StatusObservations?.Enabled == true)
        {
            TagList tags = default;
            tags.Add(HealthTagName, health);
            tags.Add(ReadinessTagName, readiness);
            tags.Add(ControlModeTagName, GetControlModeValue(status.ControlMode));
            tags.Add(OutcomeTagName, GetTerminalOutcomeValue(status.TerminalOutcome.Kind));
            Add(StatusObservations, 1, tags);
        }

        if (Activations?.Enabled == true)
        {
            long completed = 0;
            foreach (var attempt in status.Attempts)
            {
                if (long.MaxValue - completed < attempt.CompletedActivationCount)
                {
                    completed = long.MaxValue;
                    break;
                }
                completed += attempt.CompletedActivationCount;
            }
            TagList tags = default;
            tags.Add(OutcomeTagName, GetAttemptOutcomeValue(status.CurrentAttempt.Disposition));
            Record(Activations, completed, tags);
        }

        if (Waits?.Enabled == true && status.Runtime.WaitsDisclosure == ExecutionStatusDisclosure.Disclosed)
        {
            TagList tags = default;
            tags.Add(HealthTagName, health);
            Record(Waits, status.Runtime.Waits.Length, tags);
        }

        if (Retries?.Enabled == true)
        {
            TagList tags = default;
            tags.Add(KindTagName, "process_attempt");
            Record(Retries, status.Attempts.Length - 1L, tags);
        }

        if (Backlog?.Enabled == true
            && status.Runtime.DemandDisclosure == ExecutionStatusDisclosure.Disclosed
            && status.Runtime.Demand is { } demand)
        {
            TagList readyTags = default;
            readyTags.Add(KindTagName, "ready");
            Record(Backlog, demand.Ready, readyTags);
            TagList delayedTags = default;
            delayedTags.Add(KindTagName, "delayed");
            Record(Backlog, demand.Delayed, delayedTags);
        }
    }

    /// <summary>Records one durable checkpoint and its bounded aggregate signal, retry, and backlog counts.</summary>
    /// <param name="signalCount">Total retained typed inputs.</param>
    /// <param name="pendingSignalCount">Retained typed inputs without a durable disposition.</param>
    /// <param name="retryCount">Bounded physical retries retained by the checkpoint.</param>
    /// <param name="backlogCount">Pending durable work retained by the checkpoint.</param>
    /// <param name="outcome">Checkpoint observation outcome.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count or <paramref name="outcome"/> is invalid.</exception>
    public static void RecordCheckpoint(
        long signalCount,
        long pendingSignalCount,
        long retryCount,
        long backlogCount,
        ExecutionTelemetryOutcome outcome)
    {
        RequireNonNegative(signalCount, nameof(signalCount));
        RequireNonNegative(pendingSignalCount, nameof(pendingSignalCount));
        RequireNonNegative(retryCount, nameof(retryCount));
        RequireNonNegative(backlogCount, nameof(backlogCount));
        if (pendingSignalCount > signalCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pendingSignalCount), "Pending signals cannot exceed retained signals.");
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported checkpoint outcome.");
        }

        if (Checkpoints?.Enabled == true)
        {
            TagList tags = default;
            tags.Add(OutcomeTagName, GetOutcomeValue(outcome));
            Add(Checkpoints, 1, tags);
        }
        if (Signals?.Enabled == true)
        {
            TagList retainedTags = default;
            retainedTags.Add(KindTagName, "retained");
            Record(Signals, signalCount, retainedTags);
            TagList pendingTags = default;
            pendingTags.Add(KindTagName, "pending");
            Record(Signals, pendingSignalCount, pendingTags);
        }
        if (Retries?.Enabled == true)
        {
            TagList tags = default;
            tags.Add(KindTagName, "durable_operation");
            Record(Retries, retryCount, tags);
        }
        if (Backlog?.Enabled == true)
        {
            TagList tags = default;
            tags.Add(KindTagName, "durable_operation");
            Record(Backlog, backlogCount, tags);
        }
    }

    /// <summary>Records measured, recommended, or applied Control evidence with bounded dimensions.</summary>
    /// <param name="authority">Measured, recommended, or applied evidence authority.</param>
    /// <param name="outcome">Bounded evidence outcome.</param>
    /// <param name="count">Positive number of evidence items represented.</param>
    /// <exception cref="ArgumentOutOfRangeException">Authority, outcome, or count is unsupported.</exception>
    public static void RecordControl(
        ExecutionExplainEvidenceAuthority authority,
        ExecutionTelemetryOutcome outcome,
        long count = 1)
    {
        if (authority is not (ExecutionExplainEvidenceAuthority.Measured
            or ExecutionExplainEvidenceAuthority.Recommended
            or ExecutionExplainEvidenceAuthority.Applied))
        {
            throw new ArgumentOutOfRangeException(nameof(authority), authority, "Control telemetry requires measured, recommended, or applied authority.");
        }
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported Control outcome.");
        }

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Control event count must be positive.");
        }

        if (ControlEvents?.Enabled != true)
        {
            return;
        }

        TagList tags = default;
        tags.Add(AuthorityTagName, GetAuthorityValue(authority));
        tags.Add(OutcomeTagName, GetOutcomeValue(outcome));
        Add(ControlEvents, count, tags);
    }

    /// <summary>Records bounded materialization lag, backlog, shard, and generation observations.</summary>
    /// <param name="backlogCount">Pending retryable or source work.</param>
    /// <param name="lagMilliseconds">Observed non-negative lag in milliseconds, or null when unknown.</param>
    /// <param name="shardCount">Observed source-feed shard count.</param>
    /// <param name="generationCount">Observed generation count.</param>
    /// <param name="health">Projected aggregate materialization health.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count, lag, or health value is unsupported.</exception>
    public static void RecordMaterialization(
        long backlogCount,
        long? lagMilliseconds,
        long shardCount,
        long generationCount,
        ExecutionHealthStatus health)
    {
        RequireNonNegative(backlogCount, nameof(backlogCount));
        RequireNonNegative(shardCount, nameof(shardCount));
        RequireNonNegative(generationCount, nameof(generationCount));
        if (lagMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lagMilliseconds), lagMilliseconds, "Materialization lag cannot be negative.");
        }

        if (!Enum.IsDefined(health))
        {
            throw new ArgumentOutOfRangeException(nameof(health), health, "Unsupported materialization health.");
        }

        var healthValue = GetHealthValue(health);
        if (Backlog?.Enabled == true)
        {
            TagList tags = default;
            tags.Add(KindTagName, "materialization");
            tags.Add(HealthTagName, healthValue);
            Record(Backlog, backlogCount, tags);
        }
        if (Lag?.Enabled == true && lagMilliseconds is { } lag)
        {
            TagList tags = default;
            tags.Add(HealthTagName, healthValue);
            Record(Lag, lag / 1000d, tags);
        }
        if (Shards?.Enabled == true)
        {
            TagList tags = default;
            tags.Add(HealthTagName, healthValue);
            Record(Shards, shardCount, tags);
        }
        if (Generations?.Enabled == true)
        {
            TagList tags = default;
            tags.Add(HealthTagName, healthValue);
            Record(Generations, generationCount, tags);
        }
    }

    static ActivitySource? CreateActivitySource()
    {
        try
        {
            return new(ActivitySourceName, InstrumentationVersion);
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            return null;
        }
    }

    static Counter<long>? CreateCounter(string name, string unit, string description)
    {
        try
        {
            return Meter.CreateCounter<long>(name, unit, description);
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            return null;
        }
    }

    static Histogram<long>? CreateHistogram(string name, string unit, string description)
    {
        try
        {
            return Meter.CreateHistogram<long>(name, unit, description);
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            return null;
        }
    }

    static Histogram<double>? CreateDoubleHistogram(string name, string unit, string description)
    {
        try
        {
            return Meter.CreateHistogram<double>(name, unit, description);
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            return null;
        }
    }

    static void Add(Counter<long>? instrument, long value, in TagList tags)
    {
        try
        {
            instrument?.Add(value, tags);
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            // Synchronous metric listeners cannot alter execution semantics.
        }
    }

    static void Record(Histogram<long>? instrument, long value, in TagList tags)
    {
        try
        {
            instrument?.Record(value, tags);
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            // Synchronous metric listeners cannot alter execution semantics.
        }
    }

    static void Record(Histogram<double>? instrument, double value, in TagList tags)
    {
        try
        {
            instrument?.Record(value, tags);
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            // Synchronous metric listeners cannot alter execution semantics.
        }
    }

    static bool TrySetFingerprint(Activity activity, string tagName, string value)
    {
        if (value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        activity.SetTag(tagName, value);
        return true;
    }

    static string GetActivityName(ExecutionTelemetryActivityKind kind) => kind switch
    {
        ExecutionTelemetryActivityKind.Activation => "cohesive.execution.activation",
        ExecutionTelemetryActivityKind.Wait => "cohesive.execution.wait",
        ExecutionTelemetryActivityKind.Signal => "cohesive.execution.signal",
        ExecutionTelemetryActivityKind.Retry => "cohesive.execution.retry",
        ExecutionTelemetryActivityKind.Checkpoint => "cohesive.execution.checkpoint",
        ExecutionTelemetryActivityKind.ControlDecision => "cohesive.execution.control.decide",
        ExecutionTelemetryActivityKind.ControlActuation => "cohesive.execution.control.actuate",
        ExecutionTelemetryActivityKind.Materialization => "cohesive.execution.materialization.observe",
        _ => throw new UnreachableException()
    };

    static ActivityKind GetActivityKind(ExecutionTelemetryActivityKind kind) => kind switch
    {
        ExecutionTelemetryActivityKind.Signal => ActivityKind.Consumer,
        ExecutionTelemetryActivityKind.Retry => ActivityKind.Client,
        _ => ActivityKind.Internal
    };

    static string GetOutcomeValue(ExecutionTelemetryOutcome outcome) => outcome switch
    {
        ExecutionTelemetryOutcome.Observed => "observed",
        ExecutionTelemetryOutcome.Succeeded => "succeeded",
        ExecutionTelemetryOutcome.Failed => "failed",
        ExecutionTelemetryOutcome.Cancelled => "cancelled",
        ExecutionTelemetryOutcome.Rejected => "rejected",
        ExecutionTelemetryOutcome.Deferred => "deferred",
        ExecutionTelemetryOutcome.Replayed => "replayed",
        ExecutionTelemetryOutcome.Pending => "pending",
        _ => throw new UnreachableException()
    };

    static string GetAttemptOutcomeValue(ExecutionAttemptDisposition disposition) => disposition switch
    {
        ExecutionAttemptDisposition.Current => "current",
        ExecutionAttemptDisposition.Abandoned => "abandoned",
        ExecutionAttemptDisposition.Completed => "completed",
        ExecutionAttemptDisposition.Failed => "failed",
        ExecutionAttemptDisposition.Cancelled => "cancelled",
        ExecutionAttemptDisposition.Terminated => "terminated",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported attempt disposition.")
    };

    static string GetTerminalOutcomeValue(ExecutionTerminalOutcomeKind outcome) => outcome switch
    {
        ExecutionTerminalOutcomeKind.None => "none",
        ExecutionTerminalOutcomeKind.Completed => "completed",
        ExecutionTerminalOutcomeKind.Failed => "failed",
        ExecutionTerminalOutcomeKind.Cancelled => "cancelled",
        ExecutionTerminalOutcomeKind.Terminated => "terminated",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported terminal outcome.")
    };

    static string GetHealthValue(ExecutionHealthStatus health) => health switch
    {
        ExecutionHealthStatus.Unknown => "unknown",
        ExecutionHealthStatus.Healthy => "healthy",
        ExecutionHealthStatus.Degraded => "degraded",
        ExecutionHealthStatus.Unhealthy => "unhealthy",
        _ => throw new ArgumentOutOfRangeException(nameof(health), health, "Unsupported execution health.")
    };

    static string GetReadinessValue(ExecutionReadinessStatus readiness) => readiness switch
    {
        ExecutionReadinessStatus.Unknown => "unknown",
        ExecutionReadinessStatus.Ready => "ready",
        ExecutionReadinessStatus.NotReady => "not_ready",
        _ => throw new ArgumentOutOfRangeException(nameof(readiness), readiness, "Unsupported execution readiness.")
    };

    static string GetControlModeValue(ProcessControlMode mode) => mode switch
    {
        ProcessControlMode.Running => "running",
        ProcessControlMode.PauseRequested => "pause_requested",
        ProcessControlMode.Paused => "paused",
        ProcessControlMode.RestartRequested => "restart_requested",
        ProcessControlMode.CancellationRequested => "cancellation_requested",
        ProcessControlMode.Cancelled => "cancelled",
        ProcessControlMode.Terminated => "terminated",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Process control mode.")
    };

    static string GetAuthorityValue(ExecutionExplainEvidenceAuthority authority) => authority switch
    {
        ExecutionExplainEvidenceAuthority.Measured => "measured",
        ExecutionExplainEvidenceAuthority.Recommended => "recommended",
        ExecutionExplainEvidenceAuthority.Applied => "applied",
        _ => throw new UnreachableException()
    };

    static void RequireNonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Telemetry counts cannot be negative.");
        }
    }

    static bool IsRecoverableObservabilityFailure(Exception exception) => exception is not (
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);
}
