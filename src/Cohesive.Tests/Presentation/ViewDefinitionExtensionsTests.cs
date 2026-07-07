using Cohesive.Presentation;

namespace Cohesive.Tests.Presentation;

public sealed class ViewDefinitionExtensionsTests
{
    [Fact]
    public void GetEffectiveDataSourceIds_TraversesRegionAndChildViewDependencies()
    {
        var detail = CreateView("detail", ["detail-source"]);
        var table = CreateView(
            "table",
            ["table-source", "shared-source"],
            [
                CreateRegion("detail", viewIds: ["detail"], dataSourceIds: ["region-source"])
            ]);
        var page = CreateView(
            "page",
            ["page-source", "shared-source"],
            [
                CreateRegion("content", viewIds: ["table"], dataSourceIds: ["content-source"])
            ]);
        var views = new[] { page, table, detail };

        var dataSourceIds = page.GetEffectiveDataSourceIds(views);

        Assert.Equal(
            [
                "page-source",
                "shared-source",
                "content-source",
                "table-source",
                "region-source",
                "detail-source"
            ],
            dataSourceIds);
    }

    [Fact]
    public void GetEffectiveDataSourceIds_WithContainerView_DerivesChildDependencies()
    {
        var header = CreateView("header", ["summary-source", "auth-source"]);
        var page = CreateView(
            "page",
            regions:
            [
                CreateRegion("header", viewIds: ["header"])
            ]);

        var dataSourceIds = page.GetEffectiveDataSourceIds([page, header]);

        Assert.Equal(["summary-source", "auth-source"], dataSourceIds);
    }

    [Fact]
    public void GetEffectiveDataSourceIds_WithMissingChildView_Throws()
    {
        var page = CreateView(
            "page",
            regions:
            [
                CreateRegion("content", viewIds: ["missing"])
            ]);

        var exception = Assert.Throws<ArgumentException>(() => page.GetEffectiveDataSourceIds([page]));

        Assert.Equal("viewsById", exception.ParamName);
        Assert.Contains("No view named 'missing' is defined.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetEffectiveViewDataSourceIds_ResolvesRootViewFromModule()
    {
        var child = CreateView("child", ["child-source"]);
        var page = CreateView(
            "page",
            regions:
            [
                CreateRegion("content", viewIds: ["child"])
            ]);
        var module = CreateModule([page, child]);

        var dataSourceIds = module.GetEffectiveViewDataSourceIds("page");

        Assert.Equal(["child-source"], dataSourceIds);
    }

    static PresentationModuleDefinition CreateModule(ViewDefinition[] views) =>
        new(
            Id: "module",
            Name: "Module",
            Version: null,
            Navigation: [],
            Views: views,
            Workspaces: [],
            DataSources: [],
            InputForms: [],
            QueryForms: [],
            Fields: [],
            Actions: [],
            Flows: [],
            Expressions: [],
            DesignSystems: [],
            Targets: [],
            Annotations: []);

    static ViewDefinition CreateView(
        string id,
        string[]? dataSourceIds = null,
        ViewRegionDefinition[]? regions = null) =>
        new(
            Id: id,
            Name: id,
            Kind: ViewKind.Panel,
            Subject: new(Kind: ViewSubjectKind.DataSource),
            DataSourceIds: dataSourceIds ?? [],
            Regions: regions ?? [],
            FieldIds: [],
            Actions: [],
            Chrome: null,
            State: [],
            Synchronization: [],
            InteractionStateId: null,
            Accessibility: null,
            Design: null,
            Annotations: []);

    static ViewRegionDefinition CreateRegion(
        string id,
        string[] viewIds,
        string[]? dataSourceIds = null) =>
        new(
            Id: id,
            Name: id,
            Kind: ViewRegionKind.Content,
            ViewIds: viewIds,
            DataSourceIds: dataSourceIds ?? [],
            Actions: [],
            Annotations: []);
}
