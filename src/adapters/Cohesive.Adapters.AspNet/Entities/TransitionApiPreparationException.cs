using Cohesive.Transitions.Compilation;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>Endpoint registration failed to prepare an authored transition; retains structured compiler evidence.</summary>
public sealed class TransitionApiPreparationException : PreparationException
{
    internal TransitionApiPreparationException(string operationName, TransitionCompilationResult compilation)
        : base("transition", "transition.preparation.semantic", $"Cannot prepare Transition for API operation '{operationName}': "
            + string.Join("; ", compilation.Validation.Diagnostics)) => Compilation = compilation;

    /// <summary>Exact compilation result, including stable diagnostic codes and locations.</summary>
    public TransitionCompilationResult Compilation { get; }
}
