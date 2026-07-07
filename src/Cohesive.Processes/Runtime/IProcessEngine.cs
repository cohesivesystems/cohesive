namespace Cohesive.Processes.Runtime;

/// <summary>
/// Starts and monitors durable process executions.
/// </summary>
public interface IProcessEngine
{
    /// <summary>
    /// Starts a process definition and returns the accepted execution identity.
    /// </summary>
    Task<ProcessStartResult> StartAsync(OperationContext context, ProcessDefinition process, IReadOnlyDictionary<string, object?>? parameters = null, ProcessRunOptions? runOptions = null);

    /// <summary>
    /// Returns the current execution state for a previously started process.
    /// </summary>
    Task<ProcessExecutionState?> GetStatusAsync(OperationContext context, string processId);

    /// <summary>
    /// Publishes an external signal to a running process execution.
    /// </summary>
    Task SignalAsync(OperationContext context, string processId, string signalKey, object? payload = null);

    /// <summary>
    /// Waits for a previously started process to complete successfully.
    /// </summary>
    Task<ProcessRunResult> WaitForCompletionAsync(OperationContext context, string processId);
}
