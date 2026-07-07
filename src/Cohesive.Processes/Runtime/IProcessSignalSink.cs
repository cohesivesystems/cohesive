namespace Cohesive.Processes.Runtime;

/// <summary>
/// Publishes external process signals.
/// </summary>
public interface IProcessSignalSink
{
    /// <summary>
    /// Publishes a signal payload keyed for a waiting process node.
    /// </summary>
    Task PublishAsync(OperationContext context, string key, object? payload);
}
