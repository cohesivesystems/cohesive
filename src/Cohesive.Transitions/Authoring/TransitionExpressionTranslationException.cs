namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Raised when a C# expression cannot be translated into the supported declarative expression subset.
/// </summary>
public sealed class TransitionExpressionTranslationException(string message) 
    : ArgumentException(message: message);
