using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryRealizationRequirementProjectorTests
{
    [Fact]
    public void Project_EnrichedRelationPreservesRetainedVariantsTerminalsAndProvenance()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);

        var requirements = RelationQueryRealizationRequirementProjector.Project(plan);

        var traversal = requirements
            .Where(requirement => requirement.Origin?.Node == LoadCustomerRelationFixture.CustomerTraversalNodeId)
            .Where(static requirement => requirement.Capability is LogicalRelationQueryCapability)
            .Select(static requirement => ((LogicalRelationQueryCapability)requirement.Capability).Kind)
            .OrderBy(static kind => (int)kind)
            .ToArray();
        Assert.Equal(
            [
                RelationQueryLogicalCapabilityKind.RelationshipTraversal,
                RelationQueryLogicalCapabilityKind.ForwardRelationshipTraversal,
                RelationQueryLogicalCapabilityKind.AtMostOneRelationshipTraversal,
                RelationQueryLogicalCapabilityKind.RequiredRelationshipTraversal,
                RelationQueryLogicalCapabilityKind.LeftOuterJoin
            ],
            traversal);

        var traversalInput = Assert.Single(plan.InputContract.Traversals).Input.Id;
        Assert.All(
            requirements.Where(requirement =>
                requirement.Origin?.Node == LoadCustomerRelationFixture.CustomerTraversalNodeId
                && requirement.Capability is LogicalRelationQueryCapability),
            requirement =>
            {
                Assert.Equal(traversalInput, requirement.Origin!.Input);
                Assert.NotEmpty(requirement.Uses);
                Assert.All(
                    requirement.Uses.SelectMany(static use => use.Traces),
                    static trace => Assert.Equal(
                        RelationQueryRealizationTraceStepKind.Terminal,
                        trace.Steps[0].Kind));
            });

        AssertLogical(requirements, RelationQueryLogicalCapabilityKind.Source);
        AssertLogical(requirements, RelationQueryLogicalCapabilityKind.Projection);
        Assert.Equal(
            2,
            requirements.Count(static requirement =>
                requirement.Capability is LogicalRelationQueryCapability
                {
                    Kind: RelationQueryLogicalCapabilityKind.ProjectionAssignment
                }));
        AssertLogical(requirements, RelationQueryLogicalCapabilityKind.OnePerRootRelationOutput);
        AssertLogical(requirements, RelationQueryLogicalCapabilityKind.RelationOutputIdentity);

        var guarantees = GuaranteeKinds(requirements);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.MissingNullDistinction, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.JoinMembership, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.Cardinality, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.RelationshipDirection, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.RelationshipMultiplicity, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.OutputMode, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.OutputIdentity, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.DeterministicResult, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness, guarantees);
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence, guarantees);
        Assert.DoesNotContain(RelationQueryGuaranteeCapabilityKind.ConsistentSnapshot, guarantees);

        RelationQueryGuaranteeCapabilityKind[] baselineGuarantees =
        [
            RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
            RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
            RelationQueryGuaranteeCapabilityKind.DeterministicResult,
            RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance,
            RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
            RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence
        ];
        Assert.All(
            requirements.Where(static requirement => requirement.Capability is not GuaranteeRelationQueryCapability),
            requirement => Assert.True(
                baselineGuarantees.All(requirement.RequiredGuarantees.Contains),
                $"Requirement '{requirement.Id.Value}' does not preserve every plan-wide guarantee."));
        Assert.All(
            requirements.Where(static requirement => requirement.Capability is GuaranteeRelationQueryCapability),
            static requirement =>
            {
                Assert.StartsWith("plan/guarantee/", requirement.Origin?.SemanticSite, StringComparison.Ordinal);
                Assert.NotEmpty(requirement.Uses);
                Assert.All(
                    requirement.Uses.SelectMany(static use => use.Traces),
                    static trace => Assert.Equal(
                        RelationQueryRealizationTraceStepKind.Terminal,
                        Assert.Single(trace.Steps).Kind));
            });
    }

    [Fact]
    public void Project_ExplicitJoinAndRepresentativeQueryPreserveEveryLogicalVariant()
    {
        var explicitJoin = Compile(LoadCustomerRelationFixture.ExplicitJoinQueryDocument);

        var explicitJoinRequirements = RelationQueryRealizationRequirementProjector.Project(explicitJoin);

        AssertLogicalAtNode(
            explicitJoinRequirements,
            LoadCustomerRelationFixture.ExplicitJoinNodeId,
            RelationQueryLogicalCapabilityKind.Join);
        AssertLogicalAtNode(
            explicitJoinRequirements,
            LoadCustomerRelationFixture.ExplicitJoinNodeId,
            RelationQueryLogicalCapabilityKind.InnerJoin);
        AssertLogical(explicitJoinRequirements, RelationQueryLogicalCapabilityKind.QueryRowsResult);

        var query = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);

        var queryRequirements = RelationQueryRealizationRequirementProjector.Project(query);

        foreach (var kind in new[]
                 {
                     RelationQueryLogicalCapabilityKind.Filter,
                     RelationQueryLogicalCapabilityKind.OptionalRelationshipTraversal,
                     RelationQueryLogicalCapabilityKind.Aggregation,
                     RelationQueryLogicalCapabilityKind.AggregateGrouping,
                     RelationQueryLogicalCapabilityKind.AggregateFilter,
                     RelationQueryLogicalCapabilityKind.CountAggregate,
                     RelationQueryLogicalCapabilityKind.SumAggregate,
                     RelationQueryLogicalCapabilityKind.Ordering,
                     RelationQueryLogicalCapabilityKind.AscendingOrdering,
                     RelationQueryLogicalCapabilityKind.NullsLast,
                     RelationQueryLogicalCapabilityKind.StableTieOrdering,
                     RelationQueryLogicalCapabilityKind.KeysetPaging,
                     RelationQueryLogicalCapabilityKind.QueryRowsResult,
                     RelationQueryLogicalCapabilityKind.QueryAggregationResult
                 })
        {
            AssertLogical(queryRequirements, kind);
        }
    }

    [Fact]
    public void Project_IdOnlyDemandDoesNotReintroduceBypassedTraversal()
    {
        var plan = Compile(
            LoadCustomerRelationFixture.OptionalTraversalRelationDocument,
            RelationFields(LoadCustomerRelationFixture.SearchIdPath));
        Assert.DoesNotContain(
            LoadCustomerRelationFixture.CustomerTraversalNodeId,
            plan.LogicalPlan.RetainedNodes);

        var requirements = RelationQueryRealizationRequirementProjector.Project(plan);

        Assert.DoesNotContain(
            requirements,
            requirement => requirement.Origin?.Node == LoadCustomerRelationFixture.CustomerTraversalNodeId);
        Assert.DoesNotContain(
            LogicalKinds(requirements),
            kind => kind is RelationQueryLogicalCapabilityKind.RelationshipTraversal
                or RelationQueryLogicalCapabilityKind.ForwardRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.InverseRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.AtMostOneRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.ManyRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.RequiredRelationshipTraversal
                or RelationQueryLogicalCapabilityKind.OptionalRelationshipTraversal);
        var assignment = Assert.Single(
            requirements,
            static requirement => requirement.Capability is LogicalRelationQueryCapability
            {
                Kind: RelationQueryLogicalCapabilityKind.ProjectionAssignment
            });
        Assert.Equal(LoadCustomerRelationFixture.SearchIdPath, assignment.Origin?.FieldPath);
    }

    [Fact]
    public void Project_ExpressionCapabilitiesRemainDistinctAtEachExpressionPath()
    {
        var plan = Compile(LoadCustomerRelationFixture.ExplicitJoinQueryDocument);
        var joinSite = Assert.Single(
            plan.ExecutionSlice.ExpressionSites,
            static site => site.Kind == RelationQueryExpressionSiteKind.JoinPredicate);

        var requirements = RelationQueryRealizationRequirementProjector.Project(plan);

        var fieldOperations = requirements
            .Where(requirement =>
                requirement.Capability is ExpressionRelationQueryCapability
                {
                    Capability: var capability,
                    RequirementKind: ExprCapabilityRequirementKind.Operation
                }
                && capability == ExprCapabilities.Field
                && requirement.Origin?.SemanticSite == joinSite.Analysis.Site.Id.Value)
            .ToArray();
        Assert.Equal(2, fieldOperations.Length);
        Assert.Equal(2, fieldOperations.Select(static requirement => requirement.Id).Distinct().Count());
        Assert.Equal(2, fieldOperations.Select(static requirement => requirement.Origin!.ExpressionPath).Distinct().Count());
        Assert.All(fieldOperations, static requirement =>
        {
            Assert.NotNull(requirement.Origin!.Input);
            Assert.NotEmpty(requirement.Uses);
            Assert.All(
                requirement.Uses.SelectMany(static use => use.Traces),
                trace => Assert.Contains(
                    trace.Steps,
                    static step => step.Kind == RelationQueryRealizationTraceStepKind.ExpressionSite
                        && step.SiteKind == RelationQueryExpressionSiteKind.JoinPredicate));
        });
    }

    [Fact]
    public void Project_DerivedBindingReadsRemainDistinctWithoutExternalInputIds()
    {
        var (document, join, left, right) = CreateDerivedBindingJoinDocument();
        var plan = Compile(document);
        var joinSite = Assert.Single(
            plan.ExecutionSlice.ExpressionSites,
            site => site.Node == join && site.Kind == RelationQueryExpressionSiteKind.JoinPredicate);

        var requirements = RelationQueryRealizationRequirementProjector.Project(plan);

        var reads = requirements
            .Where(requirement => requirement.Origin?.SemanticSite == joinSite.Analysis.Site.Id.Value)
            .Where(requirement => requirement.Origin?.FieldPath == LoadCustomerRelationFixture.SearchIdPath)
            .Where(static requirement => requirement.Capability is StructuralRelationQueryCapability
            {
                Role: RelationQueryStructuralCapabilityRole.BindingRead,
                PathKind: RelationQueryStructuralPathKind.TopLevelField
            })
            .ToArray();
        Assert.Equal(2, reads.Length);
        Assert.Equal(
            [left, right],
            reads.Select(static requirement => requirement.Origin!.Binding)
                .OfType<ValueBindingId>()
                .OrderBy(static binding => binding.Value, StringComparer.Ordinal));
        Assert.All(reads, static requirement => Assert.Null(requirement.Origin!.Input));
        Assert.Equal(2, reads.Select(static requirement => requirement.Id).Distinct().Count());
    }

    [Fact]
    public void Project_TemporalCapabilitiesRetainExactInputAndSemanticSiteAttribution()
    {
        var plan = CompileTemporal(TemporalRelationQueryFixture.CreateQueryDocument(
            TemporalRelationQueryFixture.CreateOverlapMatch(),
            JoinKind.Left));
        var contract = Assert.Single(
            plan.InputContract.TemporalCapabilities,
            static capability => capability.Capability
                == RelationQueryTemporalExecutionCapability.IntervalOverlap);

        var requirements = RelationQueryRealizationRequirementProjector.Project(plan);

        var projected = Assert.Single(
            requirements,
            static requirement => requirement.Capability is TemporalRelationQueryCapability
            {
                Capability: RelationQueryTemporalExecutionCapability.IntervalOverlap
            });
        Assert.Equal(contract.Id, projected.Origin?.Input);
        Assert.Equal(contract.Node, projected.Origin?.Node);
        Assert.Equal(contract.SemanticSite, projected.Origin?.SemanticSite);
        Assert.EndsWith("/temporalJoin/intervalOverlap", contract.SemanticSite, StringComparison.Ordinal);
        Assert.NotEmpty(projected.Uses);
        Assert.All(
            projected.Uses.SelectMany(static use => use.Traces),
            trace =>
            {
                Assert.Equal(RelationQueryRealizationTraceStepKind.Terminal, trace.Steps[0].Kind);
                Assert.Contains(trace.Steps, step => step.Node == TemporalRelationQueryFixture.TemporalJoin);
            });

        AssertLogicalAtNode(
            requirements,
            TemporalRelationQueryFixture.TemporalJoin,
            RelationQueryLogicalCapabilityKind.TemporalJoin);
        Assert.DoesNotContain(
            requirements,
            requirement => requirement.Origin?.Node == TemporalRelationQueryFixture.TemporalJoin
                && requirement.Capability is LogicalRelationQueryCapability
                {
                    Kind: RelationQueryLogicalCapabilityKind.LeftOuterJoin
                });
        Assert.Contains(
            requirements,
            static requirement => requirement.Capability is TemporalRelationQueryCapability
            {
                Capability: RelationQueryTemporalExecutionCapability.LeftOuterJoin
            });
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.TemporalDomain, GuaranteeKinds(requirements));
        Assert.Contains(RelationQueryGuaranteeCapabilityKind.TemporalBoundary, GuaranteeKinds(requirements));
    }

    [Fact]
    public void Project_StructuralRequirementsDistinguishElementAndOutputRoles()
    {
        var itemPath = new FieldPath(
        [
            FieldPathSegment.ForField(ExprFieldRoots.CurrentItem),
            FieldPathSegment.Element()
        ]);
        var plan = Compile(CreateElementPathQueryDocument(itemPath));

        var requirements = RelationQueryRealizationRequirementProjector.Project(plan);

        var currentItem = Assert.Single(
            requirements,
            requirement => requirement.Origin?.FieldPath == itemPath
                && requirement.Capability is StructuralRelationQueryCapability
                {
                    Role: RelationQueryStructuralCapabilityRole.CurrentItemRead,
                    PathKind: RelationQueryStructuralPathKind.CollectionElement
                });
        Assert.NotEmpty(currentItem.Uses);
        Assert.Equal(
            1,
            StaticFact(currentItem, RelationQueryRealizationStaticFactKind.FieldPathDepth));

        var relationRequirements = RelationQueryRealizationRequirementProjector.Project(
            Compile(LoadCustomerRelationFixture.BaselineRelationDocument));
        foreach (var role in new[]
                 {
                     RelationQueryStructuralCapabilityRole.BindingRead,
                     RelationQueryStructuralCapabilityRole.OccurrenceEvidenceReconstruction,
                     RelationQueryStructuralCapabilityRole.ProjectionTarget,
                     RelationQueryStructuralCapabilityRole.OutputSelection,
                     RelationQueryStructuralCapabilityRole.CompleteValue
                 })
        {
            Assert.Contains(
                relationRequirements,
                requirement => requirement.Capability is StructuralRelationQueryCapability structural
                    && structural.Role == role);
        }
    }

    [Fact]
    public void Project_GlobalAggregatesDoNotInventGroupingGuarantees()
    {
        var fixture = CreateTwoGlobalCountAggregatesQueryDocument();
        var plan = Compile(fixture.Document, AggregateFields(fixture.FirstResult, fixture.SecondResult));

        var requirements = RelationQueryRealizationRequirementProjector.Project(plan);
        var guarantees = GuaranteeKinds(requirements);

        Assert.Contains(RelationQueryGuaranteeCapabilityKind.Aggregation, guarantees);
        Assert.DoesNotContain(RelationQueryGuaranteeCapabilityKind.Grouping, guarantees);
        Assert.All(
            requirements.Where(static requirement => requirement.Capability is LogicalRelationQueryCapability
            {
                Kind: RelationQueryLogicalCapabilityKind.Aggregation
                    or RelationQueryLogicalCapabilityKind.CountAggregate
            }),
            static requirement =>
            {
                Assert.Contains(
                    RelationQueryGuaranteeCapabilityKind.Aggregation,
                    requirement.RequiredGuarantees);
                Assert.DoesNotContain(
                    RelationQueryGuaranteeCapabilityKind.Grouping,
                    requirement.RequiredGuarantees);
            });
    }

    [Fact]
    public void Project_AggregateOperationAndTargetUsesRemainScopedToTheirExactAssignment()
    {
        var (document, firstAggregate, secondAggregate, firstResult, secondResult) =
            CreateTwoGlobalCountAggregatesQueryDocument();
        var requirements = RelationQueryRealizationRequirementProjector.Project(
            Compile(document, AggregateFields(firstResult, secondResult)));

        AssertAggregateScope(firstAggregate, firstResult);
        AssertAggregateScope(secondAggregate, secondResult);

        void AssertAggregateScope(QueryNodeId node, QueryResultId expectedResult)
        {
            var operation = Assert.Single(
                requirements,
                requirement => requirement.Origin?.Node == node
                    && requirement.Capability is LogicalRelationQueryCapability
                    {
                        Kind: RelationQueryLogicalCapabilityKind.CountAggregate
                    });
            Assert.Equal(
                [expectedResult],
                operation.Uses.Select(static use => use.Output.QueryResult)
                    .OfType<QueryResultId>()
                    .Distinct()
                    .ToArray());

            var target = Assert.Single(
                requirements,
                requirement => requirement.Origin?.Node == node
                    && requirement.Capability is StructuralRelationQueryCapability
                    {
                        Role: RelationQueryStructuralCapabilityRole.AggregateTarget
                    });
            Assert.Null(target.Origin!.Input);
            Assert.Equal(
                [expectedResult],
                target.Uses.Select(static use => use.Output.QueryResult)
                    .OfType<QueryResultId>()
                    .Distinct()
                    .ToArray());
        }
    }

    [Fact]
    public void Project_ConstantTemporalOperandsRetainTheirExactExpressionSiteTraces()
    {
        var point = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var instant = new ScalarTypeRef(ScalarTypeKind.Instant);
        static ObservationValue InstantValue(DateTimeOffset value) =>
            ObservationValue.FromString(value.ToString("O", CultureInfo.InvariantCulture));
        var match = new TemporalPointInIntervalMatch(
            new LiteralExpr(instant, InstantValue(point)),
            new TemporalInterval(
                new ExpressionTemporalIntervalBound(
                    new LiteralExpr(instant, InstantValue(point.AddDays(-1))),
                    TemporalBoundaryInclusion.Inclusive),
                new ExpressionTemporalIntervalBound(
                    new LiteralExpr(instant, InstantValue(point.AddDays(1))),
                    TemporalBoundaryInclusion.Exclusive)));
        var plan = CompileTemporal(TemporalRelationQueryFixture.CreateQueryDocument(match));
        var temporal = Assert.Single(
            plan.ExecutionSlice.Nodes,
            static node => node.Id == TemporalRelationQueryFixture.TemporalJoin).TemporalJoin!;
        var expectedSites = new[]
        {
            temporal.PointSite!,
            temporal.Intervals[0].Lower.ValueSite!,
            temporal.Intervals[0].Upper.ValueSite!
        }.Select(static site => site.Analysis.Site.Id).ToArray();

        var requirement = Assert.Single(
            RelationQueryRealizationRequirementProjector.Project(plan),
            static requirement => requirement.Capability is TemporalRelationQueryCapability
            {
                Capability: RelationQueryTemporalExecutionCapability.PointInInterval
            });
        var tracedSites = requirement.Uses
            .SelectMany(static use => use.Traces)
            .SelectMany(static trace => trace.Steps)
            .Where(static step => step.Kind == RelationQueryRealizationTraceStepKind.ExpressionSite)
            .Select(static step => step.ExpressionSite)
            .OfType<ExprSiteId>()
            .ToHashSet();

        Assert.All(expectedSites, site => Assert.Contains(site, tracedSites));
    }

    [Fact]
    public void Project_JoinStrategiesCarryTheirSemanticGuarantees()
    {
        var requirements = RelationQueryRealizationRequirementProjector.Project(
            Compile(LoadCustomerRelationFixture.ExplicitJoinQueryDocument));

        foreach (var kind in new[]
                 {
                     RelationQueryLogicalCapabilityKind.Join,
                     RelationQueryLogicalCapabilityKind.InnerJoin
                 })
        {
            var requirement = Assert.Single(
                requirements,
                requirement => requirement.Origin?.Node == LoadCustomerRelationFixture.ExplicitJoinNodeId
                    && requirement.Capability is LogicalRelationQueryCapability logical
                    && logical.Kind == kind);
            Assert.Equal(
                [
                    RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                    RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
                    RelationQueryGuaranteeCapabilityKind.JoinMembership,
                    RelationQueryGuaranteeCapabilityKind.Cardinality,
                    RelationQueryGuaranteeCapabilityKind.DeterministicResult,
                    RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance,
                    RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
                    RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence
                ],
                requirement.RequiredGuarantees.ToArray());
        }
    }

    [Fact]
    public void Project_BindingAvailabilityDistinguishesPreAndPostOuterJoinScopes()
    {
        var plan = Compile(LoadCustomerRelationFixture.OptionalTraversalRelationDocument);
        var requirements = RelationQueryRealizationRequirementProjector.Project(plan);

        Assert.Contains(
            requirements,
            requirement => requirement.Origin?.Binding == LoadCustomerRelationFixture.LoadBinding
                && requirement.Capability is LogicalRelationQueryCapability
                {
                    Kind: RelationQueryLogicalCapabilityKind.AlwaysPresentBinding
                });
        var optionalCustomers = requirements.Where(
                requirement => requirement.Origin?.Binding == LoadCustomerRelationFixture.CustomerBinding
                    && requirement.Capability is LogicalRelationQueryCapability
                    {
                        Kind: RelationQueryLogicalCapabilityKind.MayBeAbsentBinding
                    }
                    && requirement.Origin.SemanticSite?.Contains("/project/", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(optionalCustomers);
        Assert.All(
            optionalCustomers,
            static optionalCustomer => Assert.Equal(
                [
                    RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
                    RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
                    RelationQueryGuaranteeCapabilityKind.DeterministicResult,
                    RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance,
                    RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
                    RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence
                ],
                optionalCustomer.RequiredGuarantees.ToArray()));
    }

    [Fact]
    public void Project_PopulatesExpressionAndPageStaticFacts()
    {
        var requirements = RelationQueryRealizationRequirementProjector.Project(
            Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument));

        var filter = Assert.Single(
            requirements,
            static requirement => requirement.Capability is LogicalRelationQueryCapability
            {
                Kind: RelationQueryLogicalCapabilityKind.Filter
            });
        Assert.Equal(
            2,
            StaticFact(filter, RelationQueryRealizationStaticFactKind.ExpressionDepth));

        var page = Assert.Single(
            requirements,
            static requirement => requirement.Capability is LogicalRelationQueryCapability
            {
                Kind: RelationQueryLogicalCapabilityKind.KeysetPaging
            });
        Assert.Equal(25, StaticFact(page, RelationQueryRealizationStaticFactKind.PageSize));
        Assert.Equal(1, StaticFact(page, RelationQueryRealizationStaticFactKind.ExpressionDepth));
    }

    [Fact]
    public void Project_IsOrdinalAndCultureInvariant()
    {
        var plan = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var first = RelationQueryRealizationRequirementProjector.Project(plan);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = RelationQueryRealizationRequirementProjector.Project(plan);

            Assert.Equal(first.Select(RequirementSignature), second.Select(RequirementSignature));
            Assert.Equal(
                first.Select(static requirement => requirement.Id.Value)
                    .OrderBy(static value => value, StringComparer.Ordinal),
                first.Select(static requirement => requirement.Id.Value));
            Assert.Equal(first.Length, first.Select(static requirement => requirement.Id).Distinct().Count());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    static void AssertLogical(
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        RelationQueryLogicalCapabilityKind kind) =>
        Assert.Contains(
            requirements,
            requirement => requirement.Capability is LogicalRelationQueryCapability logical
                && logical.Kind == kind);

    static void AssertLogicalAtNode(
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        QueryNodeId node,
        RelationQueryLogicalCapabilityKind kind) =>
        Assert.Contains(
            requirements,
            requirement => requirement.Origin?.Node == node
                && requirement.Capability is LogicalRelationQueryCapability logical
                && logical.Kind == kind);

    static ImmutableArray<RelationQueryLogicalCapabilityKind> LogicalKinds(
        ImmutableArray<RelationQueryRealizationRequirement> requirements) =>
    [
        .. requirements.Select(static requirement => requirement.Capability)
            .OfType<LogicalRelationQueryCapability>()
            .Select(static capability => capability.Kind)
    ];

    static ImmutableArray<RelationQueryGuaranteeCapabilityKind> GuaranteeKinds(
        ImmutableArray<RelationQueryRealizationRequirement> requirements) =>
    [
        .. requirements.Select(static requirement => requirement.Capability)
            .OfType<GuaranteeRelationQueryCapability>()
            .Select(static capability => capability.Kind)
    ];

    static long StaticFact(
        RelationQueryRealizationRequirement requirement,
        RelationQueryRealizationStaticFactKind kind) =>
        Assert.Single(requirement.StaticFacts, fact => fact.Kind == kind).Value;

    static CompiledRelationQueryPlan Compile(
        RelationQueryDocument document,
        RelationQueryCompilationDemand? demand = null)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument,
            demand));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static CompiledRelationQueryPlan CompileTemporal(RelationQueryDocument document)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            [TemporalRelationQueryFixture.CreateShapeGraphDocument()]));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static RelationQueryCompilationDemand RelationFields(params FieldPath[] paths) =>
        RelationQueryCompilationDemand.ForRelationFields(paths.Select(path =>
            new RelationQueryFieldReference(LoadCustomerRelationFixture.LoadSearchShapeId, path)));

    static RelationQueryCompilationDemand AggregateFields(params QueryResultId[] results) =>
        RelationQueryCompilationDemand.ForQueryResults(results.Select(result =>
            QueryResultDemand.SelectedFields(
                result,
                [
                    new(
                        LoadCustomerRelationFixture.LoadAggregateShapeId,
                        LoadCustomerRelationFixture.AggregateLoadCountPath)
                ])));

    static RelationQueryDocument CreateElementPathQueryDocument(FieldPath itemPath)
    {
        var items = new QueryParameterId("items");
        var source = new QueryNodeId("element-path-source");
        var filter = new QueryNodeId("element-path-filter");
        var project = new QueryNodeId("element-path-project");
        var selected = Expr.Call(
            ExprFunctionNames.Select,
            Expr.Param(items.Value),
            Expr.Field(itemPath));
        var definition = new QueryDefinition(
            new("element-path-requirements"),
            new("ElementPathRequirements"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(
                        source,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new FilterQueryNode(
                        filter,
                        source,
                        Expr.Eq(
                            Expr.Call(ExprFunctionNames.Count, selected),
                            Expr.Const(1))),
                    new ProjectQueryNode(
                        project,
                        filter,
                        LoadCustomerRelationFixture.SearchBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new(
                                new("assign-element-path-id"),
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Field(
                                    LoadCustomerRelationFixture.LoadBinding,
                                    LoadCustomerRelationFixture.LoadIdPath))
                        ])
                ],
                parameters:
                [
                    new(
                        items,
                        new ArrayTypeRef(
                            new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.String))))
                ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static (RelationQueryDocument Document, QueryNodeId Join, ValueBindingId Left, ValueBindingId Right)
        CreateDerivedBindingJoinDocument()
    {
        var leftSource = new QueryNodeId("derived-left-source");
        var rightSource = new QueryNodeId("derived-right-source");
        var leftProject = new QueryNodeId("derived-left-project");
        var rightProject = new QueryNodeId("derived-right-project");
        var join = new QueryNodeId("derived-binding-join");
        var output = new QueryNodeId("derived-binding-output");
        var leftSourceBinding = new ValueBindingId("left-source-load");
        var rightSourceBinding = new ValueBindingId("right-source-load");
        var left = new ValueBindingId("left-derived-dto");
        var right = new ValueBindingId("right-derived-dto");
        var definition = new QueryDefinition(
            new("derived-binding-query"),
            new("DerivedBindingQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(leftSource, leftSourceBinding, LoadCustomerRelationFixture.LoadShapeId),
                new SourceQueryNode(rightSource, rightSourceBinding, LoadCustomerRelationFixture.LoadShapeId),
                new ProjectQueryNode(
                    leftProject,
                    leftSource,
                    left,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("left-id"),
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(leftSourceBinding, LoadCustomerRelationFixture.LoadIdPath))
                    ]),
                new ProjectQueryNode(
                    rightProject,
                    rightSource,
                    right,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("right-id"),
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(rightSourceBinding, LoadCustomerRelationFixture.LoadIdPath))
                    ]),
                new JoinQueryNode(
                    join,
                    leftProject,
                    rightProject,
                    JoinKind.Inner,
                    Expr.Eq(
                        Expr.Field(left, LoadCustomerRelationFixture.SearchIdPath),
                        Expr.Field(right, LoadCustomerRelationFixture.SearchIdPath))),
                new ProjectQueryNode(
                    output,
                    join,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("output-id"),
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Const("row"))
                    ])
            ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, output)]);
        return (RelationQueryDocument.FromDefinition(definition), join, left, right);
    }

    static (
        RelationQueryDocument Document,
        QueryNodeId FirstAggregate,
        QueryNodeId SecondAggregate,
        QueryResultId FirstResult,
        QueryResultId SecondResult) CreateTwoGlobalCountAggregatesQueryDocument()
    {
        var firstAggregate = new QueryNodeId("first-global-count");
        var secondAggregate = new QueryNodeId("second-global-count");
        var firstResult = new QueryResultId("first-count-result");
        var secondResult = new QueryResultId("second-count-result");
        var definition = new QueryDefinition(
            new("two-global-counts"),
            new("TwoGlobalCounts"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new AggregateQueryNode(
                    firstAggregate,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    new("first-count-binding"),
                    LoadCustomerRelationFixture.LoadAggregateShapeId,
                    aggregates:
                    [
                        new(
                            new("first-count-assignment"),
                            LoadCustomerRelationFixture.AggregateLoadCountPath,
                            AggregateOperator.Count)
                    ]),
                new AggregateQueryNode(
                    secondAggregate,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    new("second-count-binding"),
                    LoadCustomerRelationFixture.LoadAggregateShapeId,
                    aggregates:
                    [
                        new(
                            new("second-count-assignment"),
                            LoadCustomerRelationFixture.AggregateLoadCountPath,
                            AggregateOperator.Count)
                    ])
            ]),
            [
                new AggregationQueryResultDefinition(firstResult, firstAggregate),
                new AggregationQueryResultDefinition(secondResult, secondAggregate)
            ]);
        return (
            RelationQueryDocument.FromDefinition(definition),
            firstAggregate,
            secondAggregate,
            firstResult,
            secondResult);
    }

    static string RequirementSignature(RelationQueryRealizationRequirement requirement)
    {
        var capability = requirement.Capability switch
        {
            LogicalRelationQueryCapability logical => $"logical:{(int)logical.Kind}",
            ExpressionRelationQueryCapability expression =>
                $"expression:{expression.Capability.Value}:{(int)expression.RequirementKind}",
            TemporalRelationQueryCapability temporal => $"temporal:{(int)temporal.Capability}",
            StructuralRelationQueryCapability structural =>
                $"structural:{(int)structural.Role}:{(int)structural.PathKind}",
            GuaranteeRelationQueryCapability guarantee => $"guarantee:{(int)guarantee.Kind}",
            PrimitiveRelationQueryCapability primitive => $"primitive:{(int)primitive.Kind}",
            _ => throw new InvalidOperationException(
                $"Unsupported requirement capability '{requirement.Capability.GetType().Name}'.")
        };
        var origin = requirement.Origin is null
            ? "-"
            : string.Join(
                ":",
                requirement.Origin.Input?.Value ?? string.Empty,
                requirement.Origin.Node?.Value ?? string.Empty,
                requirement.Origin.SemanticSite ?? string.Empty,
                requirement.Origin.ExpressionPath ?? string.Empty,
                PathSignature(requirement.Origin.FieldPath),
                requirement.Origin.Binding?.Value ?? string.Empty);
        var uses = string.Join(
            "|",
            requirement.Uses.Select(use => string.Join(
                ":",
                use.Output.Id.Value,
                ((int)use.Effect).ToString(CultureInfo.InvariantCulture),
                ((int)use.Requirement).ToString(CultureInfo.InvariantCulture),
                string.Join(
                    ",",
                    use.Traces.Select(trace => string.Join(
                        ">",
                        trace.Steps.Select(step => string.Join(
                            "/",
                            ((int)step.Kind).ToString(CultureInfo.InvariantCulture),
                            step.Node.Value,
                            step.SiteKind is { } siteKind
                                ? ((int)siteKind).ToString(CultureInfo.InvariantCulture)
                                : string.Empty,
                            step.ExpressionSite?.Value ?? string.Empty,
                            step.Assignment?.Value ?? string.Empty,
                            step.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                            step.InvariantName ?? string.Empty))))))));
        var guarantees = string.Join(
            ",",
            requirement.RequiredGuarantees.Select(static guarantee =>
                ((int)guarantee).ToString(CultureInfo.InvariantCulture)));
        var staticFacts = string.Join(
            ",",
            requirement.StaticFacts.Select(static fact =>
                $"{((int)fact.Kind).ToString(CultureInfo.InvariantCulture)}:"
                + fact.Value.ToString(CultureInfo.InvariantCulture)));
        return string.Join("#", requirement.Id.Value, capability, origin, uses, guarantees, staticFacts);
    }

    static string PathSignature(FieldPath? path) => path is null
        ? string.Empty
        : string.Join(
            "/",
            path.Value.Segments.Select(static segment => string.Join(
                ":",
                ((int)segment.Kind).ToString(CultureInfo.InvariantCulture),
                segment.Segment ?? string.Empty)));
}
