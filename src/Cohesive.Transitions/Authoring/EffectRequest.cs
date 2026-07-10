namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Effect request emitted by transitions and workflows.
/// </summary>
/// <param name="Name">Effect request name.</param>
/// <param name="Payload">Optional effect request payload.</param>
/// <param name="Continuation">Optional continuation transition metadata.</param>
/// <param name="Snapshot">Optional snapshot token metadata used for continuation concurrency checks.</param>
public sealed record EffectRequest(
    string Name,
    ObservationValue Payload = default,
    EffectContinuation? Continuation = null,
    EffectSnapshot? Snapshot = null
    )
{
    /// <summary>
    /// Creates a named effect request with an optional payload.
    /// </summary>
    public static EffectRequest Named(
        string name,
        object? payload = null,
        EffectContinuation? continuation = null,
        EffectSnapshot? snapshot = null
        ) => new(
            Name: name,
            Payload: ObservationValue.FromObject(payload),
            Continuation: continuation,
            Snapshot: snapshot
            );
    
    /// <summary>Creates a named effect request.</summary>
    public static EffectRequest Named<TRequest>(
        TRequest? payload = default,
        EffectContinuation? continuation = null,
        EffectSnapshot? snapshot = null
    ) where TRequest : IEffectRequest => new(
        Name: TRequest.RequestName,
        Payload: ObservationValue.FromObject(payload),
        Continuation: continuation,
        Snapshot: snapshot
        );
}
