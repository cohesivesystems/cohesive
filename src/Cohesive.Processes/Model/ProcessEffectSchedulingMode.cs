namespace Cohesive.Processes.Model;

/// <summary>
/// Effect scheduling strategy for transition-emitted requests.
/// </summary>
public enum ProcessEffectSchedulingMode
{
    AutoDispatch = 0,
    Deferred = 1
}