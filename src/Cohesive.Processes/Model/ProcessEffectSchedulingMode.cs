namespace Cohesive.Processes.Model;

/// <summary>
/// Effect scheduling strategy for transition-emitted requests.
/// </summary>
public enum ProcessEffectSchedulingMode
{
    /// <summary>Represents the auto dispatch option.</summary>
    AutoDispatch = 0,
    /// <summary>Represents the deferred option.</summary>
    Deferred = 1
}
