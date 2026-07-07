namespace Cohesive.Presentation;

/// <summary>
/// Standard semantic component roles for page-host projection targets.
/// </summary>
/// <remarks>
/// Page-host roles describe the adapter-level interpretation needed to mount a
/// routed page host. They are intentionally separate from concrete component
/// keys so target adapters can provide React, Blazor, native, or other
/// implementations without changing the presentation IR.
/// </remarks>
public static class PageHostComponentRoles
{
    /// <summary>
    /// Mounts a routed presentation surface whose body is projected from view IR.
    /// </summary>
    public const string RoutedSurface = "cohesive.presentation.page-host.routed-surface";

    /// <summary>
    /// Mounts a document workspace page host whose runtime coordinates document
    /// profiles, projections, and route parameters.
    /// </summary>
    public const string DocumentWorkspace = "cohesive.presentation.page-host.document-workspace";
}
