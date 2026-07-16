using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Conservative, versioned capability profile for canonical Cosmos SQL compilation.</summary>
public static class CosmosRelationQueryTargetProfile
{
    /// <summary>Stable Cosmos SQL interpretation-target identity.</summary>
    public static RelationQueryTargetId Target { get; } = new("cohesive.adapters.cosmos.sql");

    /// <summary>Stable v1 capability-profile identity.</summary>
    public static RelationQueryTargetProfileId ProfileId { get; } = new(
        "cohesive.adapters.cosmos.sql/canonical-v1");

    /// <summary>Operating boundary requiring one physical source/container.</summary>
    public static RelationQueryOperatingBoundaryId SingleSourceBoundary { get; } = new(
        "cosmos/boundary/single-source");

    /// <summary>Operating boundary requiring non-null operands.</summary>
    public static RelationQueryOperatingBoundaryId NonNullOperandsBoundary { get; } = new(
        "cosmos/boundary/non-null-operands");

    /// <summary>Operating boundary requiring scalar operands.</summary>
    public static RelationQueryOperatingBoundaryId ScalarOperandsBoundary { get; } = new(
        "cosmos/boundary/scalar-operands");

    /// <summary>Operating boundary requiring a stable unique final ordering key.</summary>
    public static RelationQueryOperatingBoundaryId StableOrderingBoundary { get; } = new(
        "cosmos/boundary/stable-unique-ordering");

    /// <summary>Maximum offset-page size supported by the v1 compiler profile.</summary>
    public const int MaximumPageSize = 1_000;

    /// <summary>
    /// Largest integer through which every integer is exactly representable by Cosmos's binary64 numeric domain.
    /// </summary>
    public const long MaximumExactInteger = 9_007_199_254_740_991L;

    /// <summary>Operating boundary limiting the requested page size.</summary>
    public static RelationQueryOperatingBoundaryId PageSizeBoundary { get; } = new(
        "cosmos/boundary/max-page-size");

    /// <summary>Operating boundary keeping row count inside Cosmos's exact integer domain.</summary>
    public static RelationQueryOperatingBoundaryId ExactCountInputRowsBoundary { get; } = new(
        "cosmos/boundary/exact-count-input-rows");

    /// <summary>
    /// Default Cosmos target profile. It advertises operation families interpreted by the canonical v1 compiler;
    /// demand-scoped structural and value-contract checks further constrain their exact supported variants.
    /// </summary>
    public static RelationQueryTargetCapabilityProfile Default { get; } = CreateProfile();

    /// <summary>
    /// Default realization policy. Constrained strategies are permitted only when the profile and compiler retain
    /// attributable boundary validation.
    /// </summary>
    public static RelationQueryRealizationPolicy Policy { get; } = new(
        new("cohesive.adapters.cosmos.sql/realization-policy-v1"),
        conventionSetVersion: CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
        constrainedRealizations: RelationQueryConstrainedRealizationPolicy.AllowValidated);

    static RelationQueryTargetCapabilityProfile CreateProfile()
    {
        ImmutableArray<RelationQueryOperatingBoundary> boundaries =
        [
            new(SingleSourceBoundary, RelationQueryOperatingBoundaryKind.SingleSource),
            new(NonNullOperandsBoundary, RelationQueryOperatingBoundaryKind.NonNullOperands),
            new(ScalarOperandsBoundary, RelationQueryOperatingBoundaryKind.ScalarOperands),
            new(StableOrderingBoundary, RelationQueryOperatingBoundaryKind.StableUniqueOrdering),
            new(PageSizeBoundary, RelationQueryOperatingBoundaryKind.MaximumPageSize, MaximumPageSize),
            new(
                ExactCountInputRowsBoundary,
                RelationQueryOperatingBoundaryKind.MaximumInputRows,
                MaximumExactInteger)
        ];

        List<(string Id, RelationQueryCapability Capability, ImmutableArray<RelationQueryOperatingBoundaryId> Boundaries)>
            declarations = [];
        foreach (var logical in LogicalCapabilities)
        {
            var operationBoundaries = logical switch
            {
                RelationQueryLogicalCapabilityKind.ExpandCollection =>
                    [SingleSourceBoundary, NonNullOperandsBoundary],
                RelationQueryLogicalCapabilityKind.DistinctRows
                    or RelationQueryLogicalCapabilityKind.AggregateGrouping
                    or RelationQueryLogicalCapabilityKind.MinimumAggregate
                    or RelationQueryLogicalCapabilityKind.MaximumAggregate
                    or RelationQueryLogicalCapabilityKind.Ordering
                    or RelationQueryLogicalCapabilityKind.AscendingOrdering
                    or RelationQueryLogicalCapabilityKind.DescendingOrdering
                    or RelationQueryLogicalCapabilityKind.NullsFirst
                    or RelationQueryLogicalCapabilityKind.NullsLast =>
                    [SingleSourceBoundary, NonNullOperandsBoundary, ScalarOperandsBoundary],
                RelationQueryLogicalCapabilityKind.StableTieOrdering =>
                    [SingleSourceBoundary, StableOrderingBoundary],
                RelationQueryLogicalCapabilityKind.CountAggregate =>
                    [SingleSourceBoundary, ExactCountInputRowsBoundary],
                RelationQueryLogicalCapabilityKind.OffsetPaging =>
                    [SingleSourceBoundary, StableOrderingBoundary, PageSizeBoundary],
                _ => ImmutableArray.Create(SingleSourceBoundary)
            };
            declarations.Add((
                $"logical/{(int)logical}",
                new LogicalRelationQueryCapability(logical),
                operationBoundaries));
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
                    [SingleSourceBoundary]));
            }
        }

        foreach (var guarantee in GuaranteeCapabilities)
        {
            declarations.Add((
                $"guarantee/{(int)guarantee}",
                new GuaranteeRelationQueryCapability(guarantee),
                []));
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
                new($"cosmos/capability/{declaration.Id}"),
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
            "Canonical Cosmos SQL v1: exact single-container row and aggregation compilation within declared boundaries.");
    }

    static IEnumerable<ExprCapabilityId> ExpressionCapabilities()
    {
        yield return ExprCapabilities.Field;
        yield return ExprCapabilities.NestedFieldPath;
        yield return ExprCapabilities.Parameter;
        yield return ExprCapabilities.Constant;
        yield return ExprCapabilities.TypedField;
        yield return ExprCapabilities.TypedLiteral;
        yield return ExprCapabilities.Conditional;
        yield return ExprCapabilities.CurrentItem;
        yield return ExprCapabilities.ForUnary(UnaryOperator.Not);
        foreach (var @operator in SupportedBinaryOperators)
            yield return ExprCapabilities.ForBinary(@operator);
        yield return ExprCapabilities.ForFunction(ExprFunctionNames.Contains);
    }

    static ImmutableArray<RelationQueryOperatingBoundaryId> ExpressionBoundaries(
        ExprCapabilityId expression)
    {
        if (expression == ExprCapabilities.ForFunction(ExprFunctionNames.Contains))
            return [SingleSourceBoundary, NonNullOperandsBoundary];
        if (expression == ExprCapabilities.ForUnary(UnaryOperator.Not)
            || SupportedBinaryOperators.Any(@operator =>
                expression == ExprCapabilities.ForBinary(@operator)))
        {
            return [SingleSourceBoundary, NonNullOperandsBoundary, ScalarOperandsBoundary];
        }
        return [SingleSourceBoundary];
    }

    static ImmutableArray<RelationQueryLogicalCapabilityKind> LogicalCapabilities =>
    [
        RelationQueryLogicalCapabilityKind.Source,
        RelationQueryLogicalCapabilityKind.Filter,
        RelationQueryLogicalCapabilityKind.ExpandCollection,
        RelationQueryLogicalCapabilityKind.Projection,
        RelationQueryLogicalCapabilityKind.ProjectionAssignment,
        RelationQueryLogicalCapabilityKind.DistinctRows,
        RelationQueryLogicalCapabilityKind.Aggregation,
        RelationQueryLogicalCapabilityKind.AggregateGrouping,
        RelationQueryLogicalCapabilityKind.CountAggregate,
        RelationQueryLogicalCapabilityKind.MinimumAggregate,
        RelationQueryLogicalCapabilityKind.MaximumAggregate,
        RelationQueryLogicalCapabilityKind.Ordering,
        RelationQueryLogicalCapabilityKind.AscendingOrdering,
        RelationQueryLogicalCapabilityKind.DescendingOrdering,
        RelationQueryLogicalCapabilityKind.NullsFirst,
        RelationQueryLogicalCapabilityKind.NullsLast,
        RelationQueryLogicalCapabilityKind.StableTieOrdering,
        RelationQueryLogicalCapabilityKind.OffsetPaging,
        RelationQueryLogicalCapabilityKind.QueryRowsResult,
        RelationQueryLogicalCapabilityKind.QueryAggregationResult,
        RelationQueryLogicalCapabilityKind.AlwaysPresentBinding
    ];

    static ImmutableArray<RelationQueryStructuralCapabilityRole> StructuralRoles =>
    [
        RelationQueryStructuralCapabilityRole.BindingRead,
        RelationQueryStructuralCapabilityRole.CurrentItemRead,
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
        RelationQueryGuaranteeCapabilityKind.Ordering,
        RelationQueryGuaranteeCapabilityKind.NullPlacement,
        RelationQueryGuaranteeCapabilityKind.StablePaging,
        RelationQueryGuaranteeCapabilityKind.Grouping,
        RelationQueryGuaranteeCapabilityKind.Aggregation,
        RelationQueryGuaranteeCapabilityKind.DuplicateHandling,
        RelationQueryGuaranteeCapabilityKind.Cardinality,
        RelationQueryGuaranteeCapabilityKind.OutputIdentity,
        RelationQueryGuaranteeCapabilityKind.OutputMode,
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
