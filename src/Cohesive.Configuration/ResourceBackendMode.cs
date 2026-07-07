namespace Cohesive.Configuration;

/// <summary>
/// The resource backend mode.
/// </summary>
/// <remarks>
/// The resource backend mode is orthogonal to the host environment and configuration profile.
/// It classifies resource backends to facilitate testing and local execution in contrast to remote and cloud-based execution.
/// Think of this as a hierarchy, akin to a memory hierarchy with the in-memory mode being the cheapest and most ephemeral,
/// while the remote mode is the most expensive and persistent.
/// The implementation details of each resource are specified elsewhere in the configuration.
/// </remarks>
public enum ResourceBackendMode
{
    /// <summary>
    /// An in-process memory resource backend.
    /// Typically used for testing and simulation.
    /// </summary>
    InMemory = 0,
    
    /// <summary>
    /// A local resource backend (emulator, file system, process).
    /// Can be used for development and testing.
    /// Can emulate a remote resource backend or can perform real but local operations.
    /// </summary>
    Local = 1,
    
    /// <summary>
    /// A remote and persistent resource backend (cloud, on-premise).
    /// </summary>
    Remote = 2
}