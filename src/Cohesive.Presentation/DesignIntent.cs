namespace Cohesive.Presentation;

/// <summary>
/// Defines target-independent design intent.
/// </summary>
/// <param name="Role">Semantic design role.</param>
/// <param name="Variant">Semantic design variant.</param>
/// <param name="Tone">Semantic tone.</param>
/// <param name="Density">Semantic density.</param>
/// <param name="Size">Semantic size.</param>
/// <param name="Layout">Optional layout hint.</param>
public sealed record DesignIntent(
    string Role,
    string Variant,
    string Tone,
    string Density,
    string Size,
    string? Layout = null
);