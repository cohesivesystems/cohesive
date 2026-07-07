namespace Cohesive.Processes.Model;

/// <summary>
/// Execution place and available capabilities.
/// </summary>
public sealed class ProcessPlace
{
    readonly HashSet<ProcessCapability> capabilities;

    /// <summary>
    /// Creates a process place.
    /// </summary>
    public ProcessPlace(string name, IEnumerable<ProcessCapability> capabilities)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        this.capabilities = [.. Guard.RequireNotNull(capabilities)];
    }

    /// <summary>
    /// Place name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Place capabilities.
    /// </summary>
    public IReadOnlySet<ProcessCapability> Capabilities => capabilities;

    /// <summary>
    /// Returns true when the place supports the requested capability.
    /// </summary>
    public bool HasCapability(ProcessCapability capability) => 
        capabilities.Contains(capability);

    /// <summary>
    /// Creates a place containing all capabilities.
    /// </summary>
    public static ProcessPlace WithAllCapabilities(string name) => 
        new(name, Enum.GetValues<ProcessCapability>());
}