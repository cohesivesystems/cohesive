using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Execution;

static class RelationQueryInMemoryTargetProfile
{
    const string TargetIdentity = "cohesive.relations.in-memory";
    const string DefaultProfileIdentity = "cohesive.relations.in-memory/realization-v2";

    public static RelationQueryTargetCapabilityProfile Default { get; } = CreateCore(
        RelationQueryTemporalExecutionCapabilityProfile.All,
        DefaultProfileIdentity);

    public static RelationQueryRealizationPolicy Policy { get; } = new(
        new("cohesive.relations.in-memory/realization-policy-v2"),
        conventionSetVersion: "cohesive.relations/conventions-v1");

    public static RelationQueryTargetCapabilityProfile Create(
        RelationQueryTemporalExecutionCapabilityProfile temporalCapabilities)
    {
        ArgumentNullException.ThrowIfNull(temporalCapabilities);
        if (ReferenceEquals(temporalCapabilities, RelationQueryTemporalExecutionCapabilityProfile.All)
            || temporalCapabilities.SupportedCapabilities.SequenceEqual(
                RelationQueryTemporalExecutionCapabilityProfile.All.SupportedCapabilities))
        {
            return Default;
        }

        var temporalIdentity = temporalCapabilities.SupportedCapabilities.IsDefaultOrEmpty
            ? "none"
            : string.Join(
                ",",
                temporalCapabilities.SupportedCapabilities.Select(static capability =>
                    ((int)capability).ToString(CultureInfo.InvariantCulture)));
        return CreateCore(
            temporalCapabilities,
            $"{DefaultProfileIdentity}/temporal/{temporalIdentity}");
    }

    static RelationQueryTargetCapabilityProfile CreateCore(
        RelationQueryTemporalExecutionCapabilityProfile temporalCapabilities,
        string profileIdentity)
    {
        List<RelationQueryCapability> capabilities =
        [
            .. LogicalCapabilities.Select(static capability =>
                new LogicalRelationQueryCapability(capability)),
            .. RelationQueryExpressionEvaluator.SupportedCapabilities.SupportedCapabilities.Select(static capability =>
                new ExpressionRelationQueryCapability(
                    capability,
                    ExprCapabilityRequirementKind.Operation)),
            .. temporalCapabilities.SupportedCapabilities.Select(static capability =>
                new TemporalRelationQueryCapability(capability)),
            .. StructuralCapabilities(),
            .. GuaranteeCapabilities.Select(static capability =>
                new GuaranteeRelationQueryCapability(capability))
        ];
        var evidence = capabilities
            .Distinct()
            .OrderBy(RelationQueryRealizationOrdering.CapabilityKey, StringComparer.Ordinal)
            .Select(capability => new RelationQueryTargetCapabilityEvidence(
                new($"in-memory/capability/{Uri.EscapeDataString(RelationQueryRealizationOrdering.CapabilityKey(capability))}"),
                capability))
            .ToImmutableArray();
        return new(
            new(TargetIdentity),
            new(profileIdentity),
            [RelationQueryDocument.CurrentSchemaVersion],
            [RelationQueryCompilationProvenance.CurrentCompilerProfile],
            evidence);
    }

    static IEnumerable<RelationQueryCapability> StructuralCapabilities()
    {
        RelationQueryStructuralPathKind[] supportedPaths =
        [
            RelationQueryStructuralPathKind.RootValue,
            RelationQueryStructuralPathKind.TopLevelField,
            RelationQueryStructuralPathKind.NestedField
        ];
        foreach (var role in StructuralRoles)
        {
            foreach (var path in supportedPaths)
                yield return new StructuralRelationQueryCapability(role, path);
        }

        yield return new StructuralRelationQueryCapability(
            RelationQueryStructuralCapabilityRole.CurrentItemRead,
            RelationQueryStructuralPathKind.CollectionElement);
    }

    static ImmutableArray<RelationQueryStructuralCapabilityRole> StructuralRoles =>
    [
        RelationQueryStructuralCapabilityRole.BindingRead,
        RelationQueryStructuralCapabilityRole.CurrentItemRead,
        RelationQueryStructuralCapabilityRole.OccurrenceEvidenceReconstruction,
        RelationQueryStructuralCapabilityRole.ProjectionTarget,
        RelationQueryStructuralCapabilityRole.GroupingTarget,
        RelationQueryStructuralCapabilityRole.AggregateTarget,
        RelationQueryStructuralCapabilityRole.OutputSelection,
        RelationQueryStructuralCapabilityRole.CompleteValue
    ];

    static ImmutableArray<RelationQueryLogicalCapabilityKind> LogicalCapabilities =>
    [
        RelationQueryLogicalCapabilityKind.Source,
        RelationQueryLogicalCapabilityKind.Filter,
        RelationQueryLogicalCapabilityKind.RelationshipTraversal,
        RelationQueryLogicalCapabilityKind.ForwardRelationshipTraversal,
        RelationQueryLogicalCapabilityKind.InverseRelationshipTraversal,
        RelationQueryLogicalCapabilityKind.AtMostOneRelationshipTraversal,
        RelationQueryLogicalCapabilityKind.ManyRelationshipTraversal,
        RelationQueryLogicalCapabilityKind.RequiredRelationshipTraversal,
        RelationQueryLogicalCapabilityKind.OptionalRelationshipTraversal,
        RelationQueryLogicalCapabilityKind.Join,
        RelationQueryLogicalCapabilityKind.InnerJoin,
        RelationQueryLogicalCapabilityKind.LeftOuterJoin,
        RelationQueryLogicalCapabilityKind.RightOuterJoin,
        RelationQueryLogicalCapabilityKind.FullOuterJoin,
        RelationQueryLogicalCapabilityKind.TemporalJoin,
        RelationQueryLogicalCapabilityKind.ExpandCollection,
        RelationQueryLogicalCapabilityKind.Projection,
        RelationQueryLogicalCapabilityKind.ProjectionAssignment,
        RelationQueryLogicalCapabilityKind.DistinctRows,
        RelationQueryLogicalCapabilityKind.DistinctKeys,
        RelationQueryLogicalCapabilityKind.Aggregation,
        RelationQueryLogicalCapabilityKind.AggregateGrouping,
        RelationQueryLogicalCapabilityKind.AggregateFilter,
        RelationQueryLogicalCapabilityKind.CountAggregate,
        RelationQueryLogicalCapabilityKind.SumAggregate,
        RelationQueryLogicalCapabilityKind.MinimumAggregate,
        RelationQueryLogicalCapabilityKind.MaximumAggregate,
        RelationQueryLogicalCapabilityKind.AverageAggregate,
        RelationQueryLogicalCapabilityKind.AnyAggregate,
        RelationQueryLogicalCapabilityKind.AllAggregate,
        RelationQueryLogicalCapabilityKind.Ordering,
        RelationQueryLogicalCapabilityKind.AscendingOrdering,
        RelationQueryLogicalCapabilityKind.DescendingOrdering,
        RelationQueryLogicalCapabilityKind.NullsFirst,
        RelationQueryLogicalCapabilityKind.NullsLast,
        RelationQueryLogicalCapabilityKind.StableTieOrdering,
        RelationQueryLogicalCapabilityKind.OffsetPaging,
        RelationQueryLogicalCapabilityKind.KeysetPaging,
        RelationQueryLogicalCapabilityKind.OnePerRootRelationOutput,
        RelationQueryLogicalCapabilityKind.ZeroOrOnePerRootRelationOutput,
        RelationQueryLogicalCapabilityKind.ManyPerRootRelationOutput,
        RelationQueryLogicalCapabilityKind.SetRelationOutput,
        RelationQueryLogicalCapabilityKind.RelationOutputIdentity,
        RelationQueryLogicalCapabilityKind.RelationInvariant,
        RelationQueryLogicalCapabilityKind.QueryRowsResult,
        RelationQueryLogicalCapabilityKind.QueryAggregationResult,
        RelationQueryLogicalCapabilityKind.AlwaysPresentBinding,
        RelationQueryLogicalCapabilityKind.MayBeAbsentBinding
    ];

    static ImmutableArray<RelationQueryGuaranteeCapabilityKind> GuaranteeCapabilities =>
    [
        RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
        RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
        RelationQueryGuaranteeCapabilityKind.JoinMembership,
        RelationQueryGuaranteeCapabilityKind.Cardinality,
        RelationQueryGuaranteeCapabilityKind.RelationshipDirection,
        RelationQueryGuaranteeCapabilityKind.RelationshipMultiplicity,
        RelationQueryGuaranteeCapabilityKind.TemporalDomain,
        RelationQueryGuaranteeCapabilityKind.TemporalBoundary,
        RelationQueryGuaranteeCapabilityKind.UnboundedTemporalBoundary,
        RelationQueryGuaranteeCapabilityKind.Ordering,
        RelationQueryGuaranteeCapabilityKind.NullPlacement,
        RelationQueryGuaranteeCapabilityKind.StablePaging,
        RelationQueryGuaranteeCapabilityKind.Grouping,
        RelationQueryGuaranteeCapabilityKind.Aggregation,
        RelationQueryGuaranteeCapabilityKind.CollectionElementCorrelation,
        RelationQueryGuaranteeCapabilityKind.DuplicateHandling,
        RelationQueryGuaranteeCapabilityKind.OutputIdentity,
        RelationQueryGuaranteeCapabilityKind.OutputMode,
        RelationQueryGuaranteeCapabilityKind.InvariantEnforcement,
        RelationQueryGuaranteeCapabilityKind.DeterministicResult,
        RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance,
        RelationQueryGuaranteeCapabilityKind.RelationRootCorrelation,
        RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
        RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence
    ];
}
