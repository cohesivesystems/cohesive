namespace Cohesive.Execution;

/// <summary>Recovery lifetime required of a Process invocation, independent of entity persistence.</summary>
public enum ProcessExecutionLifetime
{
    /// <summary>Invocation-local execution without restart or background continuation.</summary>
    Ephemeral = 1,
    /// <summary>Execution admitted through a durable recovery authority.</summary>
    Durable = 2,
    /// <summary>Host-owned background execution and retained results, lost when that host exits.</summary>
    Host = 3
}

