namespace Cohesive.Transitions.Execution;

/// <summary>A known candidate-state preparation failure with a stable code and evidence location.</summary>
/// <param name="code">Machine-readable preparation failure code.</param>
/// <param name="location">JSON-pointer-style location of the rejected evidence or state.</param>
/// <param name="message">Explanation of the failed invariant.</param>
/// <param name="cause">Original validation failure, when preparation delegates entity validation.</param>
public sealed class TransitionStatePreparationException(string code, string location, string message,
    Exception? cause = null) : PreparationException("entityState", code, message, cause)
{
    /// <summary>Location of the rejected evidence or state.</summary>
    public string Location { get; } = location;
}
