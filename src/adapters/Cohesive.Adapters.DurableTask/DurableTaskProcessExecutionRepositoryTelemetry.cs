using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cohesive.Observability;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Stable tracing and metrics contract for Durable Task Process execution repository reads.</summary>
/// <remarks>
/// Query telemetry separates provider page acquisition from Cohesive projection so a host can compare adapter data
/// access with its enclosing API request. Metric dimensions are restricted to bounded client, phase, item-kind, and
/// outcome values. Task hubs, continuation tokens, Process identities, definitions, and retained payloads are never
/// metric dimensions or activity attributes.
/// </remarks>
public static class DurableTaskProcessExecutionRepositoryTelemetry
{
    /// <summary>Activity-source name emitted by the Durable Task Process execution repository.</summary>
    public const string ActivitySourceName = "Cohesive.Adapters.DurableTask.ProcessExecutionRepository";

    /// <summary>Meter name emitted by the Durable Task Process execution repository.</summary>
    public const string MeterName = "Cohesive.Adapters.DurableTask.ProcessExecutionRepository";

    /// <summary>Activity name for one complete repository query.</summary>
    public const string QueryActivityName = "cohesive.durable_task.process_execution.query";

    /// <summary>Client activity name for acquisition of one provider query page.</summary>
    public const string QueryProviderReadActivityName = "cohesive.durable_task.process_execution.query.provider.read";

    /// <summary>Internal activity name for projecting one provider page to canonical Process execution records.</summary>
    public const string QueryProjectionActivityName = "cohesive.durable_task.process_execution.query.project";

    /// <summary>Histogram of repository query phase durations in seconds.</summary>
    public const string QueryDurationInstrumentName = "cohesive.durable_task.process_execution.query.duration";

    /// <summary>Counter of failed repository query phases.</summary>
    public const string QueryFailuresInstrumentName = "cohesive.durable_task.process_execution.query.failures";

    /// <summary>Histogram of provider and returned item counts for one query page.</summary>
    public const string QueryItemsInstrumentName = "cohesive.durable_task.process_execution.query.items";

    /// <summary>Activity and metric tag distinguishing current standalone and migration-only Core clients.</summary>
    public const string ClientTagName = "cohesive.durable_task.client";

    /// <summary>Activity and metric tag identifying the measured query phase.</summary>
    public const string PhaseTagName = "cohesive.durable_task.query.phase";

    /// <summary>Activity and metric tag identifying the bounded terminal outcome.</summary>
    public const string OutcomeTagName = "cohesive.durable_task.outcome";

    /// <summary>Metric tag distinguishing provider-page and returned canonical item counts.</summary>
    public const string ItemKindTagName = "cohesive.durable_task.query.item.kind";

    /// <summary>Client value identifying the current standalone Durable Task SDK.</summary>
    public const string StandaloneClient = "standalone";

    /// <summary>Client value identifying the migration-only Durable Task Core query client.</summary>
    public const string CoreClient = "core";

    /// <summary>Phase value identifying the complete repository query.</summary>
    public const string TotalPhase = "total";

    /// <summary>Phase value identifying provider page acquisition.</summary>
    public const string ProviderReadPhase = "provider_read";

    /// <summary>Phase value identifying canonical Process execution projection.</summary>
    public const string ProjectionPhase = "projection";

    /// <summary>Item-kind value identifying records in the acquired provider page.</summary>
    public const string ProviderItems = "provider";

    /// <summary>Item-kind value identifying canonical records returned after filtering and projection.</summary>
    public const string ReturnedItems = "returned";

    /// <summary>Outcome value identifying successful completion.</summary>
    public const string SucceededOutcome = "succeeded";

    /// <summary>Outcome value identifying exceptional completion.</summary>
    public const string FailedOutcome = "failed";

    /// <summary>Outcome value identifying cooperative cancellation.</summary>
    public const string CancelledOutcome = "cancelled";

    static readonly string? InstrumentationVersion =
        typeof(DurableTaskProcessExecutionRepositoryTelemetry).Assembly.GetName().Version?.ToString();
    static readonly ActivitySource Activities = new(ActivitySourceName, InstrumentationVersion);
    static readonly Meter Meter = new(MeterName, InstrumentationVersion);
    static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>(
        QueryDurationInstrumentName,
        unit: "s",
        description: "Elapsed Durable Task Process execution repository query time by bounded phase.");
    static readonly Counter<long> QueryFailures = Meter.CreateCounter<long>(
        QueryFailuresInstrumentName,
        unit: "{failure}",
        description: "Failed Durable Task Process execution repository query phases.");
    static readonly Histogram<long> QueryItems = Meter.CreateHistogram<long>(
        QueryItemsInstrumentName,
        unit: "{item}",
        description: "Provider and canonical returned item counts for one repository query page.");
    static readonly OperationTelemetryEmitter Operations = new(Activities, QueryDuration, QueryFailures);

    internal static async ValueTask<T> ObserveAsync<T>(
        string activityName,
        string client,
        string phase,
        ActivityKind kind,
        CancellationToken cancellationToken,
        Func<ValueTask<T>> operation)
    {
        var telemetry = StartQuery(activityName, client, phase, kind);
        try
        {
            var result = await operation().ConfigureAwait(false);
            Complete(telemetry, SucceededOutcome);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteCancelled(telemetry);
            throw;
        }
        catch (Exception exception)
        {
            Complete(telemetry, FailedOutcome, exception);
            throw;
        }
    }

    internal static T Observe<T>(
        string activityName,
        string client,
        string phase,
        ActivityKind kind,
        CancellationToken cancellationToken,
        Func<T> operation)
    {
        var telemetry = StartQuery(activityName, client, phase, kind);
        try
        {
            var result = operation();
            Complete(telemetry, SucceededOutcome);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteCancelled(telemetry);
            throw;
        }
        catch (Exception exception)
        {
            Complete(telemetry, FailedOutcome, exception);
            throw;
        }
    }

    static QueryOperation StartQuery(string activityName, string client, string phase, ActivityKind kind) =>
        new(Operations.StartActivity(activityName, kind), Operations.StartTimer(), client, phase);

    static void Complete(in QueryOperation operation, string outcome, Exception? exception = null)
    {
        TagList tags = default;
        tags.Add(ClientTagName, operation.Client);
        tags.Add(PhaseTagName, operation.Phase);
        tags.Add(OutcomeTagName, outcome);
        Operations.CompleteOperation(
            operation.Activity,
            operation.Started,
            exception is null ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
            tags,
            exception);
    }

    static void CompleteCancelled(in QueryOperation operation)
    {
        TagList tags = default;
        tags.Add(ClientTagName, operation.Client);
        tags.Add(PhaseTagName, operation.Phase);
        tags.Add(OutcomeTagName, CancelledOutcome);
        Operations.CompleteOperation(
            operation.Activity,
            operation.Started,
            ActivityStatusCode.Unset,
            tags);
    }

    internal static void RecordItems(string client, string kind, int count)
    {
        if (!QueryItems.Enabled)
            return;

        TagList tags = default;
        tags.Add(ClientTagName, client);
        tags.Add(ItemKindTagName, kind);
        try
        {
            QueryItems.Record(count, tags);
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            // Synchronous metric observers cannot alter repository semantics.
        }
    }

    static bool IsRecoverableObservabilityFailure(Exception exception) => exception is not (
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);

    readonly record struct QueryOperation(
        Activity? Activity,
        long Started,
        string Client,
        string Phase);
}
