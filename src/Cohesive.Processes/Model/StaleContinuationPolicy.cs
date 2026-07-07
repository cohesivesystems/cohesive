namespace Cohesive.Processes.Model;

/// <summary>
/// Policy for stale continuation snapshot tokens.
/// </summary>
public enum StaleContinuationPolicy
{
    Fail = 0,
    Ignore = 1
}
