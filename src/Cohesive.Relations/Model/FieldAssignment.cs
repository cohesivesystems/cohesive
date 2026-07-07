using System.Text.Json.Serialization;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Model;

/// <summary>
/// Assignment: target field receives expression value.
/// </summary>
public sealed record FieldAssignment
{
    /// <summary>
    /// Creates a field assignment.
    /// </summary>
    [JsonConstructor]
    public FieldAssignment(string targetField, Expr expr, string? id = null)
    {
        TargetField = Guard.RequireNotNullOrWhiteSpace(targetField);
        Expr = Guard.RequireNotNull(expr);
        Id = id;
    }

    /// <summary>
    /// Optional stable assignment id for explainability.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Target field name.
    /// </summary>
    public string TargetField { get; init; }

    /// <summary>
    /// Expression evaluated on the source in context <see cref="RelationEvaluationContext"/>, to be assigned to the target field.
    /// </summary>
    public Expr Expr { get; init; }
}
