namespace Cohesive.Processes.Model;

/// <summary>
/// Typed transition invocation for process-authored entity writes.
/// </summary>
public sealed record ProcessEntityTransitionInvocation(
    ProcessEntityRef Entity,
    string TransitionName,
    object? Input = null,
    ProcessEffectSchedulingMode EffectScheduling = ProcessEffectSchedulingMode.AutoDispatch
);

/// <summary>
/// Batch of process-authored transition invocations.
/// </summary>
public sealed record ProcessEntityTransitionBatch(
    IReadOnlyList<ProcessEntityTransitionInvocation> Transitions
);

/// <summary>
/// Factory helpers for typed transition invocations.
/// </summary>
public static class ProcessEntityTransition
{
    /// <summary>
    /// Creates a typed transition invocation against the supplied entity definition.
    /// </summary>
    public static ProcessEntityTransitionInvocation For<TEntity, TInput>(Transition<TEntity, TInput> transition,
        string entityId,
        TInput input,
        string? partitionKey = null,
        ProcessEffectSchedulingMode effectScheduling = ProcessEffectSchedulingMode.AutoDispatch
        ) where TEntity : Entity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(transition);
        return new(
            Entity: new(
                entityType: transition.Entity.Definition.Name.Value,
                entityId: entityId,
                partitionKey: partitionKey
                ),
            TransitionName: transition.Name,
            Input: input,
            EffectScheduling: effectScheduling
            );
    }

    /// <summary>
    /// Creates a batch transition invocation.
    /// </summary>
    public static ProcessEntityTransitionBatch Batch(IEnumerable<ProcessEntityTransitionInvocation> transitions)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        return new([..transitions]);
    }
}
