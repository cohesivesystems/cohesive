namespace Cohesive.Processes.Model;

/// <summary>
/// Isolation hints accepted by process transactions.
/// </summary>
public enum ProcessIsolationLevel
{
    ReadCommitted = 0,
    RepeatableRead = 1,
    Serializable = 2
}