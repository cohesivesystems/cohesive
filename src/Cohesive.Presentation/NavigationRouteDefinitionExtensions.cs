using Cohesive.Prelude;

namespace Cohesive.Presentation;

/// <summary>
/// Extension methods for binding navigation route definitions to concrete href values.
/// </summary>
public static class NavigationRouteDefinitionExtensions
{
    /// <param name="route">The navigation route definition.</param>
    extension(NavigationRouteDefinition route)
    {
        /// <summary>
        /// Converts the navigation route definition to a bindable route template.
        /// </summary>
        /// <exception cref="FormatException">Route template format is invalid.</exception>
        /// <exception cref="InvalidOperationException">Route template contains invalid parameter names.</exception>
        public RouteTemplate ToRouteTemplate()
        {
            ArgumentNullException.ThrowIfNull(route);

            var parsedTemplate = RouteTemplate.Parse(route.PathTemplate);
            var explicitParameters = route.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
            var parameters = new List<Parameter>(parsedTemplate.Parameters.Count + route.Parameters.Length);
            var knownParameterNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var parameter in parsedTemplate.Parameters)
            {
                knownParameterNames.Add(parameter.Name);
                parameters.Add(parameter is QueryParameter queryParameter && explicitParameters.TryGetValue(queryParameter.Name, out var explicitParameter)
                    ? queryParameter with { Required = explicitParameter.IsRequired }
                    : parameter
                    );
            }

            foreach (var parameter in route.Parameters)
            {
                if (!knownParameterNames.Add(parameter.Name))
                    continue;

                parameters.Add(route.PathTemplate.Contains("{" + parameter.Name + "}", StringComparison.Ordinal)
                    ? new PathParameter(parameter.Name)
                    : new QueryParameter(parameter.Name, Required: parameter.IsRequired)
                );
            }

            return parsedTemplate with { Parameters = parameters };
        }

        /// <summary>
        /// Creates an href by binding public CLR object properties as route values.
        /// </summary>
        /// <param name="values">The object whose public properties provide route values. Null is treated as an empty value set.</param>
        /// <exception cref="FormatException">Route template format is invalid.</exception>
        /// <exception cref="InvalidOperationException">Route template contains invalid parameter names.</exception>
        public string CreateHref(object? values = null) =>
            route.ToRouteTemplate().Bind(values);

        /// <summary>
        /// Creates an href by binding dictionary values.
        /// </summary>
        /// <param name="values">The route values keyed by parameter name. Null is treated as an empty value set.</param>
        /// <exception cref="FormatException">Route template format is invalid.</exception>
        /// <exception cref="InvalidOperationException">Route template contains invalid parameter names.</exception>
        public string CreateHref<TValue>(IReadOnlyDictionary<string, TValue>? values) =>
            route.ToRouteTemplate().Bind(values);
    }
}

/// <summary>
/// Extension methods for resolving and binding navigation routes from a navigation definition.
/// </summary>
public static class NavigationDefinitionExtensions
{
    /// <param name="navigation">The navigation definition.</param>
    extension(NavigationDefinition navigation)
    {
        /// <summary>
        /// Gets a route by id.
        /// </summary>
        /// <param name="routeId">The route id.</param>
        /// <exception cref="ArgumentException">No navigation route with the given id is defined.</exception>
        public NavigationRouteDefinition GetRoute(string routeId)
        {
            ArgumentNullException.ThrowIfNull(navigation);
            ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
            return navigation.Routes.FirstOrDefault(route => string.Equals(route.Id, routeId, StringComparison.Ordinal))
                   ?? throw new ArgumentException($"No navigation route named '{routeId}' is defined.", nameof(routeId));
        }

        /// <summary>
        /// Creates an href for the route by binding public CLR object properties as route values.
        /// </summary>
        /// <param name="routeId">The route id.</param>
        /// <param name="values">The object whose public properties provide route values. Null is treated as an empty value set.</param>
        /// <exception cref="ArgumentException">No navigation route with the given id is defined.</exception>
        public string CreateHref(string routeId, object? values = null) =>
            navigation.GetRoute(routeId).CreateHref(values);

        /// <summary>
        /// Creates an href for the route by binding dictionary values.
        /// </summary>
        /// <param name="routeId">The route id.</param>
        /// <param name="values">The route values keyed by parameter name. Null is treated as an empty value set.</param>
        /// <exception cref="ArgumentException">No navigation route with the given id is defined.</exception>
        public string CreateHref<TValue>(string routeId, IReadOnlyDictionary<string, TValue>? values) =>
            navigation.GetRoute(routeId).CreateHref(values);

        /// <summary>
        /// Gets a page host by id.
        /// </summary>
        /// <param name="pageHostId">The page host id.</param>
        /// <exception cref="ArgumentException">No page host with the given id is defined.</exception>
        public PageHostDefinition GetPageHost(string pageHostId)
        {
            ArgumentNullException.ThrowIfNull(navigation);
            ArgumentException.ThrowIfNullOrWhiteSpace(pageHostId);

            return navigation.PageHosts.FirstOrDefault(pageHost => string.Equals(pageHost.Id, pageHostId, StringComparison.Ordinal))
                   ?? throw new ArgumentException($"No page host named '{pageHostId}' is defined.", nameof(pageHostId));
        }

        /// <summary>
        /// Gets the page host targeted by a route.
        /// </summary>
        /// <param name="routeId">The route id.</param>
        /// <exception cref="ArgumentException">No route or page host with the given id is defined.</exception>
        public PageHostDefinition GetPageHostForRoute(string routeId)
        {
            var route = navigation.GetRoute(routeId);
            return navigation.GetPageHost(route.PageHostId);
        }

        /// <summary>
        /// Gets an intentful navigation action by id.
        /// </summary>
        /// <param name="actionId">The navigation action id.</param>
        /// <exception cref="ArgumentException">No navigation action with the given id is defined.</exception>
        public NavigationActionDefinition GetNavigationAction(string actionId)
        {
            ArgumentNullException.ThrowIfNull(navigation);
            ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

            return navigation.Actions.FirstOrDefault(action => string.Equals(action.Id, actionId, StringComparison.Ordinal))
                   ?? throw new ArgumentException($"No navigation action named '{actionId}' is defined.", nameof(actionId));
        }
    }
}
