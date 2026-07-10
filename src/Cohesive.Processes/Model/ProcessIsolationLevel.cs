namespace Cohesive.Processes.Model;

/// <summary>
/// Isolation hints accepted by process transactions.
/// </summary>
public enum ProcessIsolationLevel
{
    /// <summary>Represents the read committed option.</summary>
    ReadCommitted = 0,
    /// <summary>Represents the repeatable read option.</summary>
    RepeatableRead = 1,
    /// <summary>Represents the serializable option.</summary>
    Serializable = 2
}
