using System.Collections.Immutable;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// One demanded-output impact caused by a semantic input dependency.
/// </summary>
public sealed record RelationQueryDependencyImpact
{
    internal RelationQueryDependencyImpact(
        RelationQueryOutputReference output,
        RelationQueryRequirementEffect effect,
        QueryInputRequirement requirement,
        ImmutableArray<RelationQueryRequirementTrace> traces)
    {
        Output = Guard.RequireNotNull(output);
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unsupported dependency effect.");
        if (!Enum.IsDefined(requirement))
            throw new ArgumentOutOfRangeException(nameof(requirement), requirement, "Unsupported input requirement.");
        Effect = effect;
        Requirement = requirement;
        Traces = RelationQueryRequirementOrdering.NormalizeTraces(traces);
        if (Traces.IsDefaultOrEmpty)
            throw new ArgumentException("A dependency impact requires at least one provenance trace.", nameof(traces));
    }

    /// <summary>Demanded output affected when the input changes.</summary>
    public RelationQueryOutputReference Output { get; }

    /// <summary>Semantic effect through which the input affects the output.</summary>
    public RelationQueryRequirementEffect Effect { get; }

    /// <summary>Whether acquisition of the dependency is required or optional.</summary>
    public QueryInputRequirement Requirement { get; }

    /// <summary>Propagation traces explaining the dependency impact.</summary>
    public ImmutableArray<RelationQueryRequirementTrace> Traces { get; }
}

/// <summary>
/// Inverse dependency entry from one semantic input to all outputs it may affect.
/// </summary>
public sealed record RelationQueryDependencyEntry
{
    internal RelationQueryDependencyEntry(
        RelationQueryRequirementInput input,
        ImmutableArray<RelationQueryDependencyImpact> impacts)
    {
        Input = Guard.RequireNotNull(input);
        Impacts = NormalizeImpacts(impacts);
        if (Impacts.IsDefaultOrEmpty)
            throw new ArgumentException("A dependency entry requires at least one impact.", nameof(impacts));
    }

    /// <summary>Semantic input whose change may affect compiled outputs.</summary>
    public RelationQueryRequirementInput Input { get; }

    /// <summary>Affected outputs sorted by output identity and effect.</summary>
    public ImmutableArray<RelationQueryDependencyImpact> Impacts { get; }

    static ImmutableArray<RelationQueryDependencyImpact> NormalizeImpacts(
        ImmutableArray<RelationQueryDependencyImpact> impacts)
    {
        var normalized = impacts.IsDefault ? [] : impacts;
        if (normalized.Any(static impact => impact is null))
            throw new ArgumentException("Dependency impacts cannot contain null entries.", nameof(impacts));

        foreach (var group in normalized.GroupBy(static impact => impact.Output.Id))
        {
            var output = group.First().Output;
            if (group.Skip(1).Any(impact => !Equals(impact.Output, output)))
                throw new ArgumentException($"Output id '{group.Key.Value}' has conflicting dependency definitions.", nameof(impacts));
        }

        return
        [
            .. normalized
                .GroupBy(static impact => (impact.Output.Id, impact.Effect))
                .Select(group => new RelationQueryDependencyImpact(
                    group.First().Output,
                    group.Key.Effect,
                    group.Any(static impact => impact.Requirement == QueryInputRequirement.Required)
                        ? QueryInputRequirement.Required
                        : QueryInputRequirement.Optional,
                    [.. group.SelectMany(static impact => impact.Traces)]))
                .OrderBy(static impact => impact.Output.Id.Value, StringComparer.Ordinal)
                .ThenBy(static impact => (int)impact.Effect)
                .ThenBy(static impact => (int)impact.Requirement)
        ];
    }
}

/// <summary>
/// Immutable input-oriented dependency manifest derived from a requirement graph.
/// </summary>
public sealed class RelationQueryDependencyManifest
{
    internal RelationQueryDependencyManifest(RelationQueryRequirementGraph requirements)
    {
        Requirements = Guard.RequireNotNull(requirements);

        Entries =
        [
            .. requirements.Edges
                .GroupBy(static edge => edge.Input.Id)
                .Select(group => new RelationQueryDependencyEntry(
                    group.First().Input,
                    [
                        .. group.Select(static edge => new RelationQueryDependencyImpact(
                            edge.Output,
                            edge.Effect,
                            edge.Requirement,
                            edge.Traces))
                    ]))
                .OrderBy(static entry => entry.Input.Id.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Canonical requirement graph from which this inverse manifest was projected.</summary>
    public RelationQueryRequirementGraph Requirements { get; }

    /// <summary>Dependency entries sorted by stable semantic-input identity.</summary>
    public ImmutableArray<RelationQueryDependencyEntry> Entries { get; }
}
