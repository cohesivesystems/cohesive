namespace Cohesive.Presentation;

/// <summary>
/// Standard semantic component roles for workspace runtime projection targets.
/// </summary>
/// <remarks>
/// Workspace-runtime roles name the adapter-level runtime interpretation needed
/// to coordinate a semantic workspace. They are intentionally distinct from
/// concrete component keys so the same presentation IR can target React,
/// Blazor, native, or other component systems.
/// </remarks>
public static class WorkspaceRuntimeComponentRoles
{
    /// <summary>
    /// Coordinates a document workspace runtime, including document profiles,
    /// projections, layout state, and cross-projection selection state.
    /// </summary>
    public const string DocumentWorkspace = "cohesive.presentation.workspace-runtime.document-workspace";
}
