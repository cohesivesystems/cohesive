using DurableTask.Core.Serializing;

namespace Cohesive.Adapters.DurableTask;

// These private wire projections deliberately retain the historical input and status type names so task-hub
// monitoring can read executions created by the retired adapter. They are deserialization shapes only and cannot
// start, resume, or otherwise interpret a Process definition.
sealed record DurableTaskProcessRequest(
    string? ProcessName,
    IReadOnlyDictionary<string, object?>? Parameters);

sealed record DurableTaskProcessOrchestrationStatus(
    string? ProcessName,
    ProcessExecutionStatus Status);

sealed record DurableTaskProcessOutput(object? Result);

static class DurableTaskProcessQuerySerialization
{
    public static DataConverter CreateDataConverter() => new DurableTaskSystemTextJsonDataConverter();
}
