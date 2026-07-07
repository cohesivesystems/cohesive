namespace Cohesive.Processes.Runtime;

/// <summary>
/// Materialized effect dispatch and optional continuation application.
/// </summary>
public sealed record EffectExecution(
    EffectRequest Request,
    object? Result,
    TransitionResult? ContinuationTransition
);
