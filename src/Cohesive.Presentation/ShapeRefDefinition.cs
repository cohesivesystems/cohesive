namespace Cohesive.Presentation;

/// <summary>
/// References a semantic shape, type, or contract.
/// </summary>
/// <param name="ShapeId">Optional shape identifier.</param>
/// <param name="TypeId">Optional type identifier.</param>
/// <param name="ContractName">Optional CLR or generated contract name.</param>
public sealed record ShapeRefDefinition(
    string? ShapeId,
    string? TypeId,
    string? ContractName
);