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
/// One non-value influence through which a semantic input can change a demanded output.
/// </summary>
/// <remarks>
/// Influences preserve membership, correlation, acquisition, cardinality, ordering, grouping,
/// pagination, validation, and evaluation dependencies without misclassifying them as value
/// contributions.
/// </remarks>
public sealed record RelationQueryLineageInfluence
{
    internal RelationQueryLineageInfluence(
        RelationQueryRequirementInput input,
        RelationQueryRequirementEffect effect,
        ImmutableArray<RelationQueryRequirementTrace> traces)
    {
        Input = Guard.RequireNotNull(input);
        if (effect is RelationQueryRequirementEffect.Value
            or RelationQueryRequirementEffect.Identity
            or RelationQueryRequirementEffect.Aggregation
            || !Enum.IsDefined(effect))
        {
            throw new ArgumentOutOfRangeException(
                nameof(effect),
                effect,
                "Static lineage influences exclude value, identity, and aggregation contributions.");
        }

        Effect = effect;
        Traces = RelationQueryRequirementOrdering.NormalizeTraces(traces);
        if (Traces.IsDefaultOrEmpty)
            throw new ArgumentException("A lineage influence requires at least one provenance trace.", nameof(traces));
    }

    /// <summary>Semantic input that can influence the output.</summary>
    public RelationQueryRequirementInput Input { get; }

    /// <summary>Non-value semantic effect represented by the influence.</summary>
    public RelationQueryRequirementEffect Effect { get; }

    /// <summary>Propagation traces from the output to the influencing input.</summary>
    public ImmutableArray<RelationQueryRequirementTrace> Traces { get; }
}

/// <summary>
/// Static lineage contributions for one demanded output or output field.
/// </summary>
public sealed record RelationQueryLineageEntry
{
    internal RelationQueryLineageEntry(
        RelationQueryOutputReference output,
        ImmutableArray<RelationQueryLineageContribution> contributions,
        ImmutableArray<RelationQueryLineageInfluence> influences)
    {
        Output = Guard.RequireNotNull(output);
        Contributions = NormalizeContributions(contributions);
        Influences = NormalizeInfluences(influences);
    }

    /// <summary>Demanded output described by this entry.</summary>
    public RelationQueryOutputReference Output { get; }

    /// <summary>
    /// Value, identity, and aggregate contributions sorted by input identity and effect; empty when
    /// the demanded output is constant-derived or has no data-lineage input.
    /// </summary>
    public ImmutableArray<RelationQueryLineageContribution> Contributions { get; }

    /// <summary>
    /// Non-value influences sorted by input identity and effect; these capture how inputs can alter
    /// membership, multiplicity, ordering, validation, or other output semantics.
    /// </summary>
    public ImmutableArray<RelationQueryLineageInfluence> Influences { get; }

    static ImmutableArray<RelationQueryLineageContribution> NormalizeContributions(ImmutableArray<RelationQueryLineageContribution> contributions)
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

    static ImmutableArray<RelationQueryLineageInfluence> NormalizeInfluences(ImmutableArray<RelationQueryLineageInfluence> influences)
    {
        var normalized = influences.IsDefault ? [] : influences;
        if (normalized.Any(static influence => influence is null))
            throw new ArgumentException("Lineage influences cannot contain null entries.", nameof(influences));

        foreach (var group in normalized.GroupBy(static influence => influence.Input.Id))
        {
            var input = group.First().Input;
            if (group.Skip(1).Any(influence => !Equals(influence.Input, input)))
                throw new ArgumentException($"Input id '{group.Key.Value}' has conflicting lineage definitions.", nameof(influences));
        }

        return
        [
            .. normalized
                .GroupBy(static influence => (influence.Input.Id, influence.Effect))
                .Select(group => new RelationQueryLineageInfluence(
                    group.First().Input,
                    group.Key.Effect,
                    [.. group.SelectMany(static influence => influence.Traces)]))
                .OrderBy(static influence => influence.Input.Id.Value, StringComparer.Ordinal)
                .ThenBy(static influence => (int)influence.Effect)
        ];
    }
}

/// <summary>
/// Immutable output-oriented contribution and influence lineage derived from a requirement graph.
/// </summary>
public sealed class RelationQueryLineage
{
    internal RelationQueryLineage(RelationQueryRequirementGraph requirements)
    {
        Requirements = Guard.RequireNotNull(requirements);
        var lineageEdges = requirements.Edges
            .GroupBy(static edge => edge.Output.Id)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());

        Entries =
        [
            .. requirements.Outputs
                .Select(output => new RelationQueryLineageEntry(
                    output,
                    lineageEdges.TryGetValue(output.Id, out var edges) ?
                    [
                        .. edges
                            .Where(static edge => edge.Effect is RelationQueryRequirementEffect.Value
                                or RelationQueryRequirementEffect.Identity
                                or RelationQueryRequirementEffect.Aggregation)
                            .Select(static edge => new RelationQueryLineageContribution(
                                edge.Input,
                                edge.Effect,
                                edge.Traces))
                    ] : [],
                    lineageEdges.TryGetValue(output.Id, out edges) ?
                    [
                        .. edges
                            .Where(static edge => edge.Effect is not (
                                RelationQueryRequirementEffect.Value
                                or RelationQueryRequirementEffect.Identity
                                or RelationQueryRequirementEffect.Aggregation))
                            .Select(static edge => new RelationQueryLineageInfluence(
                                edge.Input,
                                edge.Effect,
                                edge.Traces))
                    ] : []
                ))
                .OrderBy(static entry => entry.Output.Id.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Canonical requirement graph from which this lineage was projected.</summary>
    public RelationQueryRequirementGraph Requirements { get; }

    /// <summary>
    /// One lineage entry per demanded output, sorted by stable output identity; an entry may have no
    /// contributions or influences when its output is constant-derived and operationally independent.
    /// </summary>
    public ImmutableArray<RelationQueryLineageEntry> Entries { get; }
}
