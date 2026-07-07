using System.Text.Json.Serialization;
using Cohesive.Model;

namespace Cohesive.Presentation;

/// <summary>
/// Defines a named expression used by presentation policies, derived state, generated target code, or data projections.
/// </summary>
/// <param name="Id">Stable expression identifier.</param>
/// <param name="Name">Human-readable expression name.</param>
/// <param name="Expression">Canonical Cohesive expression IR.</param>
/// <param name="ReturnType">Optional semantic return type.</param>
/// <param name="ParameterNames">Named parameters expected by the expression.</param>
/// <param name="Usage">Where the expression is used.</param>
/// <param name="Annotations">Open annotations for expression-level extension data.</param>
public sealed record PresentationExpressionDefinition(
    string Id,
    string Name,
    Expr Expression,
    TypeRef? ReturnType,
    string[] ParameterNames,
    PresentationExpressionUsage Usage,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies where a presentation expression is used.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PresentationExpressionUsage
{
    Predicate = 0,
    Projection = 1,
    Metric = 2,
    Visibility = 3,
    Enablement = 4,
    Label = 5,
    Navigation = 6,
    DataSourceParameter = 7,
    StateDerivation = 8
}
