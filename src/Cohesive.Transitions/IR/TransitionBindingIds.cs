namespace Cohesive.Transitions.IR;

/// <summary>
/// Stable built-in binding identities owned by canonical Transition IR and shared by every producer and
/// interpretation.
/// </summary>
public static class TransitionBindingIds
{
    /// <summary>The complete typed invocation input.</summary>
    public static ValueBindingId Input { get; } = new("transition.input");

    /// <summary>The coherent finite aggregate observation and implicit field root.</summary>
    public static ValueBindingId Observation { get; } = new("transition.observation");
}
