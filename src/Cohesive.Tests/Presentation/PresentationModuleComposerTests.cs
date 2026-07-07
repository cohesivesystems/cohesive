using Cohesive.Presentation;

namespace Cohesive.Tests.Presentation;

public sealed class PresentationModuleComposerTests
{
    [Fact]
    public void Compose_MergesContributionsAndTargetBindingFragments()
    {
        var module = PresentationModuleComposer.Compose(
            id: "module",
            name: "Module",
            version: "1",
            new PresentationModuleContribution
            {
                Views = [CreateView("home")],
                Targets = [CreateTarget("react", "home-binding")]
            },
            new PresentationModuleContribution
            {
                Views = [CreateView("details")],
                Targets = [CreateTarget("react", "details-binding")]
            });

        Assert.Equal(["home", "details"], module.Views.Select(static view => view.Id));

        var target = Assert.Single(module.Targets);
        Assert.Equal(
            ["home-binding", "details-binding"],
            target.Bindings.Select(static binding => binding.Id));
    }

    [Fact]
    public void Compose_WithDuplicateSemanticIds_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PresentationModuleComposer.Compose(
                id: "module",
                name: "Module",
                version: null,
                new PresentationModuleContribution
                {
                    Views = [CreateView("duplicate")]
                },
                new PresentationModuleContribution
                {
                    Views = [CreateView("duplicate")]
                }));

        Assert.Equal("Duplicate presentation view id 'duplicate'.", exception.Message);
    }

    static ViewDefinition CreateView(string id) =>
        new(
            Id: id,
            Name: id,
            Kind: ViewKind.Page,
            Subject: new(Kind: ViewSubjectKind.DataSource),
            DataSourceIds: [],
            Regions: [],
            FieldIds: [],
            Actions: [],
            Chrome: null,
            State: [],
            Synchronization: [],
            InteractionStateId: null,
            Accessibility: null,
            Design: null,
            Annotations: []);

    static TargetBindingDefinition CreateTarget(string id, params string[] bindingIds) =>
        new(
            Id: id,
            Name: id,
            Target: PresentationTargetKind.React,
            ComponentSet: "test",
            Bindings: bindingIds
                .Select(static bindingId => new PresentationBindingDefinition(
                    Kind: PresentationBindingKind.Component,
                    Id: bindingId,
                    ComponentKey: bindingId))
                .ToArray(),
            Annotations: []);
}
