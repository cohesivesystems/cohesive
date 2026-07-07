namespace Cohesive.Presentation;

/// <summary>
/// Standard semantic component roles for prompt child-view projection targets.
/// </summary>
/// <remarks>
/// Prompt child-view roles name reusable prompt content interpretations such
/// as review diffs and generated document previews. They are target-adapter
/// roles rather than concrete React, Blazor, or native component names.
/// </remarks>
public static class PromptChildViewComponentRoles
{
    /// <summary>
    /// Renders a JSON document diff review surface inside a prompt.
    /// </summary>
    public const string JsonDocumentDiff = "cohesive.presentation.prompt-child-view.json-document-diff";

    /// <summary>
    /// Renders a generated document preview surface inside a prompt.
    /// </summary>
    public const string PromptDocumentPreview = "cohesive.presentation.prompt-child-view.document-preview";
}
