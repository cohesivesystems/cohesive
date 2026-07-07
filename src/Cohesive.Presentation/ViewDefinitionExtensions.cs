namespace Cohesive.Presentation;

/// <summary>
/// Extension methods for projecting view dependency metadata.
/// </summary>
public static class ViewDefinitionExtensions
{
    /// <summary>
    /// Gets the direct and transitive data source identifiers required to render a view.
    /// </summary>
    /// <param name="view">The root view.</param>
    /// <param name="views">All known views addressable by child view identifiers.</param>
    /// <returns>
    /// The ordered, de-duplicated data source identifiers consumed by the root view, its regions,
    /// and recursively hosted child views.
    /// </returns>
    /// <exception cref="ArgumentException">A hosted child view id is not present in <paramref name="views"/>.</exception>
    public static string[] GetEffectiveDataSourceIds(this ViewDefinition view, IEnumerable<ViewDefinition> views)
    {
        ArgumentNullException.ThrowIfNull(views);

        return view.GetEffectiveDataSourceIds(views.ToDictionary(static item => item.Id, StringComparer.Ordinal));
    }

    /// <summary>
    /// Gets the direct and transitive data source identifiers required to render a view.
    /// </summary>
    /// <param name="view">The root view.</param>
    /// <param name="viewsById">Known views keyed by view identifier.</param>
    /// <returns>
    /// The ordered, de-duplicated data source identifiers consumed by the root view, its regions,
    /// and recursively hosted child views.
    /// </returns>
    /// <exception cref="ArgumentException">A hosted child view id is not present in <paramref name="viewsById"/>.</exception>
    public static string[] GetEffectiveDataSourceIds(this ViewDefinition view, IReadOnlyDictionary<string, ViewDefinition> viewsById)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(viewsById);

        var orderedDataSourceIds = new List<string>();
        var addedDataSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var visitedViewIds = new HashSet<string>(StringComparer.Ordinal);

        AddView(view);

        return [.. orderedDataSourceIds];

        void AddView(ViewDefinition current)
        {
            if (!visitedViewIds.Add(current.Id))
                return;

            AddDataSourceIds(current.DataSourceIds);

            foreach (var region in current.Regions)
            {
                AddDataSourceIds(region.DataSourceIds);

                foreach (var childViewId in region.ViewIds)
                {
                    if (!viewsById.TryGetValue(childViewId, out var childView))
                        throw new ArgumentException($"No view named '{childViewId}' is defined.", nameof(viewsById));

                    AddView(childView);
                }
            }
        }

        void AddDataSourceIds(IEnumerable<string> dataSourceIds)
        {
            foreach (var dataSourceId in dataSourceIds)
            {
                if (addedDataSourceIds.Add(dataSourceId))
                    orderedDataSourceIds.Add(dataSourceId);
            }
        }
    }
}

/// <summary>
/// Extension methods for projecting presentation module dependency metadata.
/// </summary>
public static class PresentationModuleDefinitionExtensions
{
    /// <summary>
    /// Gets the direct and transitive data source identifiers required to render a view in the module.
    /// </summary>
    /// <param name="module">The presentation module.</param>
    /// <param name="viewId">The root view identifier.</param>
    /// <returns>
    /// The ordered, de-duplicated data source identifiers consumed by the root view, its regions,
    /// and recursively hosted child views.
    /// </returns>
    /// <exception cref="ArgumentException">No view with the given id is defined.</exception>
    public static string[] GetEffectiveViewDataSourceIds(this PresentationModuleDefinition module, string viewId)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);

        var viewsById = module.Views.ToDictionary(static view => view.Id, StringComparer.Ordinal);

        if (!viewsById.TryGetValue(viewId, out var view))
            throw new ArgumentException($"No view named '{viewId}' is defined.", nameof(viewId));

        return view.GetEffectiveDataSourceIds(viewsById);
    }
}
