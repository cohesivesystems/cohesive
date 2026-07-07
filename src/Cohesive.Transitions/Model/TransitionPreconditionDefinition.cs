using System.Text.Json.Serialization;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Named precondition that must hold before a transition can run.
/// </summary>
public sealed record TransitionPreconditionDefinition
{
    /// <summary>
    /// Creates a transition precondition.
    /// </summary>
    /// <param name="name">Precondition name</param>
    /// <param name="expression">Precondition expression</param>
    /// <param name="message">Optional explanatory message</param>
    [JsonConstructor]
    public TransitionPreconditionDefinition(string name, Expr expression, string? message = null)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(value: name);
        ArgumentNullException.ThrowIfNull(argument: expression);
        Expression = expression;
        Message = message;
    }

    /// <summary>
    /// Precondition name.
    /// </summary>
    public string Name { get; init; }
    
    /// <summary>
    /// Boolean precondition expression.
    /// </summary>
    public Expr Expression { get; init; }
    
    /// <summary>
    /// Optional explanatory message.
    /// </summary>
    public string? Message { get; init; }
}
