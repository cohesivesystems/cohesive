using DurableTask.Core;

namespace Cohesive.Adapters.DurableTask;

static class DurableTaskProcessStatus
{
    public static ProcessExecutionStatus ResolveStatus(
        OrchestrationStatus orchestrationStatus,
        DurableTaskProcessOrchestrationStatus? customStatus,
        FailureDetails? failure = null
        )
    {
        if (failure is not null)
            return ProcessExecutionStatus.Failed;

        var runtimeStatus = MapStatus(orchestrationStatus);
        return IsRuntimeAuthoritative(orchestrationStatus)
            ? runtimeStatus
            : customStatus?.Status ?? runtimeStatus;
    }

    public static bool IsTerminal(OrchestrationStatus status) =>
        status is OrchestrationStatus.Completed
        or OrchestrationStatus.Failed
        or OrchestrationStatus.Canceled
        or OrchestrationStatus.Terminated;

    public static ProcessExecutionStatus MapStatus(OrchestrationStatus status)
    {
        return status switch
        {
            OrchestrationStatus.Pending => ProcessExecutionStatus.Pending,
            OrchestrationStatus.Running => ProcessExecutionStatus.Running,
            OrchestrationStatus.Completed => ProcessExecutionStatus.Completed,
            OrchestrationStatus.ContinuedAsNew => ProcessExecutionStatus.Running,
            OrchestrationStatus.Failed => ProcessExecutionStatus.Failed,
            OrchestrationStatus.Canceled => ProcessExecutionStatus.Cancelled,
            OrchestrationStatus.Terminated => ProcessExecutionStatus.Terminated,
            OrchestrationStatus.Suspended => ProcessExecutionStatus.Suspended,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unexpected durable orchestration status.")
        };
    }

    public static ProcessExecutionError? MapFailure(FailureDetails? failure)
    {
        if (failure is null)
            return null;

        return new(
            ErrorType: failure.ErrorType,
            ErrorMessage: failure.ErrorMessage,
            StackTrace: failure.StackTrace,
            IsNonRetriable: failure.IsNonRetriable,
            Properties: MapProperties(failure.Properties),
            InnerError: MapFailure(failure.InnerFailure)
            );
    }

    public static string? FormatFailureMessage(FailureDetails? failure) =>
        string.IsNullOrWhiteSpace(failure?.ErrorMessage) ? failure?.ToString() : failure.ErrorMessage;

    public static DateTimeOffset? ToCompletedAtUtc(DateTime value) =>
        value == default ? null : value.ToDateTimeOffsetUtc();

    static bool IsRuntimeAuthoritative(OrchestrationStatus status) =>
        status is OrchestrationStatus.Pending
        or OrchestrationStatus.Completed
        or OrchestrationStatus.Failed
        or OrchestrationStatus.Canceled
        or OrchestrationStatus.Terminated
        or OrchestrationStatus.Suspended;

    static IReadOnlyDictionary<string, object?>? MapProperties(IDictionary<string, object?>? properties)
    {
        if (properties is null || properties.Count == 0)
            return null;

        return properties.ToDictionary(
            static pair => pair.Key,
            static pair => (object?)pair.Value,
            StringComparer.Ordinal
            );
    }
}
