namespace Cohesive.Processes.Runtime;

/// <summary>
/// Persisted effect request emitted by committed transitions.
/// </summary>
public sealed record PersistedProcessEffect(
    string ProcessId,
    ProcessEntityRef Entity,
    string TransitionName,
    EffectRequest Request,
    DateTimeOffset PersistedAtUtc
);