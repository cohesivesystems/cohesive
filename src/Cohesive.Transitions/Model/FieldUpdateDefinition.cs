using System.Text.Json.Serialization;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Declarative field assignment performed by a transition.
/// </summary>
public sealed record FieldUpdateDefinition
{
    /// <summary>
    /// Creates a field update definition.
    /// </summary>
    [JsonConstructor]
    public FieldUpdateDefinition(string field, Expr valueExpression)
    {
        Field = Guard.RequireNotNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(argument: valueExpression);
        ValueExpression = valueExpression;
    }

    /// <summary>
    /// Target field name.
    /// </summary>
    public string Field { get; init; }
    
    /// <summary>
    /// New value expression.
    /// </summary>
    public Expr ValueExpression { get; init; }
}
