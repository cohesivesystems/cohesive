namespace Cohesive.Adapters.TypeScript;

/// <summary>
/// Describes a TypeScript module that owns generated declarations for a subset of shape graph types.
/// </summary>
public sealed record TypeScriptExternalTypeModule
{
    /// <summary>
    /// TypeId prefix whose matching named types should be imported from the external module.
    /// </summary>
    public required string TypeIdPrefix { get; init; }

    /// <summary>
    /// Optional ShapeId prefix whose matching root shapes should be imported from the external module.
    /// </summary>
    public string? ShapeIdPrefix { get; init; }

    /// <summary>
    /// TypeScript import path for the external module.
    /// </summary>
    public required string ImportPath { get; init; }
}
