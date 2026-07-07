using System.Text.Json.Serialization;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Typed transition input parameter.
/// </summary>
public sealed record TransitionParameterDefinition
{
    /// <summary>
    /// Creates a transition parameter definition.
    /// </summary>
    [JsonConstructor]
    public TransitionParameterDefinition(string name, TypeRef type, bool isRequired = true, string? description = null)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(value: name);
        ArgumentNullException.ThrowIfNull(argument: type);
        Type = type;
        IsRequired = isRequired;
        Description = description;
    }

    /// <summary>
    /// Parameter name.
    /// </summary>
    public string Name { get; init; }
    
    /// <summary>
    /// Parameter type.
    /// </summary>
    public TypeRef Type { get; init; }
    
    /// <summary>
    /// Whether the parameter must be supplied.
    /// </summary>
    public bool IsRequired { get; init; }
    
    /// <summary>
    /// Optional descriptive text.
    /// </summary>
    public string? Description { get; init; }
}
