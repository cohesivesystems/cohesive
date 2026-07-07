using System.Text.Json.Serialization;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Declarative effect request emitted by a transition.
/// </summary>
public sealed record EffectDefinition
{
    /// <summary>
    /// Creates an effect definition.
    /// </summary>
    /// <param name="name">Effect name</param>
    /// <param name="payload">Optional effect payload</param>
    /// <param name="continuation">Optional continuation transition to invoke after request execution.</param>
    [JsonConstructor]
    public EffectDefinition(string name, Expr? payload = null, EffectContinuationDefinition? continuation = null)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(value: name);
        Payload = payload;
        Continuation = continuation;
    }

    /// <summary>
    /// Effect name.
    /// </summary>
    public string Name { get; init; }
    
    /// <summary>
    /// Optional effect payload expression.
    /// </summary>
    public Expr? Payload { get; init; }

    /// <summary>
    /// Optional continuation transition definition.
    /// </summary>
    public EffectContinuationDefinition? Continuation { get; init; }
}
