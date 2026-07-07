namespace Cohesive.Presentation;

/// <summary>
/// Standard component roles for semantic presentation views.
/// </summary>
/// <remarks>
/// These values describe presentation roles that target adapters can
/// interpret. They are not concrete React, Blazor, or native component names.
/// </remarks>
public static class PresentationViewComponentRoles
{
    /// <summary>
    /// Generic view surface with optional title, body, and chrome.
    /// </summary>
    public const string ViewSurface = "cohesive.presentation.view-surface";

    /// <summary>
    /// Dashboard-like metric summary view.
    /// </summary>
    public const string MetricDashboard = "cohesive.presentation.metric-dashboard";

    /// <summary>
    /// Tabbed container view.
    /// </summary>
    public const string TabsView = "cohesive.presentation.tabs-view";

    /// <summary>
    /// Collection/grid view with optional query, summary, pagination, and detail chrome.
    /// </summary>
    public const string CollectionView = "cohesive.presentation.collection-view";

    /// <summary>
    /// Query form view backed by presentation query-form and input-form semantics.
    /// </summary>
    public const string QueryForm = "cohesive.presentation.query-form";

    /// <summary>
    /// Input form view backed by presentation input-form semantics.
    /// </summary>
    public const string InputForm = "cohesive.presentation.input-form";
}
