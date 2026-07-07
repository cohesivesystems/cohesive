using Cohesive.Presentation;
using Cohesive.Prelude;

namespace Cohesive.Tests.Presentation;

public sealed class NavigationRouteDefinitionExtensionsTests
{
    [Fact]
    public void ToRouteTemplate_WithPathParameter_CreatesPathParameter()
    {
        var route = CreateRoute(
            pathTemplate: "/shape-graphs/{id}",
            parameters:
            [
                new("id", Type: "string", IsRequired: true)
            ]);

        var template = route.ToRouteTemplate();

        Assert.Equal("/shape-graphs/{id}", template.Template);
        var parameter = Assert.IsType<PathParameter>(Assert.Single(template.Parameters));
        Assert.Equal("id", parameter.Name);
    }

    [Fact]
    public void ToRouteTemplate_WithQueryPlaceholder_CreatesQueryParameterWithQueryName()
    {
        var route = CreateRoute(
            pathTemplate: "/shapes/{id}/search?q={query}",
            parameters:
            [
                new("id", Type: "string", IsRequired: true),
                new("query", Type: "string", IsRequired: true)
            ]);

        var template = route.ToRouteTemplate();

        Assert.Equal("/shapes/{id}/search", template.Template);
        var queryParameter = Assert.IsType<QueryParameter>(template.Parameters[1]);
        Assert.Equal("query", queryParameter.Name);
        Assert.Equal("q", queryParameter.QueryName);
        Assert.True(queryParameter.Required);
    }

    [Fact]
    public void ToRouteTemplate_WithRouteParameterNotInTemplate_TreatsParameterAsQueryParameter()
    {
        var route = CreateRoute(
            pathTemplate: "/tasks",
            parameters:
            [
                new("status", Type: "string", IsRequired: false)
            ]);

        var template = route.ToRouteTemplate();

        var parameter = Assert.IsType<QueryParameter>(Assert.Single(template.Parameters));
        Assert.Equal("status", parameter.Name);
        Assert.Equal("status", parameter.QueryName);
        Assert.False(parameter.Required);
    }

    [Fact]
    public void CreateHref_OnRoute_BindsPathAndQueryValues()
    {
        var route = CreateRoute(
            pathTemplate: "/shapes/{id}/search?q={query}",
            parameters:
            [
                new("id", Type: "string", IsRequired: true),
                new("query", Type: "string", IsRequired: true)
            ]);

        var href = route.CreateHref(new { id = "shape 1", query = "address.line1" });

        Assert.Equal("/shapes/shape%201/search?q=address.line1", href);
    }

    [Fact]
    public void CreateHref_OnRouteWithNullValuesAndNoParameters_ReturnsPath()
    {
        var route = CreateRoute(pathTemplate: "/tasks");

        var href = route.CreateHref((object?)null);

        Assert.Equal("/tasks", href);
    }

    [Fact]
    public void CreateHref_OnRouteWithNullDictionaryValuesAndNoParameters_ReturnsPath()
    {
        var route = CreateRoute(pathTemplate: "/tasks");

        var href = route.CreateHref<object?>(null);

        Assert.Equal("/tasks", href);
    }

    [Fact]
    public void CreateHref_OnNavigation_BindsRouteById()
    {
        var navigation = CreateNavigation(
            [
                CreateRoute(
                    id: "shape-search",
                    pathTemplate: "/shapes/{id}/search?q={query}",
                    parameters:
                    [
                        new("id", Type: "string", IsRequired: true),
                        new("query", Type: "string", IsRequired: true)
                    ])
            ]);

        var href = navigation.CreateHref("shape-search", new Dictionary<string, string>
        {
            ["id"] = "shape 1",
            ["query"] = "address.line1"
        });

        Assert.Equal("/shapes/shape%201/search?q=address.line1", href);
    }

    [Fact]
    public void CreateHref_OnNavigationWithNullValuesAndNoParameters_ReturnsPath()
    {
        var navigation = CreateNavigation([CreateRoute(id: "tasks", pathTemplate: "/tasks")]);

        var href = navigation.CreateHref("tasks", (object?)null);

        Assert.Equal("/tasks", href);
    }

    [Fact]
    public void GetRoute_WithMissingRoute_Throws()
    {
        var navigation = CreateNavigation();

        var exception = Assert.Throws<ArgumentException>(() => navigation.GetRoute("missing"));

        Assert.Equal("routeId", exception.ParamName);
        Assert.Contains("No navigation route named 'missing' is defined.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetPageHostForRoute_WithMatchingRoute_ReturnsPageHost()
    {
        var route = CreateRoute(pageHostId: "details-host");
        var pageHost = CreatePageHost(id: "details-host");
        var navigation = CreateNavigation([route], [pageHost]);

        var result = navigation.GetPageHostForRoute(route.Id);

        Assert.Same(pageHost, result);
    }

    [Fact]
    public void GetNavigationAction_WithMatchingAction_ReturnsAction()
    {
        var action = new NavigationActionDefinition(
            Id: "open-details",
            Name: "Open Details",
            Kind: NavigationActionKind.NavigateToRoute,
            RouteId: "details",
            PageHostId: "details-host",
            SourceNodeId: null,
            TargetNodeId: "details",
            Parameters: [],
            Context: new(
                Kind: NavigationContextEffectKind.Push,
                ContextId: "main",
                CapturesProvenance: true,
                WritesHistory: true),
            Annotations: []);
        var navigation = CreateNavigation(actions: [action]);

        var result = navigation.GetNavigationAction(action.Id);

        Assert.Same(action, result);
    }

    static NavigationRouteDefinition CreateRoute(
        string id = "route",
        string pathTemplate = "/route",
        string pageHostId = "page-host",
        NavigationRouteParameterDefinition[]? parameters = null) =>
        new(
            Id: id,
            Label: "Route",
            Kind: NavigationRouteKind.Page,
            PathTemplate: pathTemplate,
            PageHostId: pageHostId,
            Parameters: parameters ?? []);

    static PageHostDefinition CreatePageHost(string id = "page-host") =>
        new(
            Id: id,
            Kind: PageHostKind.SingleView,
            Workspace: null,
            View: new(ViewId: "view", Annotations: []),
            Regions:
            [
                new(
                    Id: "content",
                    Name: "Content",
                    Kind: PageRegionKind.Content,
                    ViewIds: ["view"],
                    PageHostIds: [],
                    ProjectionIds: [],
                    Placement: "main",
                    Annotations: [])
            ],
            Layout: new(
                DefaultRegionId: "content",
                Root: new(
                    Id: "content",
                    Kind: LayoutNodeKind.View,
                    Orientation: LayoutOrientation.None,
                    ProjectionIds: [],
                    ViewIds: ["view"],
                    Children: [],
                    Size: null,
                    Placement: "main")),
            State: null,
            Annotations: []);

    static NavigationDefinition CreateNavigation(
        NavigationRouteDefinition[]? routes = null,
        PageHostDefinition[]? pageHosts = null,
        NavigationActionDefinition[]? actions = null) =>
        new(
            Id: "test",
            Label: "Test",
            Nodes: [],
            Edges: [],
            Shell: new(
                Id: "shell",
                Kind: NavigationShellKind.TopNavigation,
                PrimaryNodeIds: [],
                Regions: []),
            Routes: routes ?? [],
            PageHosts: pageHosts ?? [],
            Actions: actions ?? [],
            Contexts: []);
}
