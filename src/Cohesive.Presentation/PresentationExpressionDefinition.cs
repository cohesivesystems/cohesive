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
    /// <summary>Represents the predicate option.</summary>
    Predicate = 0,
    /// <summary>Represents the projection option.</summary>
    Projection = 1,
    /// <summary>Represents the metric option.</summary>
    Metric = 2,
    /// <summary>Represents the visibility option.</summary>
    Visibility = 3,
    /// <summary>Represents the enablement option.</summary>
    Enablement = 4,
    /// <summary>Represents the label option.</summary>
    Label = 5,
    /// <summary>Represents the navigation option.</summary>
    Navigation = 6,
    /// <summary>Represents the data source parameter option.</summary>
    DataSourceParameter = 7,
    /// <summary>Represents the state derivation option.</summary>
    StateDerivation = 8
}
