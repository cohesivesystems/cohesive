namespace Cohesive.Transitions.Model;

// TODO: SemanticRuleViolationException is used in places where it shouldn't be

/// <summary>
/// Base exception type for semantic rule violations.
/// </summary>
/// <param name="message">The message describing the violated semantic rule.</param>
public class SemanticRuleViolationException(string message) 
    : Exception(message: message);

/// <summary>
/// Raised when an entity state violates a declared invariant.
/// </summary>
/// <param name="invariantName">The stable name of the violated invariant.</param>
/// <param name="entityId">The identity of the entity whose state violated the invariant.</param>
public sealed class InvariantViolationException(string invariantName, string entityId) 
    : SemanticRuleViolationException(message: $"Invariant '{invariantName}' was violated for entity '{entityId}'.");
