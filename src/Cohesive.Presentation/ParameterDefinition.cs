namespace Cohesive.Presentation;

/// <summary>
/// Defines a parameter accepted by a data source, action, or flow.
/// </summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Type">Parameter type name.</param>
/// <param name="IsRequired">Whether the parameter must be supplied.</param>
/// <param name="Label">Optional human-readable parameter label.</param>
/// <param name="DefaultValue">Optional default value encoded as text.</param>
public sealed record ParameterDefinition(
    string Name,
    string Type,
    bool IsRequired,
    string? Label = null,
    string? DefaultValue = null
);