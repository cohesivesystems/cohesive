namespace Cohesive.Presentation;

/// <summary>
/// Standard semantic component roles for document workspace surface-slot renderers.
/// </summary>
/// <remarks>
/// These roles name target-adapter interpretations for whole workspace slots,
/// not the lower-level slot container primitive. A frontend can bind them to
/// React, Blazor, native UI, or another component stack while preserving the
/// same workspace surface semantics in the presentation IR.
/// </remarks>
public static class DocumentWorkspaceSurfaceSlotComponentRoles
{
    /// <summary>
    /// Renders the document workspace header surface.
    /// </summary>
    public const string Header = "cohesive.presentation.document-workspace.surface-slot.header";

    /// <summary>
    /// Renders the primary document workspace editing surface.
    /// </summary>
    public const string PrimarySurface = "cohesive.presentation.document-workspace.surface-slot.primary-surface";

    /// <summary>
    /// Renders auxiliary document workspace content such as prompts and overlays.
    /// </summary>
    public const string Auxiliary = "cohesive.presentation.document-workspace.surface-slot.auxiliary";
}
