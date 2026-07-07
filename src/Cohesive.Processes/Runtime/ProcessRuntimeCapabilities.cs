namespace Cohesive.Processes.Runtime;

/// <summary>
/// Process runtime host capabilities requested by an application host.
/// </summary>
[Flags]
public enum ProcessRuntimeCapabilities
{
    /// <summary>
    /// No process runtime capabilities are enabled.
    /// </summary>
    None = 0,

    /// <summary>
    /// The host can start, signal, and query process executions.
    /// </summary>
    Engine = 1,

    /// <summary>
    /// The host can execute process work.
    /// </summary>
    Worker = 2
}
