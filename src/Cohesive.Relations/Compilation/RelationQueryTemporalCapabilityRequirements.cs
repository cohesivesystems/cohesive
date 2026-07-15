using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// One valid-time join semantic that an execution target can preserve exactly.
/// </summary>
/// <remarks>
/// These capabilities describe temporal-join execution rather than expression evaluation. A target
/// must support every demand-scoped capability in the compiled input contract or report an
/// attributable capability mismatch.
/// </remarks>
public enum RelationQueryTemporalExecutionCapability
{
    /// <summary>Evaluates a temporal point against one interval.</summary>
    PointInInterval = 0,

    /// <summary>Evaluates whether two temporal intervals overlap.</summary>
    IntervalOverlap = 1,

    /// <summary>Preserves inclusive finite interval endpoints.</summary>
    InclusiveBoundary = 2,

    /// <summary>Preserves exclusive finite interval endpoints.</summary>
    ExclusiveBoundary = 3,

    /// <summary>Preserves structurally unbounded interval endpoints.</summary>
    UnboundedBoundary = 4,

    /// <summary>Interprets a null expression-backed endpoint as unbounded only when explicitly declared.</summary>
    NullAsUnbounded = 5,

    /// <summary>Compares <see cref="ScalarTypeKind.Date"/> values without temporal-domain coercion.</summary>
    DateDomain = 6,

    /// <summary>Compares <see cref="ScalarTypeKind.DateTime"/> values without temporal-domain coercion.</summary>
    DateTimeDomain = 7,

    /// <summary>Compares <see cref="ScalarTypeKind.Instant"/> values without temporal-domain coercion.</summary>
    InstantDomain = 8,

    /// <summary>Emits every correlated temporal match instead of selecting one representative match.</summary>
    PreserveAllMatches = 9,

    /// <summary>Preserves inner temporal-join absence semantics.</summary>
    InnerJoin = 10,

    /// <summary>Preserves left-outer temporal-join absence semantics.</summary>
    LeftOuterJoin = 11,

    /// <summary>Preserves right-outer temporal-join absence semantics.</summary>
    RightOuterJoin = 12,

    /// <summary>Preserves full-outer temporal-join absence semantics.</summary>
    FullOuterJoin = 13,

    /// <summary>Detects malformed runtime intervals instead of interpreting them as non-matches.</summary>
    ValidateIntervals = 14,

    /// <summary>Preserves inconclusive temporal evidence instead of interpreting it as a conclusive non-match.</summary>
    InconclusiveEvidence = 15
}

/// <summary>
/// One demand-scoped temporal target capability required by a compiled input contract.
/// </summary>
public sealed record RelationQueryTemporalCapabilityInputContract
{
    internal RelationQueryTemporalCapabilityInputContract(
        RelationQueryInputId id,
        RelationQueryTemporalExecutionCapability capability,
        QueryNodeId node,
        string semanticSite)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A temporal capability contract requires a stable identity.", nameof(id));
        if (!Enum.IsDefined(capability))
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported temporal execution capability.");
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A temporal capability contract requires a logical node.", nameof(node));

        Id = id;
        Capability = capability;
        Node = node;
        SemanticSite = Guard.RequireNotNullOrWhiteSpace(semanticSite);
    }

    /// <summary>Stable demand-scoped capability requirement identity.</summary>
    public RelationQueryInputId Id { get; }

    /// <summary>Exact temporal semantic the target must preserve.</summary>
    public RelationQueryTemporalExecutionCapability Capability { get; }

    /// <summary>Retained temporal join node requiring the capability.</summary>
    public QueryNodeId Node { get; }

    /// <summary>Canonical semantic site that justifies the capability requirement.</summary>
    public string SemanticSite { get; }
}

static class RelationQueryTemporalCapabilityProjector
{
    public static ImmutableArray<RelationQueryTemporalCapabilityInputContract> Project(
        RelationQueryExecutionSlice executionSlice)
    {
        ArgumentNullException.ThrowIfNull(executionSlice);
        List<RelationQueryTemporalCapabilityInputContract> requirements = [];
        foreach (var node in executionSlice.Nodes)
        {
            if (node.TemporalJoin is not { } temporalJoin)
                continue;

            foreach (var use in GetUses(temporalJoin))
            {
                requirements.Add(new(
                    CreateId(node.Id, use.Capability, use.SemanticSite),
                    use.Capability,
                    node.Id,
                    use.SemanticSite));
            }
        }

        return
        [
            .. requirements
                .DistinctBy(static requirement => requirement.Id)
                .OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<TemporalCapabilityUse> GetUses(
        RelationQueryTemporalJoinExecution execution)
    {
        List<TemporalCapabilityUse> uses = [];
        var temporalRoot = GetTemporalRoot(execution);
        var matchRoot = execution.Definition.Match switch
        {
            TemporalPointInIntervalMatch => $"{temporalRoot}/pointInInterval",
            TemporalIntervalOverlapMatch => $"{temporalRoot}/intervalOverlap",
            _ => throw new InvalidOperationException("Unsupported prepared temporal join match type.")
        };

        uses.Add(new(
            execution.Definition.Match is TemporalPointInIntervalMatch
                ? RelationQueryTemporalExecutionCapability.PointInInterval
                : RelationQueryTemporalExecutionCapability.IntervalOverlap,
            matchRoot));
        uses.Add(new(RelationQueryTemporalExecutionCapability.PreserveAllMatches, matchRoot));
        if (execution.Intervals.Any(static interval =>
                interval.Lower.Definition is ExpressionTemporalIntervalBound
                && interval.Upper.Definition is ExpressionTemporalIntervalBound))
        {
            uses.Add(new(RelationQueryTemporalExecutionCapability.ValidateIntervals, matchRoot));
        }
        uses.Add(new(RelationQueryTemporalExecutionCapability.InconclusiveEvidence, matchRoot));
        uses.Add(new(
            execution.Definition.Kind switch
            {
                JoinKind.Inner => RelationQueryTemporalExecutionCapability.InnerJoin,
                JoinKind.Left => RelationQueryTemporalExecutionCapability.LeftOuterJoin,
                JoinKind.Right => RelationQueryTemporalExecutionCapability.RightOuterJoin,
                JoinKind.Full => RelationQueryTemporalExecutionCapability.FullOuterJoin,
                _ => throw new InvalidOperationException("Unsupported prepared temporal join kind.")
            },
            execution.CorrelationSite.Analysis.Site.Id.Value));

        if (execution.Domain is { } domain)
        {
            uses.Add(new(
                domain switch
                {
                    ScalarTypeKind.Date => RelationQueryTemporalExecutionCapability.DateDomain,
                    ScalarTypeKind.DateTime => RelationQueryTemporalExecutionCapability.DateTimeDomain,
                    ScalarTypeKind.Instant => RelationQueryTemporalExecutionCapability.InstantDomain,
                    _ => throw new InvalidOperationException("Unsupported prepared temporal join domain.")
                },
                GetDomainSite(execution, matchRoot)));
        }

        foreach (var interval in execution.Intervals)
        {
            AddBound(interval.Lower, "lower");
            AddBound(interval.Upper, "upper");
        }

        return [.. uses];

        void AddBound(RelationQueryTemporalBoundExecution bound, string endpoint)
        {
            var site = bound.ValueSite?.Analysis.Site.Id.Value
                ?? $"{matchRoot}/interval/{bound.IntervalOrdinal}/{endpoint}";
            switch (bound.Definition)
            {
                case UnboundedTemporalIntervalBound:
                    uses.Add(new(RelationQueryTemporalExecutionCapability.UnboundedBoundary, site));
                    break;
                case ExpressionTemporalIntervalBound expression:
                    uses.Add(new(
                        expression.Inclusion == TemporalBoundaryInclusion.Inclusive
                            ? RelationQueryTemporalExecutionCapability.InclusiveBoundary
                            : RelationQueryTemporalExecutionCapability.ExclusiveBoundary,
                        site));
                    if (expression.NullBehavior == TemporalNullBoundBehavior.Unbounded)
                    {
                        uses.Add(new(RelationQueryTemporalExecutionCapability.NullAsUnbounded, site));
                    }
                    break;
                default:
                    throw new InvalidOperationException("Unsupported prepared temporal interval bound type.");
            }
        }
    }

    static RelationQueryInputId CreateId(
        QueryNodeId node,
        RelationQueryTemporalExecutionCapability capability,
        string semanticSite) =>
        new(
            $"input/temporal-capability/{((int)capability).ToString(CultureInfo.InvariantCulture)}"
            + $"/{Uri.EscapeDataString(node.Value)}/{Uri.EscapeDataString(semanticSite)}");

    static string GetTemporalRoot(RelationQueryTemporalJoinExecution execution)
    {
        const string correlationSuffix = "/correlation";
        var correlationSite = execution.CorrelationSite.Analysis.Site.Id.Value;
        if (!correlationSite.EndsWith(correlationSuffix, StringComparison.Ordinal))
            throw new InvalidOperationException("Prepared temporal correlation site has an unsupported identity.");

        return correlationSite[..^correlationSuffix.Length];
    }

    static string GetDomainSite(
        RelationQueryTemporalJoinExecution execution,
        string matchRoot) =>
        execution.PointSite?.Analysis.Site.Id.Value
        ?? execution.Intervals
            .SelectMany(static interval => new[] { interval.Lower, interval.Upper })
            .Select(static bound => bound.ValueSite?.Analysis.Site.Id.Value)
            .FirstOrDefault(static site => site is not null)
        ?? matchRoot;

    readonly record struct TemporalCapabilityUse(
        RelationQueryTemporalExecutionCapability Capability,
        string SemanticSite);
}
