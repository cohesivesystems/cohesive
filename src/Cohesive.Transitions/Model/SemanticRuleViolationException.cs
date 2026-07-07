namespace Cohesive.Transitions.Model;

// TODO: SemanticRuleViolationException is used in places where it shouldn't be

/// <summary>
/// Base exception type for semantic rule violations.
/// </summary>
public class SemanticRuleViolationException(string message) 
    : Exception(message: message);

/// <summary>
/// Raised when transition preconditions are not satisfied.
/// </summary>
public sealed class TransitionPreconditionException(string transitionName, string entityId) 
    : SemanticRuleViolationException(message: $"Transition '{transitionName}' precondition failed for entity '{entityId}'.");

/// <summary>
/// Raised when a transition or restore violates an invariant.
/// </summary>
public sealed class InvariantViolationException(string invariantName, string entityId) 
    : SemanticRuleViolationException(message: $"Invariant '{invariantName}' was violated for entity '{entityId}'.");
