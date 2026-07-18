using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Conservative, versioned capability profile for canonical PostgreSQL query compilation.</summary>
/// <remarks>
/// The profile advertises the operation families understood by the v1 compiler. Exact table mappings, column value
/// encodings, collations, type domains, source co-location, snapshot behavior, interval validity, and ordering evidence
/// remain binding-scoped compiler obligations. The compiler must reject a declaration when those physical facts do not
/// prove every boundary retained by the realization report.
/// </remarks>
public static class PostgresRelationQueryTargetProfile
{
    /// <summary>Stable PostgreSQL SQL interpretation-target identity.</summary>
    public static RelationQueryTargetId Target { get; } = new("cohesive.adapters.postgres.sql");

    /// <summary>Stable canonical v1 capability-profile identity.</summary>
    public static RelationQueryTargetProfileId ProfileId { get; } = new(
        "cohesive.adapters.postgres.sql/canonical-v1");

    /// <summary>Stable convention set used by the default PostgreSQL realization and binding policy.</summary>
    public const string DefaultConventionSetVersion =
        "cohesive.adapters.postgres.sql/semantic-path-conventions/v1";

    /// <summary>Maximum page size accepted by the canonical v1 compiler profile.</summary>
    public const int MaximumPageSize = 1_000;

    /// <summary>Boundary requiring all participating tables to reside in one PostgreSQL database execution domain.</summary>
    public static RelationQueryOperatingBoundaryId SingleDatabaseBoundary { get; } = new(
        "postgres/boundary/single-database");

    /// <summary>Boundary requiring authoritative, complete physical input evidence.</summary>
    public static RelationQueryOperatingBoundaryId CompleteInputEvidenceBoundary { get; } = new(
        "postgres/boundary/complete-input-evidence");

    /// <summary>Boundary requiring participating operands to be non-null and non-missing where SQL differs canonically.</summary>
    public static RelationQueryOperatingBoundaryId NonNullOperandsBoundary { get; } = new(
        "postgres/boundary/non-null-operands");

    /// <summary>Boundary requiring participating expression and aggregate operands to have scalar SQL encodings.</summary>
    public static RelationQueryOperatingBoundaryId ScalarOperandsBoundary { get; } = new(
        "postgres/boundary/scalar-operands");

    /// <summary>Boundary requiring temporal operands to share one exact PostgreSQL temporal domain.</summary>
    public static RelationQueryOperatingBoundaryId HomogeneousTemporalDomainBoundary { get; } = new(
        "postgres/boundary/homogeneous-temporal-domain");

    /// <summary>Boundary requiring explicit exact numeric aggregate-domain evidence.</summary>
    public static RelationQueryOperatingBoundaryId ExactNumericAggregateDomainBoundary { get; } = new(
        "postgres/boundary/exact-numeric-aggregate-domain");

    /// <summary>Boundary requiring explicit finite canonical CLR physical temporal-domain evidence.</summary>
    public static RelationQueryOperatingBoundaryId ExactTemporalDomainBoundary { get; } = new(
        "postgres/boundary/exact-temporal-domain");

    /// <summary>Boundary limiting rooted non-set relation execution to one supplied root per invocation.</summary>
    public static RelationQueryOperatingBoundaryId SuppliedRelationRootBoundary { get; } = new(
        "postgres/boundary/supplied-relation-root");

    /// <summary>Boundary requiring a stable unique final ordering key.</summary>
    public static RelationQueryOperatingBoundaryId StableOrderingBoundary { get; } = new(
        "postgres/boundary/stable-unique-ordering");

    /// <summary>Boundary requiring deterministic provider behavior, including exact collation semantics.</summary>
    public static RelationQueryOperatingBoundaryId DeterministicProviderBoundary { get; } = new(
        "postgres/boundary/deterministic-provider");

    /// <summary>Boundary limiting the number of rows requested by one page.</summary>
    public static RelationQueryOperatingBoundaryId PageSizeBoundary { get; } = new(
        "postgres/boundary/max-page-size");

    /// <summary>
    /// Default PostgreSQL capability profile for exact query-row, aggregation, relationship, explicit-join, and
    /// valid-time-join compilation within the declared boundaries.
    /// </summary>
    public static RelationQueryTargetCapabilityProfile Default { get; } = CreateProfile();

    /// <summary>
    /// Default realization policy permitting a constrained strategy only after every retained boundary has
    /// attributable target validation.
    /// </summary>
    public static RelationQueryRealizationPolicy Policy { get; } = new(
        new("cohesive.adapters.postgres.sql/realization-policy-v1"),
        conventionSetVersion: DefaultConventionSetVersion,
        constrainedRealizations: RelationQueryConstrainedRealizationPolicy.AllowValidated);

    static RelationQueryTargetCapabilityProfile CreateProfile()
    {
        ImmutableArray<RelationQueryOperatingBoundary> boundaries =
        [
            new(
                SingleDatabaseBoundary,
                RelationQueryOperatingBoundaryKind.SingleSource,
                description: "All participating PostgreSQL table bindings resolve to one database execution domain."),
            new(
                CompleteInputEvidenceBoundary,
                RelationQueryOperatingBoundaryKind.CompleteInputEvidence,
                description: "Physical mappings and query execution provide authoritative complete input evidence."),
            new(
                NonNullOperandsBoundary,
                RelationQueryOperatingBoundaryKind.NonNullOperands,
                description: "Bindings prove non-null and non-missing operands where PostgreSQL three-valued logic differs."),
            new(
                ScalarOperandsBoundary,
                RelationQueryOperatingBoundaryKind.ScalarOperands,
                description: "Bindings prove exact scalar SQL encodings and representable result domains."),
            new(
                HomogeneousTemporalDomainBoundary,
                RelationQueryOperatingBoundaryKind.HomogeneousTemporalDomain,
                description: "Every temporal operand uses one exact PostgreSQL temporal domain and precision contract."),
            new(
                ExactNumericAggregateDomainBoundary,
                RelationQueryOperatingBoundaryKind.ExactNumericAggregateDomain,
                description: "Persisted binding evidence proves exact canonical decimal aggregate intermediates and results."),
            new(
                ExactTemporalDomainBoundary,
                RelationQueryOperatingBoundaryKind.ExactTemporalDomain,
                description: "Persisted binding evidence proves finite canonical CLR-range temporal values and microsecond-aligned timestamps."),
            new(
                SuppliedRelationRootBoundary,
                RelationQueryOperatingBoundaryKind.SuppliedRelationRoot,
                description: "Each rooted non-set relation statement is invoked for exactly one explicitly supplied root occurrence."),
            new(
                StableOrderingBoundary,
                RelationQueryOperatingBoundaryKind.StableUniqueOrdering,
                description: "The final ordering contains an exact stable unique key."),
            new(
                DeterministicProviderBoundary,
                RelationQueryOperatingBoundaryKind.DeterministicProvider,
                description: "Provider settings, UTF-8 database semantics, standard identifier limits, collations, and lowering decisions are deterministic and semantically exact."),
            new(
                PageSizeBoundary,
                RelationQueryOperatingBoundaryKind.MaximumPageSize,
                MaximumPageSize,
                "The requested row page does not exceed the canonical PostgreSQL v1 limit.")
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

        foreach (var temporal in TemporalCapabilities)
        {
            declarations.Add((
                $"temporal/{(int)temporal}",
                new TemporalRelationQueryCapability(temporal),
                TemporalBoundaries(temporal)));
        }

        foreach (var structural in StructuralCapabilities)
        {
            declarations.Add((
                $"structural/{(int)structural.Role}/{(int)structural.Path}",
                new StructuralRelationQueryCapability(structural.Role, structural.Path),
                [SingleDatabaseBoundary, CompleteInputEvidenceBoundary]));
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
                new($"postgres/capability/{declaration.Id}"),
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
            "Canonical PostgreSQL SQL v1: exact rows and aggregations over co-located tables, including relationship, explicit inner/left, and valid-time joins, within binding-proven boundaries.");
    }

    static ImmutableArray<RelationQueryOperatingBoundaryId> LogicalBoundaries(
        RelationQueryLogicalCapabilityKind logical) => logical switch
        {
            RelationQueryLogicalCapabilityKind.RelationshipTraversal
                or RelationQueryLogicalCapabilityKind.ForwardRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.InverseRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.AtMostOneRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.ManyRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.RequiredRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.OptionalRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.Join
                or RelationQueryLogicalCapabilityKind.InnerJoin
                or RelationQueryLogicalCapabilityKind.LeftOuterJoin =>
                [SingleDatabaseBoundary, CompleteInputEvidenceBoundary],
            RelationQueryLogicalCapabilityKind.TemporalJoin =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    HomogeneousTemporalDomainBoundary,
                    ExactTemporalDomainBoundary
                ],
            RelationQueryLogicalCapabilityKind.SumAggregate
                or RelationQueryLogicalCapabilityKind.AverageAggregate =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    NonNullOperandsBoundary,
                    ScalarOperandsBoundary,
                    ExactNumericAggregateDomainBoundary
                ],
            RelationQueryLogicalCapabilityKind.AggregateGrouping
                or RelationQueryLogicalCapabilityKind.MinimumAggregate
                or RelationQueryLogicalCapabilityKind.MaximumAggregate
                =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    NonNullOperandsBoundary,
                    ScalarOperandsBoundary
                ],
            RelationQueryLogicalCapabilityKind.AggregateFilter =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    NonNullOperandsBoundary
                ],
            RelationQueryLogicalCapabilityKind.CountAggregate =>
                [SingleDatabaseBoundary, CompleteInputEvidenceBoundary],
            RelationQueryLogicalCapabilityKind.DistinctRows =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    DeterministicProviderBoundary
                ],
            RelationQueryLogicalCapabilityKind.Ordering
                or RelationQueryLogicalCapabilityKind.AscendingOrdering
                or RelationQueryLogicalCapabilityKind.DescendingOrdering
                or RelationQueryLogicalCapabilityKind.NullsFirst
                or RelationQueryLogicalCapabilityKind.NullsLast =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    ScalarOperandsBoundary,
                    DeterministicProviderBoundary
                ],
            RelationQueryLogicalCapabilityKind.StableTieOrdering =>
                [
                    SingleDatabaseBoundary,
                    StableOrderingBoundary,
                    DeterministicProviderBoundary
                ],
            RelationQueryLogicalCapabilityKind.OffsetPaging
                or RelationQueryLogicalCapabilityKind.KeysetPaging =>
                [
                    SingleDatabaseBoundary,
                    StableOrderingBoundary,
                    DeterministicProviderBoundary,
                    PageSizeBoundary
                ],
            RelationQueryLogicalCapabilityKind.MayBeAbsentBinding =>
                [SingleDatabaseBoundary, CompleteInputEvidenceBoundary],
            RelationQueryLogicalCapabilityKind.OnePerRootRelationOutput
                or RelationQueryLogicalCapabilityKind.ZeroOrOnePerRootRelationOutput
                or RelationQueryLogicalCapabilityKind.ManyPerRootRelationOutput
                or RelationQueryLogicalCapabilityKind.SetRelationOutput
                or RelationQueryLogicalCapabilityKind.RelationOutputIdentity =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    DeterministicProviderBoundary
                ],
            RelationQueryLogicalCapabilityKind.RelationInvariant =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    NonNullOperandsBoundary,
                    DeterministicProviderBoundary
                ],
            _ => [SingleDatabaseBoundary, CompleteInputEvidenceBoundary]
        };

    static IEnumerable<ExprCapabilityId> ExpressionCapabilities()
    {
        yield return ExprCapabilities.Field;
        yield return ExprCapabilities.NestedFieldPath;
        yield return ExprCapabilities.Parameter;
        yield return ExprCapabilities.Constant;
        yield return ExprCapabilities.TypedField;
        yield return ExprCapabilities.TypedLiteral;
        yield return ExprCapabilities.Conditional;
        yield return ExprCapabilities.ForUnary(UnaryOperator.Not);
        foreach (var @operator in SupportedBinaryOperators)
        {
            yield return ExprCapabilities.ForBinary(@operator);
        }

        foreach (var aggregate in SupportedAggregateOperators)
        {
            yield return ExprCapabilities.ForAggregate(aggregate);
        }

        yield return ExprCapabilities.ForFunction(ExprFunctionNames.EndsWith);
        yield return ExprCapabilities.ForFunction(ExprFunctionNames.StartsWith);
        yield return ExprCapabilities.ForFunction(ExprFunctionNames.TextContains);
    }

    static ImmutableArray<RelationQueryOperatingBoundaryId> ExpressionBoundaries(
        ExprCapabilityId expression)
    {
        if (expression == ExprCapabilities.ForAggregate(AggregateOperator.Count))
        {
            return [SingleDatabaseBoundary, CompleteInputEvidenceBoundary];
        }

        if (expression == ExprCapabilities.ForAggregate(AggregateOperator.Sum)
            || expression == ExprCapabilities.ForAggregate(AggregateOperator.Average))
        {
            return
            [
                SingleDatabaseBoundary,
                CompleteInputEvidenceBoundary,
                NonNullOperandsBoundary,
                ScalarOperandsBoundary,
                ExactNumericAggregateDomainBoundary
            ];
        }

        if (expression == ExprCapabilities.ForAggregate(AggregateOperator.Min)
            || expression == ExprCapabilities.ForAggregate(AggregateOperator.Max))
        {
            return
            [
                SingleDatabaseBoundary,
                CompleteInputEvidenceBoundary,
                NonNullOperandsBoundary,
                ScalarOperandsBoundary
            ];
        }
        if (expression == ExprCapabilities.ForBinary(BinaryOperator.Eq)
            || expression == ExprCapabilities.ForBinary(BinaryOperator.Ne))
        {
            return
            [
                SingleDatabaseBoundary,
                CompleteInputEvidenceBoundary,
                ScalarOperandsBoundary,
                DeterministicProviderBoundary
            ];
        }
        if (expression == ExprCapabilities.ForUnary(UnaryOperator.Not)
            || expression == ExprCapabilities.Conditional
            || expression == ExprCapabilities.ForFunction(ExprFunctionNames.EndsWith)
            || expression == ExprCapabilities.ForFunction(ExprFunctionNames.StartsWith)
            || expression == ExprCapabilities.ForFunction(ExprFunctionNames.TextContains)
            || SupportedBinaryOperators.Any(@operator =>
                expression == ExprCapabilities.ForBinary(@operator)))
        {
            return
            [
                SingleDatabaseBoundary,
                CompleteInputEvidenceBoundary,
                NonNullOperandsBoundary,
                ScalarOperandsBoundary,
                DeterministicProviderBoundary
            ];
        }

        return [SingleDatabaseBoundary, CompleteInputEvidenceBoundary];
    }

    static ImmutableArray<RelationQueryOperatingBoundaryId> TemporalBoundaries(
        RelationQueryTemporalExecutionCapability temporal) => temporal switch
        {
            RelationQueryTemporalExecutionCapability.InconclusiveEvidence =>
                [SingleDatabaseBoundary, CompleteInputEvidenceBoundary],
            _ =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    HomogeneousTemporalDomainBoundary,
                    ExactTemporalDomainBoundary
                ]
        };

    static ImmutableArray<RelationQueryOperatingBoundaryId> GuaranteeBoundaries(
        RelationQueryGuaranteeCapabilityKind guarantee) => guarantee switch
        {
            RelationQueryGuaranteeCapabilityKind.MissingNullDistinction =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    NonNullOperandsBoundary
                ],
            RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction
                or RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness
                or RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence =>
                [SingleDatabaseBoundary, CompleteInputEvidenceBoundary],
            RelationQueryGuaranteeCapabilityKind.JoinMembership
                or RelationQueryGuaranteeCapabilityKind.Cardinality
                or RelationQueryGuaranteeCapabilityKind.RelationshipDirection
                or RelationQueryGuaranteeCapabilityKind.RelationshipMultiplicity =>
                [SingleDatabaseBoundary, CompleteInputEvidenceBoundary],
            RelationQueryGuaranteeCapabilityKind.TemporalDomain =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    HomogeneousTemporalDomainBoundary
                ],
            RelationQueryGuaranteeCapabilityKind.TemporalBoundary
                or RelationQueryGuaranteeCapabilityKind.UnboundedTemporalBoundary =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    HomogeneousTemporalDomainBoundary
                ],
            RelationQueryGuaranteeCapabilityKind.Ordering
                or RelationQueryGuaranteeCapabilityKind.NullPlacement =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    ScalarOperandsBoundary,
                    DeterministicProviderBoundary
                ],
            RelationQueryGuaranteeCapabilityKind.StablePaging =>
                [
                    SingleDatabaseBoundary,
                    StableOrderingBoundary,
                    DeterministicProviderBoundary,
                    PageSizeBoundary
                ],
            RelationQueryGuaranteeCapabilityKind.Grouping
                or RelationQueryGuaranteeCapabilityKind.Aggregation =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    ScalarOperandsBoundary
                ],
            RelationQueryGuaranteeCapabilityKind.DuplicateHandling =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    DeterministicProviderBoundary
                ],
            RelationQueryGuaranteeCapabilityKind.DeterministicResult =>
                [SingleDatabaseBoundary, DeterministicProviderBoundary],
            RelationQueryGuaranteeCapabilityKind.ConsistentSnapshot =>
                [SingleDatabaseBoundary, CompleteInputEvidenceBoundary],
            RelationQueryGuaranteeCapabilityKind.OutputIdentity
                or RelationQueryGuaranteeCapabilityKind.OutputMode =>
                [
                    SingleDatabaseBoundary,
                    CompleteInputEvidenceBoundary,
                    DeterministicProviderBoundary
                ],
            RelationQueryGuaranteeCapabilityKind.RelationRootCorrelation =>
                [SingleDatabaseBoundary, CompleteInputEvidenceBoundary, SuppliedRelationRootBoundary],
            _ => [SingleDatabaseBoundary, CompleteInputEvidenceBoundary]
        };

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
        RelationQueryLogicalCapabilityKind.TemporalJoin,
        RelationQueryLogicalCapabilityKind.Projection,
        RelationQueryLogicalCapabilityKind.ProjectionAssignment,
        RelationQueryLogicalCapabilityKind.DistinctRows,
        RelationQueryLogicalCapabilityKind.Aggregation,
        RelationQueryLogicalCapabilityKind.AggregateGrouping,
        RelationQueryLogicalCapabilityKind.AggregateFilter,
        RelationQueryLogicalCapabilityKind.CountAggregate,
        RelationQueryLogicalCapabilityKind.SumAggregate,
        RelationQueryLogicalCapabilityKind.MinimumAggregate,
        RelationQueryLogicalCapabilityKind.MaximumAggregate,
        RelationQueryLogicalCapabilityKind.AverageAggregate,
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

    static ImmutableArray<RelationQueryTemporalExecutionCapability> TemporalCapabilities =>
    [
        RelationQueryTemporalExecutionCapability.PointInInterval,
        RelationQueryTemporalExecutionCapability.InclusiveBoundary,
        RelationQueryTemporalExecutionCapability.ExclusiveBoundary,
        RelationQueryTemporalExecutionCapability.UnboundedBoundary,
        RelationQueryTemporalExecutionCapability.NullAsUnbounded,
        RelationQueryTemporalExecutionCapability.DateDomain,
        RelationQueryTemporalExecutionCapability.DateTimeDomain,
        RelationQueryTemporalExecutionCapability.InstantDomain,
        RelationQueryTemporalExecutionCapability.PreserveAllMatches,
        RelationQueryTemporalExecutionCapability.InnerJoin,
        RelationQueryTemporalExecutionCapability.LeftOuterJoin,
        RelationQueryTemporalExecutionCapability.ValidateIntervals,
        RelationQueryTemporalExecutionCapability.InconclusiveEvidence
    ];

    static ImmutableArray<(
        RelationQueryStructuralCapabilityRole Role,
        RelationQueryStructuralPathKind Path)> StructuralCapabilities =>
    [
        (RelationQueryStructuralCapabilityRole.BindingRead, RelationQueryStructuralPathKind.TopLevelField),
        (RelationQueryStructuralCapabilityRole.BindingRead, RelationQueryStructuralPathKind.NestedField),
        (RelationQueryStructuralCapabilityRole.ProjectionTarget, RelationQueryStructuralPathKind.TopLevelField),
        (RelationQueryStructuralCapabilityRole.ProjectionTarget, RelationQueryStructuralPathKind.NestedField),
        (RelationQueryStructuralCapabilityRole.GroupingTarget, RelationQueryStructuralPathKind.TopLevelField),
        (RelationQueryStructuralCapabilityRole.GroupingTarget, RelationQueryStructuralPathKind.NestedField),
        (RelationQueryStructuralCapabilityRole.AggregateTarget, RelationQueryStructuralPathKind.TopLevelField),
        (RelationQueryStructuralCapabilityRole.AggregateTarget, RelationQueryStructuralPathKind.NestedField),
        (RelationQueryStructuralCapabilityRole.OutputSelection, RelationQueryStructuralPathKind.TopLevelField),
        (RelationQueryStructuralCapabilityRole.OutputSelection, RelationQueryStructuralPathKind.NestedField),
        (RelationQueryStructuralCapabilityRole.CompleteValue, RelationQueryStructuralPathKind.RootValue)
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
        RelationQueryGuaranteeCapabilityKind.DuplicateHandling,
        RelationQueryGuaranteeCapabilityKind.OutputIdentity,
        RelationQueryGuaranteeCapabilityKind.OutputMode,
        RelationQueryGuaranteeCapabilityKind.RelationRootCorrelation,
        RelationQueryGuaranteeCapabilityKind.DeterministicResult,
        RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
        RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence,
        RelationQueryGuaranteeCapabilityKind.ConsistentSnapshot
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

    static ImmutableArray<AggregateOperator> SupportedAggregateOperators =>
    [
        AggregateOperator.Count,
        AggregateOperator.Sum,
        AggregateOperator.Min,
        AggregateOperator.Max,
        AggregateOperator.Average
    ];
}
