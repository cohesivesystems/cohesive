namespace Cohesive.Transitions.Execution;

/// <summary>A known candidate-state preparation failure with a stable code and evidence location.</summary>
/// <param name="code">Machine-readable preparation failure code.</param>
/// <param name="location">JSON-pointer-style location of the rejected evidence or state.</param>
/// <param name="message">Explanation of the failed invariant.</param>
/// <param name="cause">Original validation failure, when preparation delegates entity validation.</param>
public sealed class TransitionStatePreparationException(string code, string location, string message,
    Exception? cause = null) : PreparationException("entityState", code, message, cause)
{
    /// <summary>Sanitized description for external result projections; excludes provider and entity values.</summary>
    public static string SafeMessage => "The operation could not prepare a valid entity state. No change was committed.";

    /// <summary>Location of the rejected evidence or state.</summary>
    public string Location { get; } = location;
}
