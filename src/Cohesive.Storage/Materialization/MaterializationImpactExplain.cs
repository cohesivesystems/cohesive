using System.Collections.Immutable;
using Cohesive.Relations.Compilation;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// One deterministic explanation from a change route through canonical Relations dependencies to root work.
/// </summary>
public sealed record MaterializationImpactRouteExplain
{
    internal MaterializationImpactRouteExplain(
        MaterializationImpactRoute route,
        ImmutableArray<RelationQueryDependencyEntry> dependencies,
        ImmutableArray<RelationQueryRelationshipInput> relationships,
        ImmutableArray<MaterializationCapabilityRequirement> capabilities)
    {
        Route = Guard.RequireNotNull(route);
        Dependencies = dependencies;
        Relationships = relationships;
        Capabilities = capabilities;
    }

    /// <summary>Compiled route, including change input, strategy, precision, and hard bound.</summary>
    public MaterializationImpactRoute Route { get; }

    /// <summary>
    /// Canonical dependency-manifest entries referenced by the route, including their original effects and traces.
    /// </summary>
    public ImmutableArray<RelationQueryDependencyEntry> Dependencies { get; }

    /// <summary>Exact canonical relationship inputs dereferenced from inverse-traversal strategy steps.</summary>
    public ImmutableArray<RelationQueryRelationshipInput> Relationships { get; }

    /// <summary>Exact materialization capability requirements referenced by the route.</summary>
    public ImmutableArray<MaterializationCapabilityRequirement> Capabilities { get; }
}

/// <summary>Complete deterministic explain projection for one materialization impact plan.</summary>
public sealed record MaterializationImpactExplainArtifact
{
    internal MaterializationImpactExplainArtifact(
        MaterializationId materialization,
        MaterializationImpactPlanFingerprint planFingerprint,
        RelationQueryCompiledPlanReference relationPlan,
        RelationQueryOutputReference output,
        ImmutableArray<MaterializationImpactRouteExplain> routes)
    {
        Materialization = materialization;
        PlanFingerprint = Guard.RequireNotNull(planFingerprint);
        RelationPlan = Guard.RequireNotNull(relationPlan);
        Output = Guard.RequireNotNull(output);
        Routes = routes;
    }

    /// <summary>Stable logical materialization identity.</summary>
    public MaterializationId Materialization { get; }

    /// <summary>Exact impact-plan fingerprint being explained.</summary>
    public MaterializationImpactPlanFingerprint PlanFingerprint { get; }

    /// <summary>Fenced canonical Relations plan that owns dependency semantics.</summary>
    public RelationQueryCompiledPlanReference RelationPlan { get; }

    /// <summary>Selected materialized relation output.</summary>
    public RelationQueryOutputReference Output { get; }

    /// <summary>Route explanations in deterministic change-input order.</summary>
    public ImmutableArray<MaterializationImpactRouteExplain> Routes { get; }
}

/// <summary>Projects inspectable impact provenance without copying or translating Relations dependency semantics.</summary>
public static class MaterializationImpactExplainProjector
{
    /// <summary>Dereferences one impact plan against its exact materialization and canonical Relations manifest.</summary>
    /// <param name="impactPlan">Compiled impact plan to explain.</param>
    /// <param name="materialization">Exact materialization definition fenced by the plan.</param>
    /// <returns>
    /// A deterministic route projection retaining the original dependency entries, effects, traces, relationships,
    /// and materialization capability requirements.
    /// </returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan is stale, foreign, or references absent canonical content.</exception>
    /// <exception cref="InvalidOperationException">Canonical content cannot be fingerprinted.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime value.</exception>
    public static MaterializationImpactExplainArtifact Project(
        MaterializationImpactPlan impactPlan,
        MaterializationDefinition materialization)
    {
        ArgumentNullException.ThrowIfNull(impactPlan);
        ArgumentNullException.ThrowIfNull(materialization);
        var linkage = MaterializationImpactPlanLinker.Link(impactPlan, materialization);
        var relationPlan = linkage.RelationPlan;

        var dependencies = relationPlan.DependencyManifest.Entries.ToDictionary(static entry => entry.Input.Id);
        var relationshipInputs = relationPlan.RequirementGraph.Inputs
            .OfType<RelationQueryRelationshipInput>()
            .ToDictionary(static input => input.Id);
        var capabilityRequirements = materialization.Sources
            .SelectMany(static source => source.Capabilities)
            .Concat(materialization.TargetCapabilities)
            .ToDictionary(static requirement => requirement.Id);
        ImmutableArray<MaterializationImpactRouteExplain>.Builder routes =
            ImmutableArray.CreateBuilder<MaterializationImpactRouteExplain>(impactPlan.Routes.Length);
        foreach (var route in impactPlan.Routes)
        {
            var routeDependencies = route.DependencyInputs.Select(input =>
            {
                if (!dependencies.TryGetValue(input, out var dependency)
                    || !dependency.Impacts.Any(impact => impactPlan.Output.Covers(impact.Output)))
                {
                    throw new ArgumentException(
                        $"Impact route '{route.ChangeInput.Value}' references absent dependency '{input.Value}'.",
                        nameof(impactPlan));
                }

                return dependency;
            }).ToImmutableArray();
            ImmutableArray<MaterializationInverseImpactStep> relationshipSteps = route.Strategy switch
            {
                MaterializationInverseTraversalImpactStrategy inverse => inverse.Steps,
                MaterializationContributorLedgerImpactStrategy ledger => ledger.CurrentRootSteps,
                _ => []
            };
            ImmutableArray<RelationQueryRelationshipInput> relationships =
            [
                .. relationshipSteps.Select(step =>
                {
                    if (!relationshipInputs.TryGetValue(step.RelationshipInput, out var relationship))
                    {
                        throw new ArgumentException(
                            $"Impact route '{route.ChangeInput.Value}' references absent relationship input "
                            + $"'{step.RelationshipInput.Value}'.",
                            nameof(impactPlan));
                    }

                    return relationship;
                })
            ];
            var capabilities = route.Capabilities.Select(reference =>
            {
                if (!capabilityRequirements.TryGetValue(reference.Requirement, out var requirement))
                {
                    throw new ArgumentException(
                        $"Impact route '{route.ChangeInput.Value}' references absent capability requirement "
                        + $"'{reference.Requirement.Value}'.",
                        nameof(impactPlan));
                }

                return requirement;
            }).ToImmutableArray();
            routes.Add(new(
                route: route,
                dependencies: routeDependencies,
                relationships: relationships,
                capabilities: capabilities));
        }

        return new(
            materialization: impactPlan.Materialization,
            planFingerprint: impactPlan.Fingerprint,
            relationPlan: impactPlan.RelationPlan,
            output: impactPlan.Output,
            routes: routes.MoveToImmutable());
    }
}
