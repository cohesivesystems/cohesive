namespace Cohesive.Presentation;

/// <summary>
/// Standard semantic component roles for navigation-shell region projection.
/// </summary>
/// <remarks>
/// Shell-region roles describe persistent app-shell capabilities independently
/// of the concrete frontend component used to render them. Projection targets
/// interpret these roles through their own shell-region adapter registry.
/// </remarks>
public static class NavigationShellComponentRoles
{
    /// <summary>
    /// Region that prompts the user to authenticate before protected resources
    /// are loaded.
    /// </summary>
    public const string AuthenticationPrompt = "cohesive.presentation.navigation-shell.authentication-prompt";

    /// <summary>
    /// Region that exposes active process tasks and links to process history.
    /// </summary>
    public const string ProcessTaskDrawer = "cohesive.presentation.navigation-shell.process-task-drawer";

    /// <summary>
    /// Region that displays transient process task notifications.
    /// </summary>
    public const string ProcessTaskToasts = "cohesive.presentation.navigation-shell.process-task-toasts";
}
