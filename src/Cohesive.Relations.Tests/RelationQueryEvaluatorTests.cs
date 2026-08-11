using System.Collections.Immutable;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryEvaluatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9_007_199_254_740_992)]
    public void CreateSuppliedOnly_rejects_nonportable_root_bounds(long maximumRootRows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RelationQueryEvaluator.CreateSuppliedOnly(maximumRootRows));
    }

    [Fact]
    public async Task CreateSuppliedOnly_rejects_relations_that_require_external_traversal()
    {
        var evaluation = RelationEvaluation("tests/supplied-only/traversal", "customer-1");
        var evaluator = RelationQueryEvaluator.CreateSuppliedOnly();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.EvaluateAsync(evaluation).AsTask());

        Assert.Contains("no relationship traversals", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluation_builder_maps_typed_roots_and_preserves_empty_root_evidence()
    {
        var evaluation = LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new("tests/typed-roots"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument
                )
            .Supply(
                [new LoadRoot("load-1", "customer-1")],
                static load => load.Id,
                evidenceReference: "tests/load-snapshot"
                )
            .Build();

        Assert.IsType<RelationDefinition>(evaluation.Definition);
        Assert.Equal(
            LoadCustomerRelationFixture.ShapeGraphDocuments.Select(static document => document.Graph.Id),
            evaluation.Compilation.ShapeDocuments.Select(static document => document.Graph.Id));
        Assert.Same(
            LoadCustomerRelationFixture.RelationshipCatalogDocument,
            evaluation.Compilation.RelationshipCatalogDocument);
        var supplied = Assert.IsType<RelationQuerySuppliedRootSet>(evaluation.SuppliedRoots);
        var observation = Assert.Single(supplied.Observations);
        Assert.Equal(LoadCustomerRelationFixture.LoadShapeLocalId, observation.ShapeId);
        Assert.Equal("load-1", observation.Id);
        Assert.Equal(ObservationValue.FromString("customer-1"), observation.Fields["CustomerId"]);
        Assert.Equal(RelationQueryEvidenceCompleteness.Complete, supplied.Completeness);
        Assert.Equal("tests/load-snapshot", supplied.EvidenceReference);
        Assert.Equal(
            "input/source-set/loads",
            RelationQueryInputIds.ForSource(LoadCustomerRelationFixture.LoadSourceNodeId).Value);

        var empty = LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new("tests/empty-roots"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Supply([])
            .Build();
        Assert.NotNull(empty.SuppliedRoots);
        Assert.Empty(empty.SuppliedRoots.Observations);

        var omitted = LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new("tests/omitted-roots"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Build();
        Assert.Null(omitted.SuppliedRoots);
    }

    [Fact]
    public async Task CreateSuppliedOnly_accepts_nested_single_value_under_its_compiled_decimal_contract()
    {
        var author = RelationQuery.Expression();
        var inputs = author.Source<FloatScoreInput>();
        var outputs = author.Project(
            inputs,
            (FloatScoreInput input) => new FloatScoreOutput
            {
                Id = input.Id,
                Score = input.Signals.Score
            });
        var relation = outputs.BuildRelation(static output => output.Id);
        var evaluation = author.Evaluate(relation, new("tests/supplied-single-value"))
            .Supply(
                [new FloatScoreInput
                {
                    Id = "score-1",
                    Signals = new() { Score = 0.98f }
                }],
                static input => input.Id)
            .Build();

        var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);

        Assert.True(
            outcome.IsSuccessful,
            string.Join(Environment.NewLine, outcome.PhysicalExecution?.Diagnostics.Select(static diagnostic => diagnostic.Message) ?? []));
        var row = Assert.Single(Assert.IsType<RelationQueryExecutionResult>(outcome.Result).Relation!.Rows);
        Assert.Equal(0.98m, row.Value.GetProperty(nameof(FloatScoreOutput.Score)).GetDecimal());
    }

    [Fact]
    public async Task EvaluateAsync_RelationHydratesSuppliedLoadThroughCustomerSource()
    {
        var evaluation = RelationEvaluation("tests/relation-success", "customer-1");
        ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> customers =
        [
            Customer("customer-1", "Acme", "Priority")
        ];
        var evaluator = CreateEvaluator(evaluation, customerRows: customers);

        var outcome = await evaluator.EvaluateAsync(evaluation);

        Assert.True(outcome.IsSuccessful);
        Assert.NotNull(outcome.Compilation.Plan);
        Assert.True(outcome.Realization?.IsRealizable);
        Assert.True(outcome.PhysicalPlanning?.IsSuccessful);
        var result = Assert.IsType<RelationQueryExecutionResult>(outcome.Result);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        var row = Assert.Single(relation.Rows);
        Assert.Equal("load-1", row.Value.GetProperty(LoadCustomerRelationFixture.SearchIdFieldName).String);
        Assert.Equal(
            "Acme",
            row.Value.GetProperty(LoadCustomerRelationFixture.SearchCustomerNameFieldName).String);
        Assert.Equal("load-1", row.Root?.ObservationIdentity);
        Assert.Empty(result.RequirementGapAnalysis.Gaps);
    }

    [Fact]
    public async Task EvaluateAsync_MissingCustomerProducesAttributableRequirementGap()
    {
        var evaluation = RelationEvaluation("tests/relation-gap", "customer-missing");
        var evaluator = CreateEvaluator(evaluation, customerRows: []);

        var outcome = await evaluator.EvaluateAsync(evaluation);

        Assert.True(
            outcome.PhysicalExecution?.Interpretation is not null,
            string.Join(
                Environment.NewLine,
                outcome.PhysicalExecution?.Diagnostics.Select(static diagnostic =>
                    $"execution {diagnostic.Code}: {diagnostic.Message}")
                ?? outcome.PhysicalPlanning?.Diagnostics.Select(static diagnostic =>
                    $"planning {diagnostic.Code}: {diagnostic.Message}")
                ?? outcome.Realization?.Diagnostics.Select(static diagnostic =>
                    $"realization {diagnostic.Code}: {diagnostic.Message}")
                ?? outcome.Compilation.Diagnostics.Select(static diagnostic =>
                    $"compilation {diagnostic.Code}: {diagnostic.Message}")));
        var result = Assert.IsType<RelationQueryExecutionResult>(outcome.Result);
        Assert.Contains(
            result.RequirementGapAnalysis.Gaps,
            static gap => gap.Cause == RelationRequirementGapCause.RelatedObservationNotFound);
        Assert.Equal(evaluation.Evaluation, result.Evaluation);
        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task EvaluateAsync_ExpressionAuthoredSuppliedLoadHydratesFlattenedDtoAndDiagnosesMissingCustomer()
    {
        var author = RelationQuery.Expression();
        _ = author.Clr.Shape<ExpressionLoad>(LoadCustomerRelationFixture.LoadShapeId);
        _ = author.Clr.Shape<ExpressionCustomer>(LoadCustomerRelationFixture.CustomerShapeId);
        _ = author.Clr.Shape<ExpressionLoadSearchDto>(LoadCustomerRelationFixture.LoadSearchShapeId);
        var loads = author.Source<ExpressionLoad>();
        var customers = author.Traverse<ExpressionLoad, ExpressionCustomer>(
            loads,
            load => load.CustomerId);
        var documents = author.Project(
            customers,
            (ExpressionLoad load, ExpressionCustomer customer) => new ExpressionLoadSearchDto
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name,
                CustomerType = customer.Type
            });
        var relation = documents.BuildRelation(document => document.Id);

        var successfulEvaluation = author.Evaluate(
                relation,
                new("tests/expression-relation-success"))
            .Supply(
                [new ExpressionLoad { Id = "load-1", CustomerId = "customer-1" }],
                static load => load.Id,
                evidenceReference: "tests/expression-root")
            .Build();
        var successfulOutcome = await CreateEvaluator(
                successfulEvaluation,
                customerRows: [Customer("customer-1", "Acme", "Priority")])
            .EvaluateAsync(successfulEvaluation);

        Assert.True(successfulOutcome.IsSuccessful);
        var successfulResult = Assert.IsType<RelationQueryExecutionResult>(successfulOutcome.Result);
        var successfulRow = Assert.Single(
            Assert.IsType<RelationQueryRelationResult>(successfulResult.Relation).Rows);
        Assert.Equal(ObservationValue.FromString("load-1"), successfulRow.Value.GetProperty("Id"));
        Assert.Equal(
            ObservationValue.FromString("customer-1"),
            successfulRow.Value.GetProperty("CustomerId"));
        Assert.Equal(ObservationValue.FromString("Acme"), successfulRow.Value.GetProperty("CustomerName"));
        Assert.Equal(ObservationValue.FromString("Priority"), successfulRow.Value.GetProperty("CustomerType"));
        Assert.Empty(successfulResult.RequirementGapAnalysis.Gaps);

        var missingEvaluation = author.Evaluate(
                relation,
                new("tests/expression-relation-gap"))
            .Supply(
                [new ExpressionLoad { Id = "load-2", CustomerId = "customer-missing" }],
                static load => load.Id,
                evidenceReference: "tests/expression-root")
            .Build();
        var missingOutcome = await CreateEvaluator(missingEvaluation, customerRows: [])
            .EvaluateAsync(missingEvaluation);

        var missingResult = Assert.IsType<RelationQueryExecutionResult>(missingOutcome.Result);
        Assert.Equal(RelationQueryExecutionStatus.Incomplete, missingResult.Status);
        var gap = Assert.Single(
            missingResult.RequirementGapAnalysis.Gaps,
            static candidate => candidate.Cause == RelationRequirementGapCause.RelatedObservationNotFound);
        Assert.Equal(missingEvaluation.Evaluation, gap.Evaluation);
        Assert.NotNull(gap.RelationshipContext);
        Assert.Equal(
            ObservationValue.FromString("customer-missing"),
            gap.RelationshipContext.ReferenceValue);
        Assert.Contains(RelationRequirementGapResolutionKind.ProvideRelatedObservation, gap.SuggestedResolutions);
        Assert.Contains(
            missingResult.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.RequirementGapRelatedObservationNotFound);
    }

    [Fact]
    public async Task EvaluateAsync_ParameterizedQueryReturnsRowsAndAggregation()
    {
        var evaluation = CreateParameterizedQueryDocument()
            .Evaluate(
                new("tests/query"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Set(LoadCustomerRelationFixture.StatusParameterId, ObservationValue.FromString("Open"))
            .Build();
        ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> loads =
        [
            Load("load-1", "customer-1", "Open", 12m),
            Load("load-2", "customer-2", "Closed", 30m)
        ];
        ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> customers =
        [
            Customer("customer-1", "Acme", "Priority"),
            Customer("customer-2", "Beta", "Standard")
        ];
        var evaluator = CreateEvaluator(evaluation, loads, customers);

        var outcome = await evaluator.EvaluateAsync(evaluation);

        Assert.True(
            outcome.PhysicalExecution?.Interpretation is not null,
            string.Join(
                Environment.NewLine,
                outcome.PhysicalExecution?.Diagnostics.Select(static diagnostic =>
                    $"execution {diagnostic.Code}: {diagnostic.Message}")
                ?? outcome.PhysicalPlanning?.Diagnostics.Select(static diagnostic =>
                    $"planning {diagnostic.Code}: {diagnostic.Message}")
                ?? outcome.Realization?.Diagnostics.Select(static diagnostic =>
                    $"realization {diagnostic.Code}: {diagnostic.Message}")
                ?? outcome.Compilation.Diagnostics.Select(static diagnostic =>
                    $"compilation {diagnostic.Code}: {diagnostic.Message}")));
        var result = Assert.IsType<RelationQueryExecutionResult>(outcome.Result);
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, result.QueryResults.Length);
        var rows = Assert.Single(
            result.QueryResults,
            branch => branch.Result == LoadCustomerRelationFixture.RowsResultId);
        Assert.Equal("load-1", Assert.Single(rows.Rows).Value
            .GetProperty(LoadCustomerRelationFixture.SearchIdFieldName).String);
        var aggregation = Assert.Single(
            result.QueryResults,
            branch => branch.Result == LoadCustomerRelationFixture.AggregationResultId);
        Assert.Equal(
            1L,
            Assert.Single(aggregation.Rows).Value
                .GetProperty(LoadCustomerRelationFixture.AggregateLoadCountFieldName).Int64);
    }

    [Fact]
    public async Task EvaluateAsync_UsesTheConfiguredInterpreterRealizationForPhysicalPlanning()
    {
        var evaluation = RelationEvaluation("tests/restricted-interpreter", "customer-1");
        var interpreter = new RelationQueryInMemoryInterpreter(
            RelationQueryTemporalExecutionCapabilityProfile.None);
        var evaluator = CreateEvaluator(
            evaluation,
            customerRows: [Customer("customer-1", "Acme", "Priority")],
            interpreter: interpreter);

        var outcome = await evaluator.EvaluateAsync(evaluation);

        Assert.True(outcome.IsSuccessful);
        var realization = Assert.IsType<RelationQueryRealizationReport>(outcome.Realization);
        Assert.Same(interpreter.TargetProfile, realization.TargetProfile);
        Assert.NotEqual(
            RelationQueryInMemoryInterpreter.Default.Realize(outcome.Compilation.Plan!).Fingerprint,
            realization.Fingerprint);
        Assert.Equal(realization.Fingerprint, outcome.PhysicalPlanning!.Plan!.Realization);
        Assert.All(
            outcome.PhysicalExecution!.Evidence!.Capabilities,
            capability => Assert.Contains(
                Uri.EscapeDataString(interpreter.TargetProfile.Id.Value),
                capability.EvidenceReference,
                StringComparison.Ordinal));
        Assert.IsType<RelationQueryRelationResult>(outcome.Result!.Relation);
    }

    [Fact]
    public async Task EvaluateAsync_UnrealizableConfiguredInterpreterStopsBeforePlacement()
    {
        var evaluation = TemporalRelationQueryFixture.CreateQueryDocument(
                TemporalRelationQueryFixture.CreatePointMatch(),
                JoinKind.Inner)
            .Evaluate(
                new("tests/restricted-interpreter-unrealizable"),
                [TemporalRelationQueryFixture.CreateShapeGraphDocument()])
            .Build();
        var interpreter = new RelationQueryInMemoryInterpreter(
            RelationQueryTemporalExecutionCapabilityProfile.None);
        var placementInvoked = false;
        RelationQueryEvaluator evaluator = new(
            _ =>
            {
                placementInvoked = true;
                throw new InvalidOperationException("Placement must not run for an unrealizable evaluation.");
            },
            FederatedLoadPhysicalExecutionFixture.CreatePolicy(),
            [],
            interpreter);

        var outcome = await evaluator.EvaluateAsync(evaluation);

        Assert.False(placementInvoked);
        Assert.True(outcome.Compilation.IsSuccessful);
        var realization = Assert.IsType<RelationQueryRealizationReport>(outcome.Realization);
        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, realization.Status);
        Assert.Same(interpreter.TargetProfile, realization.TargetProfile);
        Assert.Contains(
            realization.Diagnostics,
            static diagnostic => diagnostic.Code == RelationQueryRealizationDiagnosticCodes.RequirementUnavailable);
        Assert.Null(outcome.Placement);
        Assert.Null(outcome.PhysicalPlanning);
        Assert.Null(outcome.PhysicalExecution);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task EvaluateAsync_PropagatesCancellationThroughSourceAcquisition()
    {
        var evaluation = CreateParameterizedQueryDocument()
            .Evaluate(
                new("tests/cancellation"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Set(LoadCustomerRelationFixture.StatusParameterId, ObservationValue.FromString("Open"))
            .Build();
        using CancellationTokenSource cancellation = new();
        var evaluator = CreateEvaluator(
            evaluation,
            loadRows: [Load("load-1", "customer-1", "Open", 12m)],
            customerRows: [Customer("customer-1", "Acme", "Priority")],
            afterLoadRead: cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await evaluator.EvaluateAsync(evaluation, cancellation.Token));
    }

    [Fact]
    public async Task EvaluateAsync_RejectsStaleOrForeignCompiledPlanAttribution()
    {
        var document = CreateParameterizedQueryDocument();
        var compilation = RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var reference = RelationQueryCompiledPlanReference.From(plan);
        RelationQueryCompiledPlanReference foreignReference = new(
            reference.CompilerProfile,
            reference.DefinitionSchemaVersion,
            reference.DefinitionFingerprint,
            new(
                reference.ShapeSnapshotsFingerprint.Algorithm,
                reference.ShapeSnapshotsFingerprint.Canonicalization,
                "foreign-shape-snapshot"),
            reference.RelationshipCatalogFingerprint,
            reference.DemandFingerprint,
            reference.Inputs);
        var evaluation = document
            .Evaluate(
                new("tests/foreign-plan"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument,
                foreignReference)
            .Set(LoadCustomerRelationFixture.StatusParameterId, ObservationValue.FromString("Open"))
            .Build();
        RelationQueryEvaluator evaluator = new(
            static _ => throw new InvalidOperationException("Placement must not run for a foreign plan."),
            FederatedLoadPhysicalExecutionFixture.CreatePolicy(),
            []);

        var outcome = await evaluator.EvaluateAsync(evaluation);

        Assert.False(outcome.IsSuccessful);
        Assert.Null(outcome.Realization);
        var diagnostic = Assert.Single(outcome.Diagnostics);
        Assert.Equal(RelationQueryEvaluationDiagnosticCodes.PlanReferenceMismatch, diagnostic.Code);
        Assert.Contains("shapes", diagnostic.PlanComponents);
    }

    [Fact]
    public async Task EvaluateAsync_RetainsTheAttemptedPlacementWhenPhysicalPlanningFails()
    {
        var evaluation = RelationEvaluation("tests/planning-failure", "customer-1");
        RelationQuerySourcePlacement? attemptedPlacement = null;
        RelationQueryEvaluator evaluator = new(
            plan =>
            {
                var conventional = LoadCustomerRelationFixture.CreatePhysicalPlacement(plan);
                var omittedTraversal = Assert.Single(plan.InputContract.Traversals).Input.Id;
                attemptedPlacement = new(
                    RelationQuerySourcePlacement.CurrentSchemaVersion,
                    conventional.Plan,
                    conventional.ConventionSetVersion,
                    conventional.SourceInstances,
                    [.. conventional.Bindings.Where(binding => binding.Input != omittedTraversal)]);
                return attemptedPlacement;
            },
            FederatedLoadPhysicalExecutionFixture.CreatePolicy(),
            []);

        var outcome = await evaluator.EvaluateAsync(evaluation);

        Assert.Same(attemptedPlacement, outcome.Placement);
        Assert.False(Assert.IsType<RelationQueryPhysicalPlanningResult>(outcome.PhysicalPlanning).IsSuccessful);
        Assert.Null(outcome.PhysicalExecution);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsStructuredPlanningFailureForForeignPlacement()
    {
        var evaluation = RelationEvaluation("tests/foreign-placement", "customer-1");
        RelationQuerySourcePlacement? attemptedPlacement = null;
        RelationQueryEvaluator evaluator = new(
            plan =>
            {
                var conventional = LoadCustomerRelationFixture.CreatePhysicalPlacement(plan);
                var reference = conventional.Plan;
                RelationQueryCompiledPlanReference foreignReference = new(
                    reference.CompilerProfile,
                    reference.DefinitionSchemaVersion,
                    reference.DefinitionFingerprint,
                    new(
                        reference.ShapeSnapshotsFingerprint.Algorithm,
                        reference.ShapeSnapshotsFingerprint.Canonicalization,
                        "foreign-placement-shape-snapshot"),
                    reference.RelationshipCatalogFingerprint,
                    reference.DemandFingerprint,
                    reference.Inputs);
                attemptedPlacement = new(
                    RelationQuerySourcePlacement.CurrentSchemaVersion,
                    foreignReference,
                    conventional.ConventionSetVersion,
                    conventional.SourceInstances,
                    conventional.Bindings);
                return attemptedPlacement;
            },
            FederatedLoadPhysicalExecutionFixture.CreatePolicy(),
            []);

        var outcome = await evaluator.EvaluateAsync(evaluation);

        Assert.Same(attemptedPlacement, outcome.Placement);
        var planning = Assert.IsType<RelationQueryPhysicalPlanningResult>(outcome.PhysicalPlanning);
        Assert.False(planning.IsSuccessful);
        Assert.Contains(
            planning.Diagnostics,
            static diagnostic => diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch);
        Assert.Null(outcome.PhysicalExecution);
        Assert.Equal(RelationQueryExecutionStatus.Failed, outcome.Status);
    }

    static RelationQueryEvaluation RelationEvaluation(string evaluationId, string customerId) =>
        LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new(evaluationId),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Supply(
            [
                new Observation(
                    LoadCustomerRelationFixture.LoadShapeLocalId,
                    "load-1",
                    new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                    {
                        [LoadCustomerRelationFixture.LoadIdFieldName] = ObservationValue.FromString("load-1"),
                        [LoadCustomerRelationFixture.LoadCustomerIdFieldName] = ObservationValue.FromString(customerId)
                    })
            ],
            evidenceReference: "tests/root")
            .Build();

    static RelationQueryDocument CreateParameterizedQueryDocument()
    {
        QueryNodeId filter = new("tests/filter-after-customer");
        QueryDefinition definition = new(
            new("tests/rows-and-aggregation"),
            new("Rows and aggregation"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(
                        LoadCustomerRelationFixture.LoadSourceNodeId,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new TraverseRelationshipQueryNode(
                        LoadCustomerRelationFixture.CustomerTraversalNodeId,
                        LoadCustomerRelationFixture.LoadSourceNodeId,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadCustomerRelationshipId,
                        RelationshipTraversalDirection.Forward,
                        LoadCustomerRelationFixture.CustomerBinding,
                        JoinKind.Left,
                        QueryInputRequirement.Optional),
                    new FilterQueryNode(
                        filter,
                        LoadCustomerRelationFixture.CustomerTraversalNodeId,
                        Expr.Eq(
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadStatusPath),
                            Expr.Param(LoadCustomerRelationFixture.StatusParameterId.Value))),
                    new ProjectQueryNode(
                        LoadCustomerRelationFixture.ProjectionNodeId,
                        filter,
                        LoadCustomerRelationFixture.SearchBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new ProjectionAssignment(
                                LoadCustomerRelationFixture.SearchIdAssignmentId,
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Field(
                                    LoadCustomerRelationFixture.LoadBinding,
                                    LoadCustomerRelationFixture.LoadIdPath)),
                            new ProjectionAssignment(
                                LoadCustomerRelationFixture.SearchCustomerNameAssignmentId,
                                LoadCustomerRelationFixture.SearchCustomerNamePath,
                                Expr.Field(
                                    LoadCustomerRelationFixture.CustomerBinding,
                                    LoadCustomerRelationFixture.CustomerNamePath))
                        ]),
                    new AggregateQueryNode(
                        LoadCustomerRelationFixture.AggregateNodeId,
                        filter,
                        LoadCustomerRelationFixture.AggregateBinding,
                        LoadCustomerRelationFixture.LoadAggregateShapeId,
                        groupings:
                        [
                            new QueryGrouping(
                                LoadCustomerRelationFixture.AggregateCustomerNameGroupingId,
                                LoadCustomerRelationFixture.AggregateCustomerNamePath,
                                Expr.Field(
                                    LoadCustomerRelationFixture.CustomerBinding,
                                    LoadCustomerRelationFixture.CustomerNamePath))
                        ],
                        aggregates:
                        [
                            new QueryAggregateAssignment(
                                LoadCustomerRelationFixture.AggregateLoadCountAssignmentId,
                                LoadCustomerRelationFixture.AggregateLoadCountPath,
                                AggregateOperator.Count),
                            new QueryAggregateAssignment(
                                LoadCustomerRelationFixture.AggregateTotalAmountAssignmentId,
                                LoadCustomerRelationFixture.AggregateTotalAmountPath,
                                AggregateOperator.Sum,
                                Expr.Field(
                                    LoadCustomerRelationFixture.LoadBinding,
                                    LoadCustomerRelationFixture.LoadAmountPath))
                        ])
                ],
                parameters:
                [
                    new QueryParameterDefinition(
                        LoadCustomerRelationFixture.StatusParameterId,
                        new ScalarTypeRef(ScalarTypeKind.String))
                ]),
            results:
            [
                new AggregationQueryResultDefinition(
                    LoadCustomerRelationFixture.AggregationResultId,
                    LoadCustomerRelationFixture.AggregateNodeId),
                new RowsQueryResultDefinition(
                    LoadCustomerRelationFixture.RowsResultId,
                    LoadCustomerRelationFixture.ProjectionNodeId)
            ]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryEvaluator CreateEvaluator(
        RelationQueryEvaluation evaluation,
        ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> loadRows = default,
        ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> customerRows = default,
        Action? afterLoadRead = null,
        RelationQueryInMemoryInterpreter? interpreter = null)
    {
        var compilation = RelationQueryStaticCompiler.Compile(evaluation.Compilation);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var placement = LoadCustomerRelationFixture.CreatePhysicalPlacement(plan);
        List<IRelationQuerySourceReader> readers = [];
        if (placement.Bindings.Any(binding => binding.Source == FederatedLoadPhysicalExecutionFixture.LoadsSource
                && binding.Acquisition != RelationQuerySourceAcquisitionKind.Supplied))
        {
            var source = placement.SourceInstances.Single(
                candidate => candidate.Id == FederatedLoadPhysicalExecutionFixture.LoadsSource);
            readers.Add(new DeterministicRelationQuerySourceReader(
                new(source.Id, source.ExecutionDomain, source.TargetProfile),
                loadRows,
                afterRead: afterLoadRead is null ? null : _ => afterLoadRead()));
        }

        if (placement.Bindings.Any(binding => binding.Source == FederatedLoadPhysicalExecutionFixture.CustomersSource
                && binding.Acquisition != RelationQuerySourceAcquisitionKind.Supplied))
        {
            var source = placement.SourceInstances.Single(
                candidate => candidate.Id == FederatedLoadPhysicalExecutionFixture.CustomersSource);
            readers.Add(new DeterministicRelationQuerySourceReader(
                new(source.Id, source.ExecutionDomain, source.TargetProfile),
                customerRows));
        }

        return new(
            static plan => LoadCustomerRelationFixture.CreatePhysicalPlacement(plan),
            FederatedLoadPhysicalExecutionFixture.CreatePolicy(),
            readers,
            interpreter);
    }

    static DeterministicRelationQuerySourceReader.SourceRow Load(
        string id,
        string customerId,
        string status,
        decimal amount) => DeterministicRelationQuerySourceReader.SourceRow.Create(
        id,
        (LoadCustomerRelationFixture.LoadIdPath, ObservationValue.FromString(id)),
        (LoadCustomerRelationFixture.LoadCustomerIdPath, ObservationValue.FromString(customerId)),
        (LoadCustomerRelationFixture.LoadStatusPath, ObservationValue.FromString(status)),
        (LoadCustomerRelationFixture.LoadAmountPath, ObservationValue.FromDecimal(amount)),
        (LoadCustomerRelationFixture.LoadActivePath, ObservationValue.FromBool(true)));

    static DeterministicRelationQuerySourceReader.SourceRow Customer(
        string id,
        string name,
        string type) => DeterministicRelationQuerySourceReader.SourceRow.Create(
        id,
        (LoadCustomerRelationFixture.CustomerIdPath, ObservationValue.FromString(id)),
        (LoadCustomerRelationFixture.CustomerNamePath, ObservationValue.FromString(name)),
        (LoadCustomerRelationFixture.CustomerTypePath, ObservationValue.FromString(type)));

    sealed record LoadRoot(string Id, string CustomerId);

    sealed class FloatScoreInput
    {
        public required string Id { get; init; }

        public required FloatScoreSignals Signals { get; init; }
    }

    sealed class FloatScoreSignals
    {
        public float Score { get; init; }
    }

    sealed class FloatScoreOutput
    {
        public required string Id { get; init; }

        public float Score { get; init; }
    }

    sealed class ExpressionLoad
    {
        public string Id { get; init; } = string.Empty;

        public string CustomerId { get; init; } = string.Empty;
    }

    sealed class ExpressionCustomer
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;
    }

    sealed class ExpressionLoadSearchDto
    {
        public string Id { get; init; } = string.Empty;

        public string CustomerId { get; init; } = string.Empty;

        public string CustomerName { get; init; } = string.Empty;

        public string CustomerType { get; init; } = string.Empty;
    }
}
