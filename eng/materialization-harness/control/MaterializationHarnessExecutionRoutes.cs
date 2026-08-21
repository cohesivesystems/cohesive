using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Processes.Runtime;

namespace Cohesive.MaterializationHarness.Control;

static class MaterializationHarnessExecutionRoutes
{
    internal const string Start = "/execution-control/processes/start";
    internal const string Pause = "/execution-control/processes/pause";
    internal const string Continue = "/execution-control/processes/continue";
    internal const string RestartAttempt = "/execution-control/processes/restart-attempt";
    internal const string Cancel = "/execution-control/processes/cancel";
    internal const string UpdateLimits = "/execution-control/processes/update-limits";
    internal const string RequestProjection =
        "/materialization-harness/providers/{provider}/control-requests/{operation}";
    internal const string FailureEvidence =
        "/materialization-harness/providers/{provider}/failure-evidence";
    internal const string Inspect = "/execution-control/processes/{processInstanceId}";
    internal const string Explain = "/execution-control/processes/{processInstanceId}/explain";
    internal const string Traces = "/execution-control/processes/{processInstanceId}/traces";

    internal static string Command(string operation) => operation switch
    {
        ProcessStartWireNames.Start => Start,
        ExecutionControlWireNames.Pause => Pause,
        ExecutionControlWireNames.Continue => Continue,
        ExecutionControlWireNames.RestartAttempt => RestartAttempt,
        ExecutionControlWireNames.Cancel => Cancel,
        ControlLimitUpdateWireNames.UpdateLimits => UpdateLimits,
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation),
            operation,
            "The operation has no materialization-harness command route.")
    };

    internal static string ProjectRequest(string provider, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return $"/materialization-harness/providers/{Uri.EscapeDataString(provider)}/control-requests/"
            + Uri.EscapeDataString(operation);
    }

    internal static string FailureEvidenceFor(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return $"/materialization-harness/providers/{Uri.EscapeDataString(provider)}/failure-evidence";
    }

    internal static string InspectFor(string processInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        return $"/execution-control/processes/{Uri.EscapeDataString(processInstanceId)}";
    }

    internal static string ExplainFor(string processInstanceId) =>
        $"{InspectFor(processInstanceId)}/explain";

    internal static string TracesFor(string processInstanceId) =>
        $"{InspectFor(processInstanceId)}/traces";
}

sealed record MaterializationHarnessControlRequestProjection(
    string Operation,
    string Method,
    string Route,
    object Request);
