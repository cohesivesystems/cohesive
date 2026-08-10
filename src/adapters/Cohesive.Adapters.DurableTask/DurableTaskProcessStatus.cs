using Cohesive.Execution;
using Cohesive.Processes.Execution;
using DurableTask.Core;

namespace Cohesive.Adapters.DurableTask;

static class DurableTaskProcessStatus
{
    public static ExecutionStatus Project(DurableTaskSequentialProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ProcessExecutionStatusProjector.Project(
            result.State,
            result.Control,
            [.. result.DurableOperations.Select(static operation => operation.State)]);
    }

    public static ProcessExecutionStatus ResolveStatus(
        OrchestrationStatus orchestrationStatus,
        DurableTaskProcessOrchestrationStatus? customStatus,
        FailureDetails? failure = null
        )
    {
        if (failure is not null)
        {
            return ProcessExecutionStatus.Failed;
        }

        var runtimeStatus = MapStatus(orchestrationStatus);
        return IsRuntimeAuthoritative(orchestrationStatus)
            ? runtimeStatus
            : customStatus?.Status ?? runtimeStatus;
    }

    // The standalone client retains Canceled and ContinuedAsNew only as wire-compatibility states. They must still
    // be readable from migrated task-hub indexes even though the current interpreter does not produce them directly.
#pragma warning disable CS0618
    public static ProcessExecutionStatus ResolveStatus(
        Microsoft.DurableTask.Client.OrchestrationRuntimeStatus orchestrationStatus,
        ExecutionStatus? customStatus,
        Microsoft.DurableTask.TaskFailureDetails? failure = null)
    {
        if (failure is not null)
        {
            return ProcessExecutionStatus.Failed;
        }

        var runtimeStatus = MapStatus(orchestrationStatus);
        if (customStatus is null)
        {
            return runtimeStatus;
        }

        if (orchestrationStatus == Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Completed)
        {
            return customStatus.TerminalOutcome.Kind == ExecutionTerminalOutcomeKind.None
                ? runtimeStatus
                : MapStatus(customStatus);
        }

        return IsRuntimeAuthoritative(orchestrationStatus)
            ? runtimeStatus
            : MapStatus(customStatus);
    }

    public static bool IsTerminal(OrchestrationStatus status) =>
        status is OrchestrationStatus.Completed
        or OrchestrationStatus.Failed
        or OrchestrationStatus.Canceled
        or OrchestrationStatus.Terminated;

    public static bool IsTerminal(Microsoft.DurableTask.Client.OrchestrationRuntimeStatus status) =>
        status is Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Completed
        or Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Failed
        or Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Canceled
        or Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Terminated;

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

    public static ProcessExecutionStatus MapStatus(
        Microsoft.DurableTask.Client.OrchestrationRuntimeStatus status)
    {
        return status switch
        {
            Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Pending => ProcessExecutionStatus.Pending,
            Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Running => ProcessExecutionStatus.Running,
            Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Completed => ProcessExecutionStatus.Completed,
            Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.ContinuedAsNew => ProcessExecutionStatus.Running,
            Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Failed => ProcessExecutionStatus.Failed,
            Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Canceled => ProcessExecutionStatus.Cancelled,
            Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Terminated => ProcessExecutionStatus.Terminated,
            Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Suspended => ProcessExecutionStatus.Suspended,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unexpected durable orchestration status.")
        };
    }

    public static ProcessExecutionError? MapFailure(FailureDetails? failure)
    {
        if (failure is null)
        {
            return null;
        }

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

    static bool IsRuntimeAuthoritative(Microsoft.DurableTask.Client.OrchestrationRuntimeStatus status) =>
        status is Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Pending
        or Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Failed
        or Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Canceled
        or Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Terminated
        or Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Suspended;

    public static ProcessExecutionStatus MapStatus(ExecutionStatus status)
    {
        return status.TerminalOutcome.Kind switch
        {
            ExecutionTerminalOutcomeKind.Completed => ProcessExecutionStatus.Completed,
            ExecutionTerminalOutcomeKind.Failed => ProcessExecutionStatus.Failed,
            ExecutionTerminalOutcomeKind.Cancelled => ProcessExecutionStatus.Cancelled,
            ExecutionTerminalOutcomeKind.Terminated => ProcessExecutionStatus.Terminated,
            ExecutionTerminalOutcomeKind.None when status.ControlMode == ProcessControlMode.Paused =>
                ProcessExecutionStatus.Suspended,
            ExecutionTerminalOutcomeKind.None when status.Runtime.WaitsDisclosure == ExecutionStatusDisclosure.Disclosed
                && !status.Runtime.Waits.IsEmpty => ProcessExecutionStatus.Waiting,
            ExecutionTerminalOutcomeKind.None => ProcessExecutionStatus.Running,
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status.TerminalOutcome.Kind,
                "Unexpected canonical Process terminal outcome.")
        };
    }
#pragma warning restore CS0618

    static IReadOnlyDictionary<string, object?>? MapProperties(IDictionary<string, object?>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return null;
        }

        return properties.ToDictionary(
            static pair => pair.Key,
            static pair => (object?)pair.Value,
            StringComparer.Ordinal
            );
    }
}
