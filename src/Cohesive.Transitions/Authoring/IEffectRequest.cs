namespace Cohesive.Transitions.Authoring;

/// <summary>
///  Marker for typed effect requests.
/// </summary>
/// <remarks>
/// Compatibility contract retained for the flat Transition effect surface through ARI-218. Canonical interactions
/// are exact execution-definition references carried by Transition emission intents.
/// </remarks>
public interface IEffectRequest
{
    /// <summary>
    /// Stable request name used for dispatch.
    /// </summary>
    static abstract string RequestName { get; }
}

/// <summary>
/// Instance-side marker for typed effect request payloads.
/// </summary>
/// <typeparam name="TResult">Effect response type.</typeparam>
public interface IEffectRequestPayload<out TResult>
{
}

/// <summary>
/// Marker for typed effect requests that produce a typed response.
/// </summary>
/// <typeparam name="TResult">Effect response type.</typeparam>
public interface IEffectRequest<out TResult> : IEffectRequest, IEffectRequestPayload<TResult>
{
}
