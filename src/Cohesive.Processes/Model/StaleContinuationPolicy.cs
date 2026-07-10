namespace Cohesive.Processes.Model;

/// <summary>
/// Policy for stale continuation snapshot tokens.
/// </summary>
public enum StaleContinuationPolicy
{
    /// <summary>Represents the fail option.</summary>
    Fail = 0,
    /// <summary>Represents the ignore option.</summary>
    Ignore = 1
}
