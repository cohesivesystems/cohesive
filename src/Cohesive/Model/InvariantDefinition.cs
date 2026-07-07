using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Shared invariant definition used by transitions and relations.
/// </summary>
public sealed record InvariantDefinition
{
    /// <summary>
    /// Creates an invariant definition.
    /// </summary>
    [JsonConstructor]
    public InvariantDefinition(string name, Expr expression, string? message = null, EntityId? entity = null)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Expression = Guard.RequireNotNull(expression);
        Message = message;
        Entity = entity;
    }
    
    /// <summary>
    /// Invariant identifier or name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Optional entity id this invariant applies to (relations).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EntityId? Entity { get; init; }

    /// <summary>
    /// Invariant expression.
    /// </summary>
    public Expr Expression { get; init; }

    /// <summary>
    /// Optional explanatory message.
    /// </summary>
    public string? Message { get; init; }
}
