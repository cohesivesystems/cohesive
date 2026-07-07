namespace Cohesive.Presentation;

/// <summary>
/// Composes area or feature-owned presentation contributions into a runtime module.
/// </summary>
public static class PresentationModuleComposer
{
    /// <summary>
    /// Creates one <see cref="PresentationModuleDefinition"/> from independently
    /// authored module contributions.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A contributed semantic item has a duplicate identifier where duplicates
    /// are not composable.
    /// </exception>
    public static PresentationModuleDefinition Compose(
        string id,
        string name,
        string? version,
        params PresentationModuleContribution[] contributions
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(contributions);

        var navigation = Collect(contributions, static contribution => contribution.Navigation);
        var views = Collect(contributions, static contribution => contribution.Views);
        var workspaces = Collect(contributions, static contribution => contribution.Workspaces);
        var dataSources = Collect(contributions, static contribution => contribution.DataSources);
        var inputForms = Collect(contributions, static contribution => contribution.InputForms);
        var queryForms = Collect(contributions, static contribution => contribution.QueryForms);
        var fields = Collect(contributions, static contribution => contribution.Fields);
        var actions = Collect(contributions, static contribution => contribution.Actions);
        var flows = Collect(contributions, static contribution => contribution.Flows);
        var expressions = Collect(contributions, static contribution => contribution.Expressions);
        var annotations = Collect(contributions, static contribution => contribution.Annotations);
        var designSystems = MergeDesignSystems(Collect(contributions, static contribution => contribution.DesignSystems));
        var targets = MergeTargets(Collect(contributions, static contribution => contribution.Targets));

        EnsureUnique(navigation, static item => item.Id, "navigation graph");
        EnsureUnique(views, static item => item.Id, "view");
        EnsureUnique(workspaces, static item => item.Id, "workspace");
        EnsureUnique(dataSources, static item => item.Id, "data source");
        EnsureUnique(inputForms, static item => item.Id, "input form");
        EnsureUnique(queryForms, static item => item.Id, "query form");
        EnsureUnique(fields, static item => item.Id, "field");
        EnsureUnique(actions, static item => item.Id, "action");
        EnsureUnique(flows, static item => item.Id, "flow");
        EnsureUnique(expressions, static item => item.Id, "expression");
        EnsureUnique(designSystems, static item => item.Id, "design system");
        EnsureUnique(targets, static item => item.Id, "target binding");

        return new(
            Id: id,
            Name: name,
            Version: version,
            Navigation: navigation,
            Views: views,
            Workspaces: workspaces,
            DataSources: dataSources,
            InputForms: inputForms,
            QueryForms: queryForms,
            Fields: fields,
            Actions: actions,
            Flows: flows,
            Expressions: expressions,
            DesignSystems: designSystems,
            Targets: targets,
            Annotations: annotations
            );
    }

    static T[] Collect<T>(
        IEnumerable<PresentationModuleContribution> contributions,
        Func<PresentationModuleContribution, T[]> select
        ) =>
        contributions.SelectMany(select).ToArray();

    static DesignSystemBindingDefinition[] MergeDesignSystems(DesignSystemBindingDefinition[] designSystems) =>
        designSystems
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static group =>
            {
                var first = group.First();
                foreach (var item in group.Skip(1))
                {
                    if (item.Name != first.Name || item.Kind != first.Kind)
                        throw new InvalidOperationException($"Design system '{first.Id}' has incompatible contributed definitions.");
                }

                return first with
                {
                    ComponentBindings = group.SelectMany(static item => item.ComponentBindings).ToArray(),
                    Annotations = group.SelectMany(static item => item.Annotations).ToArray()
                };
            })
            .ToArray();

    static TargetBindingDefinition[] MergeTargets(TargetBindingDefinition[] targets) =>
        targets
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static group =>
            {
                var first = group.First();
                foreach (var item in group.Skip(1))
                {
                    if (item.Name != first.Name ||
                        item.Target != first.Target ||
                        item.ComponentSet != first.ComponentSet)
                    {
                        throw new InvalidOperationException($"Target binding '{first.Id}' has incompatible contributed definitions.");
                    }
                }

                return first with
                {
                    Bindings = group.SelectMany(static item => item.Bindings).ToArray(),
                    Annotations = group.SelectMany(static item => item.Annotations).ToArray()
                };
            })
            .ToArray();

    static void EnsureUnique<T>(
        IEnumerable<T> items,
        Func<T, string> readId,
        string kind
        )
    {
        var duplicateId = items
            .GroupBy(readId, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .FirstOrDefault();

        if (duplicateId is not null)
            throw new InvalidOperationException($"Duplicate presentation {kind} id '{duplicateId}'.");
    }
}
