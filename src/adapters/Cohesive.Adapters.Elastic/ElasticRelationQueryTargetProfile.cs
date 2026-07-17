using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.Elastic;

/// <summary>Conservative, versioned capability profile for canonical Elasticsearch query compilation.</summary>
public static class ElasticRelationQueryTargetProfile
{
    /// <summary>Stable Elasticsearch search interpretation-target identity.</summary>
    public static RelationQueryTargetId Target { get; } = new("cohesive.adapters.elastic.search");

    /// <summary>Stable canonical v1 capability-profile identity.</summary>
    public static RelationQueryTargetProfileId ProfileId { get; } = new(
        "cohesive.adapters.elastic.search/canonical-v1");

    /// <summary>Operating boundary requiring one physical source and concrete index.</summary>
    public static RelationQueryOperatingBoundaryId SingleIndexBoundary { get; } = new(
        "elastic/boundary/single-index");

    /// <summary>Operating boundary requiring scalar operands.</summary>
    public static RelationQueryOperatingBoundaryId ScalarOperandsBoundary { get; } = new(
        "elastic/boundary/scalar-operands");

    /// <summary>Operating boundary requiring operands whose missing and null behavior is exactly bound.</summary>
    public static RelationQueryOperatingBoundaryId NonNullOperandsBoundary { get; } = new(
        "elastic/boundary/non-null-operands");

    /// <summary>Operating boundary requiring a stable unique final ordering field.</summary>
    public static RelationQueryOperatingBoundaryId StableOrderingBoundary { get; } = new(
        "elastic/boundary/stable-unique-ordering");

    /// <summary>Operating boundary requiring deterministic configured lowering providers.</summary>
    public static RelationQueryOperatingBoundaryId DeterministicProviderBoundary { get; } = new(
        "elastic/boundary/deterministic-provider");

    /// <summary>Operating boundary limiting a requested page size.</summary>
    public static RelationQueryOperatingBoundaryId PageSizeBoundary { get; } = new(
        "elastic/boundary/max-page-size");

    /// <summary>Default canonical v1 page-size limit.</summary>
    public const int MaximumPageSize = ElasticRelationQueryStorageBinding.DefaultMaximumPageSize;

    /// <summary>
    /// Default capability profile. It advertises only the single-index operation families interpreted by the
    /// canonical v1 compiler; exact mapping, document scope, retrieval encoding, normalization, pagination
    /// consistency, result-window, and strategy checks remain binding-scoped compiler obligations.
    /// </summary>
    public static RelationQueryTargetCapabilityProfile Default { get; } = CreateProfile();

    /// <summary>
    /// Default realization policy permitting constrained strategies only after their declared operating boundaries
    /// have been validated.
    /// </summary>
    public static RelationQueryRealizationPolicy Policy { get; } = new(
        new("cohesive.adapters.elastic.search/realization-policy-v1"),
        conventionSetVersion: ElasticRelationQueryStorageBinding.SemanticPathConventionSet,
        constrainedRealizations: RelationQueryConstrainedRealizationPolicy.AllowValidated);

    static RelationQueryTargetCapabilityProfile CreateProfile()
    {
        ImmutableArray<RelationQueryOperatingBoundary> boundaries =
        [
            new(SingleIndexBoundary, RelationQueryOperatingBoundaryKind.SingleSource),
            new(ScalarOperandsBoundary, RelationQueryOperatingBoundaryKind.ScalarOperands),
            new(NonNullOperandsBoundary, RelationQueryOperatingBoundaryKind.NonNullOperands),
            new(StableOrderingBoundary, RelationQueryOperatingBoundaryKind.StableUniqueOrdering),
            new(DeterministicProviderBoundary, RelationQueryOperatingBoundaryKind.DeterministicProvider),
            new(PageSizeBoundary, RelationQueryOperatingBoundaryKind.MaximumPageSize, MaximumPageSize)
        ];

        List<(string Id, RelationQueryCapability Capability, ImmutableArray<RelationQueryOperatingBoundaryId> Boundaries)>
            declarations = [];
        foreach (var logical in LogicalCapabilities)
        {
            declarations.Add((
                $"logical/{(int)logical}",
                new LogicalRelationQueryCapability(logical),
                LogicalBoundaries(logical)));
        }

        foreach (var expression in ExpressionCapabilities())
        {
            declarations.Add((
                $"expression/{Uri.EscapeDataString(expression.Value)}",
                new ExpressionRelationQueryCapability(expression, ExprCapabilityRequirementKind.Operation),
                ExpressionBoundaries(expression)));
        }

        foreach (var role in StructuralRoles)
        {
            foreach (var path in StructuralPaths)
            {
                declarations.Add((
                    $"structural/{(int)role}/{(int)path}",
                    new StructuralRelationQueryCapability(role, path),
                    [SingleIndexBoundary]));
            }
        }

        foreach (var guarantee in GuaranteeCapabilities)
        {
            declarations.Add((
                $"guarantee/{(int)guarantee}",
                new GuaranteeRelationQueryCapability(guarantee),
                GuaranteeBoundaries(guarantee)));
        }

        foreach (var boundary in boundaries)
        {
            declarations.Add((
                $"boundary-validator/{Uri.EscapeDataString(boundary.Id.Value)}",
                new OperatingBoundaryValidationRelationQueryCapability(boundary.Id),
                []));
        }

        var evidence = declarations
            .Select(static declaration => new RelationQueryTargetCapabilityEvidence(
                new($"elastic/capability/{declaration.Id}"),
                declaration.Capability,
                declaration.Boundaries))
            .ToImmutableArray();
        return new(
            Target,
            ProfileId,
            [RelationQueryDocument.CurrentSchemaVersion],
            [RelationQueryCompilationProvenance.CurrentCompilerProfile],
            evidence,
            boundaries,
            "Canonical Elasticsearch v1: exact single-index structured rows, root scalar-array membership, and global or composite-grouped row counts within declared boundaries.");
    }

    static ImmutableArray<RelationQueryOperatingBoundaryId> LogicalBoundaries(
        RelationQueryLogicalCapabilityKind logical) => logical switch
    {
        RelationQueryLogicalCapabilityKind.AggregateGrouping =>
            [
                SingleIndexBoundary,
                NonNullOperandsBoundary,
                ScalarOperandsBoundary,
                StableOrderingBoundary,
                PageSizeBoundary
            ],
        RelationQueryLogicalCapabilityKind.Ordering
            or RelationQueryLogicalCapabilityKind.AscendingOrdering
            or RelationQueryLogicalCapabilityKind.DescendingOrdering
            or RelationQueryLogicalCapabilityKind.NullsFirst
            or RelationQueryLogicalCapabilityKind.NullsLast =>
            [SingleIndexBoundary, ScalarOperandsBoundary],
        RelationQueryLogicalCapabilityKind.StableTieOrdering =>
            [SingleIndexBoundary, StableOrderingBoundary],
        RelationQueryLogicalCapabilityKind.OffsetPaging
            or RelationQueryLogicalCapabilityKind.KeysetPaging =>
            [SingleIndexBoundary, StableOrderingBoundary, PageSizeBoundary],
        RelationQueryLogicalCapabilityKind.CountAggregate =>
            [SingleIndexBoundary],
        _ => [SingleIndexBoundary]
    };

    static IEnumerable<ExprCapabilityId> ExpressionCapabilities()
    {
        yield return ExprCapabilities.Field;
        yield return ExprCapabilities.NestedFieldPath;
        yield return ExprCapabilities.Parameter;
        yield return ExprCapabilities.Constant;
        yield return ExprCapabilities.TypedField;
        yield return ExprCapabilities.TypedLiteral;
        yield return ExprCapabilities.ForUnary(UnaryOperator.Not);
        foreach (var @operator in SupportedBinaryOperators)
            yield return ExprCapabilities.ForBinary(@operator);
        yield return ExprCapabilities.ForFunction(ExprFunctionNames.Contains);
        yield return ExprCapabilities.ForFunction(ExprFunctionNames.EndsWith);
    }

    static ImmutableArray<RelationQueryOperatingBoundaryId> ExpressionBoundaries(
        ExprCapabilityId expression)
    {
        if (expression == ExprCapabilities.ForFunction(ExprFunctionNames.EndsWith))
        {
            return
            [
                SingleIndexBoundary,
                NonNullOperandsBoundary,
                ScalarOperandsBoundary,
                DeterministicProviderBoundary
            ];
        }
        if (expression == ExprCapabilities.ForFunction(ExprFunctionNames.Contains))
            return [SingleIndexBoundary, NonNullOperandsBoundary];
        if (expression == ExprCapabilities.ForUnary(UnaryOperator.Not)
            || SupportedBinaryOperators.Any(@operator =>
                expression == ExprCapabilities.ForBinary(@operator)))
        {
            return [SingleIndexBoundary, NonNullOperandsBoundary, ScalarOperandsBoundary];
        }
        return [SingleIndexBoundary];
    }

    static ImmutableArray<RelationQueryOperatingBoundaryId> GuaranteeBoundaries(
        RelationQueryGuaranteeCapabilityKind guarantee) => guarantee switch
    {
        RelationQueryGuaranteeCapabilityKind.Ordering
            or RelationQueryGuaranteeCapabilityKind.NullPlacement =>
            [SingleIndexBoundary, ScalarOperandsBoundary],
        RelationQueryGuaranteeCapabilityKind.StablePaging =>
            [SingleIndexBoundary, StableOrderingBoundary, PageSizeBoundary],
        RelationQueryGuaranteeCapabilityKind.Grouping =>
            [
                SingleIndexBoundary,
                NonNullOperandsBoundary,
                ScalarOperandsBoundary,
                StableOrderingBoundary,
                PageSizeBoundary
            ],
        RelationQueryGuaranteeCapabilityKind.DeterministicResult =>
            [SingleIndexBoundary, DeterministicProviderBoundary],
        RelationQueryGuaranteeCapabilityKind.MissingNullDistinction =>
            [SingleIndexBoundary, NonNullOperandsBoundary],
        _ => [SingleIndexBoundary]
    };

    static ImmutableArray<RelationQueryLogicalCapabilityKind> LogicalCapabilities =>
    [
        RelationQueryLogicalCapabilityKind.Source,
        RelationQueryLogicalCapabilityKind.Filter,
        RelationQueryLogicalCapabilityKind.Projection,
        RelationQueryLogicalCapabilityKind.ProjectionAssignment,
        RelationQueryLogicalCapabilityKind.Aggregation,
        RelationQueryLogicalCapabilityKind.AggregateGrouping,
        RelationQueryLogicalCapabilityKind.CountAggregate,
        RelationQueryLogicalCapabilityKind.Ordering,
        RelationQueryLogicalCapabilityKind.AscendingOrdering,
        RelationQueryLogicalCapabilityKind.DescendingOrdering,
        RelationQueryLogicalCapabilityKind.NullsFirst,
        RelationQueryLogicalCapabilityKind.NullsLast,
        RelationQueryLogicalCapabilityKind.StableTieOrdering,
        RelationQueryLogicalCapabilityKind.OffsetPaging,
        RelationQueryLogicalCapabilityKind.KeysetPaging,
        RelationQueryLogicalCapabilityKind.QueryRowsResult,
        RelationQueryLogicalCapabilityKind.QueryAggregationResult,
        RelationQueryLogicalCapabilityKind.AlwaysPresentBinding
    ];

    static ImmutableArray<RelationQueryStructuralCapabilityRole> StructuralRoles =>
    [
        RelationQueryStructuralCapabilityRole.BindingRead,
        RelationQueryStructuralCapabilityRole.ProjectionTarget,
        RelationQueryStructuralCapabilityRole.GroupingTarget,
        RelationQueryStructuralCapabilityRole.AggregateTarget,
        RelationQueryStructuralCapabilityRole.OutputSelection,
        RelationQueryStructuralCapabilityRole.CompleteValue
    ];

    static ImmutableArray<RelationQueryStructuralPathKind> StructuralPaths =>
    [
        RelationQueryStructuralPathKind.RootValue,
        RelationQueryStructuralPathKind.TopLevelField,
        RelationQueryStructuralPathKind.NestedField
    ];

    static ImmutableArray<RelationQueryGuaranteeCapabilityKind> GuaranteeCapabilities =>
    [
        RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
        RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
        RelationQueryGuaranteeCapabilityKind.Cardinality,
        RelationQueryGuaranteeCapabilityKind.Ordering,
        RelationQueryGuaranteeCapabilityKind.NullPlacement,
        RelationQueryGuaranteeCapabilityKind.StablePaging,
        RelationQueryGuaranteeCapabilityKind.Grouping,
        RelationQueryGuaranteeCapabilityKind.Aggregation,
        RelationQueryGuaranteeCapabilityKind.DeterministicResult,
        RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
        RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence
    ];

    static ImmutableArray<BinaryOperator> SupportedBinaryOperators =>
    [
        BinaryOperator.Eq,
        BinaryOperator.Ne,
        BinaryOperator.Gt,
        BinaryOperator.Ge,
        BinaryOperator.Lt,
        BinaryOperator.Le,
        BinaryOperator.And,
        BinaryOperator.Or
    ];
}
