using System.Collections.Immutable;

namespace Cohesive.Storage.Materialization;

/// <summary>One independently fenced materialization route selected for a changed semantic shape.</summary>
public sealed record MaterializationImpactPlanMatch
{
    internal MaterializationImpactPlanMatch(
        MaterializationImpactPlan plan,
        MaterializationImpactRoute route)
    {
        Plan = Guard.RequireNotNull(plan);
        Route = Guard.RequireNotNull(route);
    }

    /// <summary>Exact independently fenced materialization plan.</summary>
    public MaterializationImpactPlan Plan { get; }

    /// <summary>Canonical acquisition-role-specific route within the plan.</summary>
    public MaterializationImpactRoute Route { get; }
}

/// <summary>
/// Immutable outer routing index that preserves independently compiled impact semantics for every materialization.
/// </summary>
/// <remarks>
/// The catalog indexes existing plan routes by graph-qualified change shape. It does not merge routes or copy their
/// canonical Relations dependencies, so one entity change may fan out to several materializations and to several
/// semantic roles within one materialization without identity collapse.
/// </remarks>
public sealed class MaterializationImpactPlanCatalog
{
    readonly ImmutableDictionary<QualifiedShapeId, ImmutableArray<MaterializationImpactPlanMatch>> byShape;

    /// <summary>Creates an immutable catalog from independently compiled impact plans.</summary>
    /// <param name="plans">Complete impact plans to index.</param>
    /// <exception cref="ArgumentException">
    /// Plans contain null or repeat a materialization identity.
    /// </exception>
    public MaterializationImpactPlanCatalog(ImmutableArray<MaterializationImpactPlan> plans)
    {
        var normalized = plans.IsDefault ? [] : plans;
        if (normalized.Any(static plan => plan is null))
        {
            throw new ArgumentException("An impact-plan catalog cannot contain null plans.", nameof(plans));
        }

        if (normalized.GroupBy(static plan => plan.Materialization).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "An impact-plan catalog requires exactly one current plan per materialization.",
                nameof(plans));
        }

        Plans = [.. normalized.OrderBy(static plan => plan.Materialization.Value, StringComparer.Ordinal)];
        byShape = Plans
            .SelectMany(static plan => plan.Routes.Select(route => new MaterializationImpactPlanMatch(
                plan: plan,
                route: route)))
            .GroupBy(static match => match.Route.ChangeShape)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static match => match.Plan.Materialization.Value, StringComparer.Ordinal)
                    .ThenBy(static match => match.Route.ChangeInput.Value, StringComparer.Ordinal)
                    .ToImmutableArray());
    }

    /// <summary>Independently fenced plans in deterministic materialization-identity order.</summary>
    public ImmutableArray<MaterializationImpactPlan> Plans { get; }

    /// <summary>Gets every materialization and semantic role affected by one graph-qualified shape.</summary>
    /// <param name="shape">Changed graph-qualified semantic shape.</param>
    /// <returns>
    /// Independently fenced plan-route matches ordered by materialization identity and canonical change input.
    /// </returns>
    public ImmutableArray<MaterializationImpactPlanMatch> GetRoutes(QualifiedShapeId shape) =>
        byShape.TryGetValue(shape, out var routes) ? routes : [];
}
