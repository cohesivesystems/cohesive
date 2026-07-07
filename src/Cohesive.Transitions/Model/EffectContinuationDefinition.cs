using System.Text.Json.Serialization;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Declarative continuation metadata for an emitted effect request.
/// </summary>
public sealed record EffectContinuationDefinition
{
    /// <summary>
    /// Creates continuation metadata.
    /// </summary>
    [JsonConstructor]
    public EffectContinuationDefinition(string transitionName)
    {
        TransitionName = Guard.RequireNotNullOrWhiteSpace(value: transitionName);
    }

    /// <summary>
    /// Creates continuation metadata from a transition definition reference.
    /// </summary>
    public EffectContinuationDefinition(TransitionDefinition transition)
    {
        Transition = Guard.RequireNotNull(transition);
        TransitionName = transition.Name;
    }

    /// <summary>
    /// Transition to invoke after effect execution.
    /// </summary>
    public string TransitionName { get; init; }

    /// <summary>
    /// Optional direct transition definition reference.
    /// </summary>
    [JsonIgnore]
    public TransitionDefinition? Transition { get; init; }
}
