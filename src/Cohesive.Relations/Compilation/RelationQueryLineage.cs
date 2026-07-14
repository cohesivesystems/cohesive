using System.Collections.Immutable;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// One value, identity, or aggregate contribution to a demanded output.
/// </summary>
public sealed record RelationQueryLineageContribution
{
    internal RelationQueryLineageContribution(
        RelationQueryRequirementInput input,
        RelationQueryRequirementEffect effect,
        ImmutableArray<RelationQueryRequirementTrace> traces)
    {
        Input = Guard.RequireNotNull(input);
        if (effect is not RelationQueryRequirementEffect.Value
            and not RelationQueryRequirementEffect.Identity
            and not RelationQueryRequirementEffect.Aggregation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effect),
                effect,
                "Static lineage contains value, identity, and aggregation effects only.");
        }

        Effect = effect;
        Traces = RelationQueryRequirementOrdering.NormalizeTraces(traces);
        if (Traces.IsDefaultOrEmpty)
            throw new ArgumentException("A lineage contribution requires at least one provenance trace.", nameof(traces));
    }

    /// <summary>Semantic input contributing to the output.</summary>
    public RelationQueryRequirementInput Input { get; }

    /// <summary>Value, identity, or aggregation effect represented by the contribution.</summary>
    public RelationQueryRequirementEffect Effect { get; }

    /// <summary>Propagation traces from the output to the contributing input.</summary>
    public ImmutableArray<RelationQueryRequirementTrace> Traces { get; }
}

/// <summary>
/// Static lineage contributions for one demanded output or output field.
/// </summary>
public sealed record RelationQueryLineageEntry
{
    internal RelationQueryLineageEntry(
        RelationQueryOutputReference output,
        ImmutableArray<RelationQueryLineageContribution> contributions)
    {
        Output = Guard.RequireNotNull(output);
        Contributions = NormalizeContributions(contributions);
    }

    /// <summary>Demanded output described by this entry.</summary>
    public RelationQueryOutputReference Output { get; }

    /// <summary>
    /// Value, identity, and aggregate contributions sorted by input identity and effect; empty when
    /// the demanded output is constant-derived or has no data-lineage input.
    /// </summary>
    public ImmutableArray<RelationQueryLineageContribution> Contributions { get; }

    static ImmutableArray<RelationQueryLineageContribution> NormalizeContributions(
        ImmutableArray<RelationQueryLineageContribution> contributions)
    {
        var normalized = contributions.IsDefault ? [] : contributions;
        if (normalized.Any(static contribution => contribution is null))
            throw new ArgumentException("Lineage contributions cannot contain null entries.", nameof(contributions));

        foreach (var group in normalized.GroupBy(static contribution => contribution.Input.Id))
        {
            var input = group.First().Input;
            if (group.Skip(1).Any(contribution => !Equals(contribution.Input, input)))
                throw new ArgumentException($"Input id '{group.Key.Value}' has conflicting lineage definitions.", nameof(contributions));
        }

        return
        [
            .. normalized
                .GroupBy(static contribution => (contribution.Input.Id, contribution.Effect))
                .Select(group => new RelationQueryLineageContribution(
                    group.First().Input,
                    group.Key.Effect,
                    [.. group.SelectMany(static contribution => contribution.Traces)]))
                .OrderBy(static contribution => contribution.Input.Id.Value, StringComparer.Ordinal)
                .ThenBy(static contribution => (int)contribution.Effect)
        ];
    }
}

/// <summary>
/// Immutable output-oriented static lineage derived from a requirement graph.
/// </summary>
public sealed class RelationQueryLineage
{
    internal RelationQueryLineage(RelationQueryRequirementGraph requirements)
    {
        Requirements = Guard.RequireNotNull(requirements);
        var lineageEdges = requirements.Edges.Where(static edge =>
            edge.Effect is RelationQueryRequirementEffect.Value
                or RelationQueryRequirementEffect.Identity
                or RelationQueryRequirementEffect.Aggregation)
            .GroupBy(static edge => edge.Output.Id)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());

        Entries =
        [
            .. requirements.Outputs
                .Select(output => new RelationQueryLineageEntry(
                    output,
                    lineageEdges.TryGetValue(output.Id, out var edges)
                        ?
                    [
                        .. edges.Select(static edge => new RelationQueryLineageContribution(
                            edge.Input,
                            edge.Effect,
                            edge.Traces))
                    ]
                        : []))
                .OrderBy(static entry => entry.Output.Id.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Canonical requirement graph from which this lineage was projected.</summary>
    public RelationQueryRequirementGraph Requirements { get; }

    /// <summary>
    /// One lineage entry per demanded output, sorted by stable output identity; an entry may have no
    /// contributions when its output is constant-derived or has no data-lineage input.
    /// </summary>
    public ImmutableArray<RelationQueryLineageEntry> Entries { get; }
}
