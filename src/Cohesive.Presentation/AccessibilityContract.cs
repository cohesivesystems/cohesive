namespace Cohesive.Presentation;

/// <summary>
/// Defines target-independent accessibility semantics.
/// </summary>
/// <param name="Role">Semantic accessibility role.</param>
/// <param name="Label">Optional accessible label.</param>
/// <param name="Description">Optional accessible description.</param>
/// <param name="Keyboard">Optional keyboard interaction model key.</param>
/// <param name="Focus">Optional focus policy key.</param>
public sealed record AccessibilityContract(
    string Role,
    string? Label = null,
    string? Description = null,
    string? Keyboard = null,
    string? Focus = null
);