using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryInMemoryInterpreterTests
{
    [Fact]
    public void Execute_EnrichedRelationProjectsKeyRootAndOccurrenceProvenance()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("b", "load-2", "customer-2", "Beta"),
                new("a", "load-1", "customer-1", "Acme")
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, relation.State);
        Assert.Equal(LoadCustomerRelationFixture.LoadSearchRelationId, relation.Relation);
        Assert.Equal(LoadCustomerRelationFixture.LoadSearchShapeId, relation.Shape);
        Assert.Equal(RelationOutputMode.OnePerRoot, relation.Mode);
        Assert.Equal(2, relation.Rows.Length);

        AssertRelationRow(
            relation.Rows[0],
            scenario.Loads["a"],
            scenario.Customers["a"],
            "load-1",
            "Acme");
        AssertRelationRow(
            relation.Rows[1],
            scenario.Loads["b"],
            scenario.Customers["b"],
            "load-2",
            "Beta");
    }

    [Fact]
    public void ExecutionResult_NormalizesDiagnosticsAndRejectsInvalidPublicStates()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "customer-1", "Acme")]);
        var original = Execute(plan, scenario.Evidence);
        var expression = new RelationRuntimeDiagnostic(
            RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
            DiagnosticSeverity.Warning,
            "Expression warning.",
            original.Evaluation);
        var evidence = new RelationRuntimeDiagnostic(
            RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive,
            DiagnosticSeverity.Warning,
            "Evidence warning.",
            original.Evaluation);

        var normalized = new RelationQueryExecutionResult(
            original.Status,
            original.Evidence,
            original.RequirementGapAnalysis,
            original.Relation,
            original.QueryResults,
            [evidence, expression]);

        Assert.Equal(
            [
                RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive
            ],
            normalized.Diagnostics.Select(static diagnostic => diagnostic.Code));
        Assert.Throws<ArgumentException>(() => new RelationQueryExecutionResult(
            original.Status,
            original.Evidence,
            original.RequirementGapAnalysis,
            original.Relation,
            original.QueryResults,
            [expression, expression]));
        Assert.Throws<ArgumentException>(() => new RelationQueryExecutionResult(
            RelationQueryExecutionStatus.Incomplete,
            original.Evidence,
            original.RequirementGapAnalysis,
            relation: null,
            queryResults: [],
            diagnostics: []));
    }

    [Fact]
    public void Execute_PreCanceledTokenPropagatesCancellation()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "customer-1", "Acme")]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RelationQueryInMemoryInterpreter.Default.Execute(
                new(plan, scenario.Evidence),
                cancellation.Token));
    }

    [Fact]
    public void Execute_SelectedRelationFieldUsesHiddenKeyButEmitsOnlyDemandedField()
    {
        var plan = Compile(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            RelationQueryCompilationDemand.ForRelationFields(
            [
                new(
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    LoadCustomerRelationFixture.SearchCustomerNamePath)
            ]));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "customer-1", "Acme")]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var row = Assert.Single(Assert.IsType<RelationQueryRelationResult>(result.Relation).Rows);
        Assert.Equal(ObservationValue.FromString("load-1"), row.Identity);
        AssertObject(
            row.Value,
            (LoadCustomerRelationFixture.SearchCustomerNameFieldName, ObservationValue.FromString("Acme")));
    }

    [Fact]
    public void Execute_RowsQueryFiltersTraversesOrdersAndAppliesKeysetBoundary()
    {
        var plan = Compile(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    LoadCustomerRelationFixture.RowsResultId,
                    [
                        new(
                            LoadCustomerRelationFixture.LoadSearchShapeId,
                            LoadCustomerRelationFixture.SearchIdPath),
                        new(
                            LoadCustomerRelationFixture.LoadSearchShapeId,
                            LoadCustomerRelationFixture.SearchCustomerNamePath)
                    ])
            ]));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("c", "load-c", "customer-c", "Gamma", Status: "Open"),
                new("a", "load-a", "customer-a", "Acme", Status: "Open"),
                new("b", "load-b", "customer-b", "Beta", Status: "Closed")
            ],
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [LoadCustomerRelationFixture.StatusParameterId] = ObservationValue.FromString("Open"),
                [LoadCustomerRelationFixture.CursorParameterId] = ObservationValue.FromString("load-a")
            });

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Null(result.Relation);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(LoadCustomerRelationFixture.RowsResultId, branch.Result);
        Assert.Equal(RelationQueryExecutionResultKind.Rows, branch.Kind);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, branch.State);
        var row = Assert.Single(branch.Rows);
        Assert.Null(row.Root);
        AssertObject(
            row.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("load-c")),
            (LoadCustomerRelationFixture.SearchCustomerNameFieldName, ObservationValue.FromString("Gamma")));
        Assert.Equal(
            [scenario.Customers["c"].Id, scenario.Loads["c"].Id],
            row.InputOccurrences.Select(static occurrence => occurrence.Id));
    }

    [Fact]
    public void Execute_StructuredAnyFiltersUsingSameElementCorrelation()
    {
        var stopsPath = FieldPath.FromField("Stops");
        var plan = CompileWithShapes(
            CreateStructuredStopsAnyQueryDocument(stopsPath),
            CreateStructuredStopsShapeDocuments(),
            QueryFields(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath));
        var matching = new RelationQueryObservationOccurrence(
            new("load/structured-any/matching"),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            "load-matching");
        var split = new RelationQueryObservationOccurrence(
            new("load/structured-any/split"),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            "load-split");
        var empty = new RelationQueryObservationOccurrence(
            new("load/structured-any/empty"),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            "load-empty");
        var source = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>());
        var stopValues = new Dictionary<RelationQueryOccurrenceId, ObservationValue>
        {
            [matching.Id] = ObservationValue.FromArray(
            [
                StructuredStop("Seattle", "Pickup"),
                StructuredStop("Portland", "Delivery")
            ]),
            [split.Id] = ObservationValue.FromArray(
            [
                StructuredStop("Seattle", "Delivery"),
                StructuredStop("Portland", "Pickup")
            ]),
            [empty.Id] = ObservationValue.FromArray([])
        };
        var identities = new Dictionary<RelationQueryOccurrenceId, string>
        {
            [matching.Id] = "load-matching",
            [split.Id] = "load-split",
            [empty.Id] = "load-empty"
        };
        ImmutableArray<RelationQueryFieldEvidence>.Builder fields =
            ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>())
        {
            foreach (var occurrence in new[] { matching, split, empty })
            {
                var value = input.Field.Path == LoadCustomerRelationFixture.LoadIdPath
                    ? ObservationValue.FromString(identities[occurrence.Id])
                    : input.Field.Path == stopsPath
                        ? stopValues[occurrence.Id]
                        : throw new InvalidOperationException(
                            $"Unexpected structured-any field input '{input.Field.Path}'.");
                fields.Add(new(
                    input.Id,
                    occurrence.Id,
                    RelationQueryFieldEvidenceState.Value,
                    value));
            }
        }
        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/structured-any-evaluation"),
            plan,
            sources:
            [
                new(
                    source.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [matching, split, empty])
            ],
            fields: fields.ToImmutable(),
            capabilities: AvailableCapabilities(plan));

        var result = Execute(plan, evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        var row = Assert.Single(Assert.Single(result.QueryResults).Rows);
        AssertObject(
            row.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("load-matching")));
        Assert.Equal([matching.Id], row.InputOccurrences.Select(static occurrence => occurrence.Id));
    }

    [Fact]
    public void Execute_AggregationQueryGroupsCountsAndAppliesAggregateFilter()
    {
        var plan = Compile(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    LoadCustomerRelationFixture.AggregationResultId,
                    [
                        new(
                            LoadCustomerRelationFixture.LoadAggregateShapeId,
                            LoadCustomerRelationFixture.AggregateCustomerNamePath),
                        new(
                            LoadCustomerRelationFixture.LoadAggregateShapeId,
                            LoadCustomerRelationFixture.AggregateLoadCountPath),
                        new(
                            LoadCustomerRelationFixture.LoadAggregateShapeId,
                            LoadCustomerRelationFixture.AggregateTotalAmountPath)
                    ])
            ]));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("a", "load-a", "customer-1", "Acme", Amount: 10d, Active: true),
                new("b", "load-b", "customer-1", "Acme", Amount: 5d, Active: false),
                new("c", "load-c", "customer-2", "Beta", Amount: 7d, Active: true)
            ],
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [LoadCustomerRelationFixture.StatusParameterId] = ObservationValue.FromString("Open")
            });

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(LoadCustomerRelationFixture.AggregationResultId, branch.Result);
        Assert.Equal(RelationQueryExecutionResultKind.Aggregation, branch.Kind);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, branch.State);
        Assert.Equal(2, branch.Rows.Length);
        AssertObject(
            branch.Rows[0].Value,
            (LoadCustomerRelationFixture.AggregateCustomerNameFieldName, ObservationValue.FromString("Acme")),
            (LoadCustomerRelationFixture.AggregateLoadCountFieldName, ObservationValue.FromInt64(2)),
            (LoadCustomerRelationFixture.AggregateTotalAmountFieldName, ObservationValue.FromDouble(10d)));
        Assert.Equal(
            [
                scenario.Customers["a"].Id,
                scenario.Customers["b"].Id,
                scenario.Loads["a"].Id,
                scenario.Loads["b"].Id
            ],
            branch.Rows[0].InputOccurrences.Select(static occurrence => occurrence.Id));
        AssertObject(
            branch.Rows[1].Value,
            (LoadCustomerRelationFixture.AggregateCustomerNameFieldName, ObservationValue.FromString("Beta")),
            (LoadCustomerRelationFixture.AggregateLoadCountFieldName, ObservationValue.FromInt64(1)),
            (LoadCustomerRelationFixture.AggregateTotalAmountFieldName, ObservationValue.FromDouble(7d)));
    }

    [Fact]
    public void Execute_AverageAggregateRealizesAndProducesCanonicalDecimalResults()
    {
        var original = Assert.IsType<IRQueryDefinition>(
            LoadCustomerRelationFixture.RepresentativeQueryDocument.Definition);
        var definition = original with
        {
            Body = original.Body with
            {
                Nodes =
                [
                    .. original.Body.Nodes.Select(static node => node is AggregateQueryNode aggregate
                        ? aggregate with
                        {
                            Aggregates =
                            [
                                .. aggregate.Aggregates.Select(static assignment =>
                                    assignment.Id == LoadCustomerRelationFixture.AggregateTotalAmountAssignmentId
                                        ? assignment with
                                        {
                                            Operation = AggregateOperator.Average,
                                            Filter = null
                                        }
                                        : assignment)
                            ]
                        }
                        : node)
                ]
            }
        };
        var plan = Compile(
            RelationQueryDocument.FromDefinition(definition),
            RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    LoadCustomerRelationFixture.AggregationResultId,
                    [
                        new(
                            LoadCustomerRelationFixture.LoadAggregateShapeId,
                            LoadCustomerRelationFixture.AggregateTotalAmountPath)
                    ])
            ]));
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var scenario = CreateEvidence(
            plan,
            specs:
            [
                new("a", "load-a", "customer-1", "Acme", Amount: 10d),
                new("b", "load-b", "customer-1", "Acme", Amount: 5d),
                new("c", "load-c", "customer-2", "Beta", Amount: 7d)
            ],
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [LoadCustomerRelationFixture.StatusParameterId] = ObservationValue.FromString("Open")
            });

        var result = Execute(plan, scenario.Evidence);

        Assert.True(realization.IsRealizable);
        var averageRequirement = Assert.Single(
            realization.Requirements,
            static requirement => requirement.Capability is LogicalRelationQueryCapability
            {
                Kind: RelationQueryLogicalCapabilityKind.AverageAggregate
            });
        Assert.Contains(
            realization.Decisions,
            decision => decision.Requirement == averageRequirement.Id
                && decision.Kind == RelationQueryRealizationDecisionKind.Native);
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, branch.State);
        var averages = branch.Rows
            .Select(static row => row.Value.Fields![
                LoadCustomerRelationFixture.AggregateTotalAmountFieldName])
            .ToArray();
        Assert.Equal(
            [7m, 7.5m],
            averages
                .Select(static average =>
                {
                    Assert.True(average.TryGetDecimal(out var value));
                    return value;
                })
                .Order()
                .ToArray());
    }

    [Fact]
    public void Execute_EvidenceFromDifferentCompiledDemandFailsClosed()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        var otherPlan = Compile(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            RelationQueryCompilationDemand.ForRelationFields(
            [
                new(
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    LoadCustomerRelationFixture.SearchIdPath)
            ]));
        var mismatched = CreateEvidence(otherPlan, specs: []).Evidence;

        var result = Execute(plan, mismatched);

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Null(result.Relation);
        Assert.Empty(result.QueryResults);
        Assert.False(result.RequirementGapAnalysis.IsEvidenceValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.PlanMismatch);
    }

    [Fact]
    public void Execute_EmptyPartialTraversalDoesNotInventAnAuthoritativeLeftJoinRow()
    {
        var plan = Compile(LoadCustomerRelationFixture.OptionalTraversalRelationDocument);
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new(
                    "a",
                    "load-1",
                    "customer-1",
                    "unused",
                    IncludeCustomer: false,
                    TraversalCompleteness: RelationQueryEvidenceCompleteness.Partial)
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, relation.State);
        Assert.Empty(relation.Rows);
        Assert.Empty(result.RequirementGapAnalysis.Gaps);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputCardinalityViolation);
    }

    [Fact]
    public void Execute_NotApplicableTraversalPreservesConclusiveLeftJoinRowWithAbsentRelatedBinding()
    {
        var plan = Compile(LoadCustomerRelationFixture.OptionalTraversalRelationDocument);
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new(
                    "a",
                    "load-1",
                    "customer-1",
                    "unused",
                    IncludeCustomer: false,
                    TraversalState: RelationQueryTraversalEvidenceState.NotApplicable)
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.RequirementGapAnalysis.Gaps);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, relation.State);
        var row = Assert.Single(relation.Rows);
        Assert.Equal(scenario.Loads["a"], row.Root);
        Assert.Equal(ObservationValue.FromString("load-1"), row.Identity);
        Assert.True(row.IsComplete);
        Assert.Empty(row.UnresolvedGaps);
        AssertObject(
            row.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("load-1")));
        Assert.Equal(
            [scenario.Loads["a"].Id],
            row.InputOccurrences.Select(static occurrence => occurrence.Id));
    }

    [Fact]
    public void Execute_UnresolvedFieldGapIsScopedToTheContributingRow()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("a", "load-1", "customer-1", "Acme"),
                new(
                    "b",
                    "load-2",
                    "customer-2",
                    "unused",
                    CustomerNameState: RelationQueryFieldEvidenceState.NotLoaded)
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, relation.State);
        Assert.Equal(2, relation.Rows.Length);

        var complete = Assert.Single(relation.Rows, row => row.Root?.Id == scenario.Loads["a"].Id);
        Assert.True(complete.IsComplete);
        AssertObject(
            complete.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("load-1")),
            (LoadCustomerRelationFixture.SearchCustomerNameFieldName, ObservationValue.FromString("Acme")));

        var incomplete = Assert.Single(relation.Rows, row => row.Root?.Id == scenario.Loads["b"].Id);
        Assert.False(incomplete.IsComplete);
        var gap = Assert.Single(result.RequirementGapAnalysis.Gaps);
        Assert.Equal(RelationRequirementGapCause.RequiredFieldNotLoaded, gap.Cause);
        Assert.Equal(scenario.Customers["b"].Id, gap.Occurrence?.Id);
        Assert.Equal(gap.Id, Assert.Single(incomplete.UnresolvedGaps));
        AssertObject(
            incomplete.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("load-2")));
    }

    [Fact]
    public void Execute_DefaultSubstitutionRepairsOnlyTheAffectedField()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("a", "load-1", "customer-1", "Acme"),
                new(
                    "b",
                    "load-2",
                    "customer-2",
                    "unused",
                    CustomerNameState: RelationQueryFieldEvidenceState.NotLoaded)
            ]);
        var fallback = ObservationValue.FromString("Unknown customer");
        var policy = new RelationRequirementGapPolicy(
            new("tests/default-customer-name-v1"),
            RelationRequirementGapPolicySource.Explicit,
            (_, impact) => new(
                impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchCustomerNamePath
                    ? RelationRequirementGapDisposition.UseDefault(fallback)
                    : RelationRequirementGapDisposition.Unresolved,
                RelationRequirementGapReportingKind.Suppress));

        var result = Execute(plan, scenario.Evidence, policy);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, relation.State);
        var unaffected = Assert.Single(relation.Rows, row => row.Root?.Id == scenario.Loads["a"].Id);
        AssertObject(
            unaffected.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("load-1")),
            (LoadCustomerRelationFixture.SearchCustomerNameFieldName, ObservationValue.FromString("Acme")));
        var repaired = Assert.Single(relation.Rows, row => row.Root?.Id == scenario.Loads["b"].Id);
        Assert.True(repaired.IsComplete);
        AssertObject(
            repaired.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("load-2")),
            (LoadCustomerRelationFixture.SearchCustomerNameFieldName, fallback));
    }

    [Fact]
    public void Execute_FieldSubstitutionFeedsKeyButDoesNotResolveDistinctIdentityImpact()
    {
        var plan = Compile(CreatePolicySubstitutedKeyRelationDocument());
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new(
                    "a",
                    "load-1",
                    "unused",
                    "unused",
                    IncludeCustomer: false,
                    StatusState: RelationQueryFieldEvidenceState.NotLoaded)
            ]);
        var fallback = ObservationValue.FromString("substituted-key");
        var policy = new RelationRequirementGapPolicy(
            new("tests/default-relation-key-v1"),
            RelationRequirementGapPolicySource.Explicit,
            (_, impact) => new(
                impact.Output.Field?.Path == LoadCustomerRelationFixture.SearchIdPath
                    ? RelationRequirementGapDisposition.UseDefault(fallback)
                    : RelationRequirementGapDisposition.Unresolved,
                RelationRequirementGapReportingKind.Suppress));

        var result = Execute(plan, scenario.Evidence, policy);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputIdentityInvalid);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, relation.State);
        var row = Assert.Single(relation.Rows);
        Assert.Equal(fallback, row.Identity);
        Assert.False(row.IsComplete);
        Assert.Single(row.UnresolvedGaps);
        AssertObject(
            row.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, fallback));
        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
    }

    [Fact]
    public void Execute_RowSuppressionSatisfiesOnePerRootCardinalityUnderPolicy()
    {
        var plan = Compile(CreatePolicySubstitutedKeyRelationDocument());
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new(
                    "a",
                    "load-1",
                    "unused",
                    "unused",
                    IncludeCustomer: false,
                    StatusState: RelationQueryFieldEvidenceState.NotLoaded)
            ]);
        var policy = new RelationRequirementGapPolicy(
            new("tests/suppress-relation-row-v1"),
            RelationRequirementGapPolicySource.Explicit,
            static (_, _) => new(
                RelationRequirementGapDisposition.SuppressOutput,
                RelationRequirementGapReportingKind.Suppress));

        var result = Execute(plan, scenario.Evidence, policy);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Suppressed, relation.State);
        Assert.Empty(relation.Rows);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputCardinalityViolation);
    }

    [Theory]
    [InlineData(RelationQueryFieldEvidenceState.NotLoaded)]
    [InlineData(RelationQueryFieldEvidenceState.Missing)]
    public void Execute_DirectSourceTerminalActivatesFieldGapAndAllowsDefaultRepair(
        RelationQueryFieldEvidenceState state)
    {
        var plan = Compile(
            CreateDirectSourceQueryDocument(),
            QueryFields(
                LoadCustomerRelationFixture.LoadShapeId,
                LoadCustomerRelationFixture.LoadIdPath));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new(
                    "a",
                    "load-1",
                    "unused",
                    "unused",
                    IncludeCustomer: false,
                    LoadIdState: state)
            ]);

        var incomplete = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, incomplete.Status);
        var incompleteBranch = Assert.Single(incomplete.QueryResults);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, incompleteBranch.State);
        var incompleteRow = Assert.Single(incompleteBranch.Rows);
        var gap = Assert.Single(incomplete.RequirementGapAnalysis.Gaps);
        Assert.Equal(scenario.Loads["a"].Id, gap.Occurrence?.Id);
        Assert.Equal(gap.Id, Assert.Single(incompleteRow.UnresolvedGaps));
        AssertObject(incompleteRow.Value);
        Assert.DoesNotContain(
            incomplete.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputShapeInvalid);

        var fallback = ObservationValue.FromString("repaired-load-id");
        var policy = new RelationRequirementGapPolicy(
            new("tests/default-direct-source-id-v1"),
            RelationRequirementGapPolicySource.Explicit,
            (_, impact) => new(
                impact.Output.Field?.Path == LoadCustomerRelationFixture.LoadIdPath
                    ? RelationRequirementGapDisposition.UseDefault(fallback)
                    : RelationRequirementGapDisposition.Unresolved,
                RelationRequirementGapReportingKind.Suppress));

        var repaired = Execute(plan, scenario.Evidence, policy);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, repaired.Status);
        var repairedBranch = Assert.Single(repaired.QueryResults);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, repairedBranch.State);
        var repairedRow = Assert.Single(repairedBranch.Rows);
        Assert.True(repairedRow.IsComplete);
        AssertObject(
            repairedRow.Value,
            (LoadCustomerRelationFixture.LoadIdFieldName, fallback));
        Assert.DoesNotContain(
            repaired.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputShapeInvalid);
    }

    [Fact]
    public void Execute_SourceConversionFailureBlocksProvidedRows()
    {
        var plan = Compile(
            CreateDirectSourceQueryDocument(),
            QueryFields(
                LoadCustomerRelationFixture.LoadShapeId,
                LoadCustomerRelationFixture.LoadIdPath));
        var sourceInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>());
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "unused", "unused", IncludeCustomer: false)]);
        var evidence = WithConversionFailures(
            plan,
            scenario.Evidence,
            new RelationQueryConversionFailureEvidence(
                sourceInput.Id,
                occurrence: null,
                evidenceReference: "tests/source-conversion"));

        var result = Execute(plan, evidence);

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, branch.State);
        Assert.Empty(branch.Rows);
        var gap = Assert.Single(result.RequirementGapAnalysis.Gaps);
        Assert.Equal(RelationRequirementGapCause.ConversionFailure, gap.Cause);
        Assert.Equal(sourceInput.Id, gap.Input.Id);
        Assert.Null(gap.Occurrence);
    }

    [Fact]
    public void Execute_ReferenceConversionFailureBlocksCompletedTraversalResults()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        var referenceInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            static input => input.Binding == LoadCustomerRelationFixture.LoadBinding
                && input.Field.Path == LoadCustomerRelationFixture.LoadCustomerIdPath);
        var traversalInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryRelationshipInput>());
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "customer-1", "Acme")]);
        var evidence = WithConversionFailures(
            plan,
            scenario.Evidence,
            new RelationQueryConversionFailureEvidence(
                referenceInput.Id,
                scenario.Loads["a"].Id,
                "tests/reference-conversion"));

        var result = Execute(plan, evidence);

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, relation.State);
        Assert.Empty(relation.Rows);
        var gap = Assert.Single(result.RequirementGapAnalysis.Gaps);
        Assert.Equal(RelationRequirementGapCause.ConversionFailure, gap.Cause);
        Assert.Equal(referenceInput.Id, gap.Input.Id);
        Assert.Contains(traversalInput.Id, gap.BlockedInputs);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputCardinalityViolation);
    }

    [Fact]
    public void Execute_TraversalConversionFailureRejectsCompletedTraversalResults()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        var traversalInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryRelationshipInput>());
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "customer-1", "Acme")]);
        var evidence = WithConversionFailures(
            plan,
            scenario.Evidence,
            new RelationQueryConversionFailureEvidence(
                traversalInput.Id,
                scenario.Loads["a"].Id,
                "tests/traversal-conversion"));

        var result = Execute(plan, evidence);

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, relation.State);
        Assert.Empty(relation.Rows);
        var gap = Assert.Single(result.RequirementGapAnalysis.Gaps);
        Assert.Equal(RelationRequirementGapCause.ConversionFailure, gap.Cause);
        Assert.Equal(traversalInput.Id, gap.Input.Id);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputCardinalityViolation);
    }

    [Theory]
    [InlineData(JoinKind.Inner, 1)]
    [InlineData(JoinKind.Full, 3)]
    public void Execute_ExplicitJoinPreservesInnerAndOuterAbsenceSemantics(
        JoinKind joinKind,
        int expectedCount)
    {
        var plan = Compile(
            CreateOuterSafeJoinQueryDocument(joinKind),
            RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    LoadCustomerRelationFixture.RowsResultId,
                    [
                        new(
                            LoadCustomerRelationFixture.LoadSearchShapeId,
                            LoadCustomerRelationFixture.SearchCustomerTypePath),
                        new(
                            LoadCustomerRelationFixture.LoadSearchShapeId,
                            LoadCustomerRelationFixture.SearchCustomerNamePath)
                    ])
            ]));
        ExplicitJoinEvidence scenario = CreateExplicitJoinEvidence(plan);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(expectedCount, branch.Rows.Length);
        AssertObject(
            branch.Rows[0].Value,
            (LoadCustomerRelationFixture.SearchCustomerTypeFieldName, ObservationValue.FromString("load-1")),
            (LoadCustomerRelationFixture.SearchCustomerNameFieldName, ObservationValue.FromString("Acme")));
        Assert.Equal(
            [scenario.Customer1.Id, scenario.Load1.Id],
            branch.Rows[0].InputOccurrences.Select(static occurrence => occurrence.Id));

        if (joinKind != JoinKind.Full)
            return;

        AssertObject(
            branch.Rows[1].Value,
            (LoadCustomerRelationFixture.SearchCustomerTypeFieldName, ObservationValue.FromString("load-2")));
        Assert.Equal(
            [scenario.Load2.Id],
            branch.Rows[1].InputOccurrences.Select(static occurrence => occurrence.Id));
        AssertObject(
            branch.Rows[2].Value,
            (LoadCustomerRelationFixture.SearchCustomerNameFieldName, ObservationValue.FromString("Beta")));
        Assert.Equal(
            [scenario.Customer2.Id],
            branch.Rows[2].InputOccurrences.Select(static occurrence => occurrence.Id));
    }

    [Fact]
    public void Execute_RootedJoinOnlyCombinesRowsFromTheSameRoot()
    {
        var plan = Compile(CreateRootPartitionedJoinRelationDocument());
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("b", "load-2", "unused", "unused", IncludeCustomer: false),
                new("a", "load-1", "unused", "unused", IncludeCustomer: false)
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, relation.State);
        Assert.Equal(2, relation.Rows.Length);
        foreach (var spec in new[] { (Key: "a", Id: "load-1"), (Key: "b", Id: "load-2") })
        {
            var row = Assert.Single(
                relation.Rows,
                candidate => candidate.Root?.Id == scenario.Loads[spec.Key].Id);
            Assert.Equal(ObservationValue.FromString(spec.Id), row.Identity);
            AssertObject(
                row.Value,
                (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString(spec.Id)));
            Assert.Equal(
                [scenario.Loads[spec.Key].Id],
                row.InputOccurrences.Select(static occurrence => occurrence.Id));
        }
    }

    [Fact]
    public void Execute_ExpandCollectionEmitsOneRowPerItemAndPreservesSourceProvenance()
    {
        var items = new QueryParameterId("items");
        var plan = Compile(
            CreateExpandQueryDocument(items),
            QueryFields(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "unused", "unused", IncludeCustomer: false)],
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [items] = ObservationValue.FromArray(
                [
                    ObservationValue.FromString("second"),
                    ObservationValue.FromString("first")
                ])
            });

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var rows = Assert.Single(result.QueryResults).Rows;
        Assert.Equal(2, rows.Length);
        AssertObject(
            rows[0].Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("expanded")));
        AssertObject(
            rows[1].Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("expanded")));
        Assert.All(
            rows,
            row => Assert.Equal(
                [scenario.Loads["a"].Id],
                row.InputOccurrences.Select(static occurrence => occurrence.Id)));
    }

    [Fact]
    public void Execute_DistinctRetainsFirstRowAndUnionsDuplicateProvenance()
    {
        var plan = Compile(
            CreateDistinctQueryDocument(),
            QueryFields(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("c", "load-c", "unused", "unused", Status: "B", IncludeCustomer: false),
                new("b", "load-b", "unused", "unused", Status: "A", IncludeCustomer: false),
                new("a", "load-a", "unused", "unused", Status: "A", IncludeCustomer: false)
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var rows = Assert.Single(result.QueryResults).Rows;
        Assert.Equal(2, rows.Length);
        AssertObject(
            rows[0].Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("A")));
        Assert.Equal(
            [scenario.Loads["a"].Id, scenario.Loads["b"].Id],
            rows[0].InputOccurrences.Select(static occurrence => occurrence.Id));
        AssertObject(
            rows[1].Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("B")));
        Assert.Equal(
            [scenario.Loads["c"].Id],
            rows[1].InputOccurrences.Select(static occurrence => occurrence.Id));
    }

    [Fact]
    public void Execute_OffsetPageRespectsDescendingOrderWithNullsFirst()
    {
        var plan = Compile(
            CreateOffsetPageQueryDocument(),
            QueryFields(
                LoadCustomerRelationFixture.LoadShapeId,
                LoadCustomerRelationFixture.LoadIdPath));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new(
                    "d",
                    "load-d",
                    "unused",
                    "unused",
                    IncludeCustomer: false,
                    Notes: "a"),
                new(
                    "a",
                    "load-a",
                    "unused",
                    "unused",
                    IncludeCustomer: false,
                    NotesState: RelationQueryFieldEvidenceState.Null),
                new(
                    "c",
                    "load-c",
                    "unused",
                    "unused",
                    IncludeCustomer: false,
                    Notes: "m"),
                new(
                    "b",
                    "load-b",
                    "unused",
                    "unused",
                    IncludeCustomer: false,
                    Notes: "z")
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var rows = Assert.Single(result.QueryResults).Rows;
        Assert.Equal(2, rows.Length);
        AssertObject(
            rows[0].Value,
            (LoadCustomerRelationFixture.LoadIdFieldName, ObservationValue.FromString("load-b")));
        AssertObject(
            rows[1].Value,
            (LoadCustomerRelationFixture.LoadIdFieldName, ObservationValue.FromString("load-c")));
    }

    [Fact]
    public void Execute_OffsetPageAppliesStableDescendingOrdering()
    {
        var plan = Compile(
            CreateOffsetPageQueryDocument(useNullableNotes: false),
            QueryFields(
                LoadCustomerRelationFixture.LoadShapeId,
                LoadCustomerRelationFixture.LoadIdPath));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("c", "load-c", "unused", "unused", Status: "a", IncludeCustomer: false),
                new("a", "load-a", "unused", "unused", Status: "z", IncludeCustomer: false),
                new("b", "load-b", "unused", "unused", Status: "m", IncludeCustomer: false)
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var rows = Assert.Single(result.QueryResults).Rows;
        Assert.Equal(2, rows.Length);
        AssertObject(
            rows[0].Value,
            (LoadCustomerRelationFixture.LoadIdFieldName, ObservationValue.FromString("load-b")));
        AssertObject(
            rows[1].Value,
            (LoadCustomerRelationFixture.LoadIdFieldName, ObservationValue.FromString("load-c")));
    }

    [Fact]
    public void Execute_DuplicateRelationKeysProduceIdentityDiagnostic()
    {
        var plan = Compile(CreateDuplicateKeyRelationDocument());
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("b", "load-2", "unused", "unused", IncludeCustomer: false),
                new("a", "load-1", "unused", "unused", IncludeCustomer: false)
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        var relation = Assert.IsType<RelationQueryRelationResult>(result.Relation);
        Assert.Equal(2, relation.Rows.Length);
        Assert.All(
            relation.Rows,
            static row => Assert.Equal(ObservationValue.FromString("duplicate"), row.Identity));
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static item => item.Code == RelationRuntimeDiagnosticCodes.ExecutionOutputIdentityInvalid);
        Assert.Equal(scenario.Loads["b"].Id, diagnostic.Occurrence);
        Assert.Equal(LoadCustomerRelationFixture.ProjectionNodeId, diagnostic.Node);
    }

    [Fact]
    public void Execute_FalseRelationInvariantProducesNamedDiagnostic()
    {
        const string invariantName = "allowed-load";
        var plan = Compile(CreateInvariantRelationDocument(invariantName));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs:
            [
                new("b", "rejected", "unused", "unused", IncludeCustomer: false),
                new("a", "allowed", "unused", "unused", IncludeCustomer: false)
            ]);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static item => item.Code == RelationRuntimeDiagnosticCodes.ExecutionInvariantViolation);
        Assert.Equal(scenario.Loads["b"].Id, diagnostic.Occurrence);
        Assert.Equal(invariantName, diagnostic.SemanticSite);
    }

    [Theory]
    [InlineData(RelationQueryEvidenceCompleteness.Complete)]
    [InlineData(RelationQueryEvidenceCompleteness.Partial)]
    public void Execute_MultipleRowsForOneRootProduceCardinalityDiagnosticEvenWhenEvidenceIsPartial(
        RelationQueryEvidenceCompleteness completeness)
    {
        var items = new QueryParameterId("items");
        var plan = Compile(CreateExpandingRelationDocument(items));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "unused", "unused", IncludeCustomer: false)],
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [items] = ObservationValue.FromArray(
                [
                    ObservationValue.FromString("one"),
                    ObservationValue.FromString("two")
                ])
            },
            completeness: completeness);

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Equal(2, Assert.IsType<RelationQueryRelationResult>(result.Relation).Rows.Length);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static item => item.Code
                == RelationRuntimeDiagnosticCodes.ExecutionOutputCardinalityViolation);
        Assert.Equal(scenario.Loads["a"].Id, diagnostic.Occurrence);
    }

    [Fact]
    public void Execute_OmittedPartialFieldAndParameterCannotPassSelfEqualityPredicate()
    {
        var expected = new QueryParameterId("expected-status");
        var plan = Compile(
            CreatePartialEvidencePredicateQueryDocument(expected),
            QueryFields(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath));
        var load = new RelationQueryObservationOccurrence(
            new("load/a"),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            "load-1");
        var source = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>(),
            static input => input.Binding == LoadCustomerRelationFixture.LoadBinding);
        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/partial-omission-evaluation"),
            plan,
            RelationQueryEvidenceCompleteness.Partial,
            sources:
            [
                new(
                    source.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [load])
            ],
            capabilities: AvailableCapabilities(plan));

        var result = Execute(plan, evidence);

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, branch.State);
        Assert.Empty(branch.Rows);
        Assert.Empty(result.RequirementGapAnalysis.Gaps);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Execute_AndFilterReadsUnavailableFieldOnlyWhenRequired(
        bool leftValue,
        bool expectsUnavailableAccess)
    {
        var plan = Compile(
            CreateLazyAvailabilityFilterQueryDocument(
                useParameter: false,
                useAnd: true,
                leftValue),
            QueryFields(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath));
        var (evidence, _) = CreateLazyAvailabilityEvidence(plan, unavailableField: true);
        var filterSite = Assert.Single(
            plan.ExecutionSlice.ExpressionSites,
            static site => site.Kind == RelationQueryExpressionSiteKind.FilterPredicate).Analysis.Site.Id.Value;

        var result = Execute(plan, evidence);

        Assert.Equal(
            expectsUnavailableAccess
                ? RelationQueryExecutionStatus.Incomplete
                : RelationQueryExecutionStatus.Succeeded,
            result.Status);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(
            expectsUnavailableAccess
                ? RelationQueryExecutionOutputState.Incomplete
                : RelationQueryExecutionOutputState.Complete,
            branch.State);
        Assert.Empty(branch.Rows);
        Assert.Equal(
            expectsUnavailableAccess,
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive
                && diagnostic.SemanticSite == filterSite));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Execute_OrFilterReadsUnavailableParameterOnlyWhenRequired(
        bool leftValue,
        bool expectsUnavailableAccess)
    {
        var plan = Compile(
            CreateLazyAvailabilityFilterQueryDocument(
                useParameter: true,
                useAnd: false,
                leftValue),
            QueryFields(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath));
        var (evidence, load) = CreateLazyAvailabilityEvidence(plan, unavailableField: false);
        var filterSite = Assert.Single(
            plan.ExecutionSlice.ExpressionSites,
            static site => site.Kind == RelationQueryExpressionSiteKind.FilterPredicate).Analysis.Site.Id.Value;

        var result = Execute(plan, evidence);

        Assert.Equal(
            expectsUnavailableAccess
                ? RelationQueryExecutionStatus.Incomplete
                : RelationQueryExecutionStatus.Succeeded,
            result.Status);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(
            expectsUnavailableAccess
                ? RelationQueryExecutionOutputState.Incomplete
                : RelationQueryExecutionOutputState.Complete,
            branch.State);
        if (expectsUnavailableAccess)
        {
            Assert.Empty(branch.Rows);
        }
        else
        {
            var row = Assert.Single(branch.Rows);
            AssertObject(
                row.Value,
                (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("load-1")));
            Assert.Equal([load.Id], row.InputOccurrences.Select(static occurrence => occurrence.Id));
        }
        Assert.Equal(
            expectsUnavailableAccess,
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive
                && diagnostic.SemanticSite == filterSite));
    }

    [Fact]
    public void Execute_NotProvidedOptionalParameterIsConclusiveUndefinedWhenAccessed()
    {
        var parameter = new QueryParameterId("optional-status");
        var plan = Compile(
            CreateOptionalUndefinedParameterQueryDocument(parameter),
            QueryFields(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath));
        var (evidence, load) = CreateLazyAvailabilityEvidence(plan, unavailableField: false);
        var filterSite = Assert.Single(
            plan.ExecutionSlice.ExpressionSites,
            static site => site.Kind == RelationQueryExpressionSiteKind.FilterPredicate).Analysis.Site.Id.Value;

        var result = Execute(plan, evidence);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.RequirementGapAnalysis.Gaps);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive
                && diagnostic.SemanticSite == filterSite);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(RelationQueryExecutionOutputState.Complete, branch.State);
        var row = Assert.Single(branch.Rows);
        AssertObject(
            row.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString("load-1")));
        Assert.Equal([load.Id], row.InputOccurrences.Select(static occurrence => occurrence.Id));
    }

    [Fact]
    public void Execute_CompilerValidUnsupportedIntrinsicReturnsAttributableDiagnostic()
    {
        var filter = new QueryNodeId("unsupported-intrinsic-filter");
        var plan = Compile(
            CreateUnsupportedIntrinsicQueryDocument(filter),
            QueryFields(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "unused", "unused", IncludeCustomer: false)]);
        var filterSite = Assert.Single(
            plan.ExecutionSlice.ExpressionSites,
            static site => site.Kind == RelationQueryExpressionSiteKind.FilterPredicate).Analysis.Site.Id.Value;

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Null(result.Relation);
        Assert.Empty(result.QueryResults);
        var capabilityInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryCapabilityInput>(),
            static input => input.Capability.Kind == ExprCapabilityRequirementKind.Operation
                && input.Capability.Capability
                    == ExprCapabilities.ForFunction(ExprFunctionNames.GroupByRows));
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static item => item.Code
                == RelationRuntimeDiagnosticCodes.ExecutionTargetCapabilityUnsupported);
        Assert.Equal(capabilityInput.Id, diagnostic.Input);
        Assert.Equal(filter, diagnostic.Node);
        Assert.Equal(filterSite, diagnostic.SemanticSite);
        Assert.Contains(ExprFunctionNames.GroupByRows, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_CompilerValidElementPathReturnsAttributableDiagnostic()
    {
        var filter = new QueryNodeId("element-path-filter");
        var items = new QueryParameterId("nested-items");
        var plan = Compile(
            CreateElementPathQueryDocument(filter, items),
            QueryFields(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath));
        LoadCustomerEvidence scenario = CreateEvidence(
            plan,
            specs: [new("a", "load-1", "unused", "unused", IncludeCustomer: false)],
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [items] = ObservationValue.FromArray(
                [
                    ObservationValue.FromArray([ObservationValue.FromString("value")])
                ])
            });
        var filterSite = Assert.Single(
            plan.ExecutionSlice.ExpressionSites,
            static site => site.Kind == RelationQueryExpressionSiteKind.FilterPredicate).Analysis.Site.Id.Value;

        var result = Execute(plan, scenario.Evidence);

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Null(result.Relation);
        Assert.Empty(result.QueryResults);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static item => item.Code
                == RelationRuntimeDiagnosticCodes.ExecutionTargetCapabilityUnsupported);
        Assert.Null(diagnostic.Input);
        Assert.Equal(filter, diagnostic.Node);
        Assert.Equal(filterSite, diagnostic.SemanticSite);
        Assert.Contains("collection-element", diagnostic.Message, StringComparison.Ordinal);
    }

    static RelationQueryCompilationDemand QueryFields(
        QualifiedShapeId shape,
        params FieldPath[] fields) =>
        RelationQueryCompilationDemand.ForQueryResults(
        [
            QueryResultDemand.SelectedFields(
                LoadCustomerRelationFixture.RowsResultId,
                fields.Select(path => new RelationQueryFieldReference(shape, path)))
        ]);

    static RelationQueryDocument CreatePolicySubstitutedKeyRelationDocument()
    {
        var source = new QueryNodeId("policy-key-source");
        var project = new QueryNodeId("policy-key-project");
        IRRelationDefinition definition = new(
            new("policy-substituted-key-relation"),
            new("PolicySubstitutedKeyRelation"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    source,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new ProjectQueryNode(
                    project,
                    source,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("assign-policy-key"),
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadStatusPath))
                    ])
            ]),
            LoadCustomerRelationFixture.LoadBinding,
            new(
                project,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                RelationOutputMode.OnePerRoot,
                Expr.Field(
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.SearchIdPath)));
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateDirectSourceQueryDocument()
    {
        var source = new QueryNodeId("direct-source");
        IRQueryDefinition definition = new(
            new("direct-source-query"),
            new("DirectSourceQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    source,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId)
            ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, source)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateStructuredStopsAnyQueryDocument(FieldPath stopsPath)
    {
        var source = new QueryNodeId("structured-any-source");
        var filter = new QueryNodeId("structured-any-filter");
        var project = new QueryNodeId("structured-any-project");
        IRQueryDefinition definition = new(
            new("structured-stops-any-query"),
            new("StructuredStopsAnyQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    source,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new FilterQueryNode(
                    filter,
                    source,
                    Expr.Any(
                        Expr.Field(LoadCustomerRelationFixture.LoadBinding, stopsPath),
                        Expr.And(
                            Expr.Eq(Expr.Field("item.Location"), Expr.Const("Seattle")),
                            Expr.Eq(Expr.Field("item.Type"), Expr.Const("Pickup"))))),
                CreateLoadIdProjection(project, filter, "assign-structured-any-id")
            ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static ImmutableArray<ShapeGraphDocument> CreateStructuredStopsShapeDocuments()
    {
        var domain = LoadCustomerRelationFixture.DomainShapeGraphDocument.Graph;
        var stopType = new ObjectTypeRef(
        [
            new("Location", new ScalarTypeRef(ScalarTypeKind.String)),
            new("Type", new ScalarTypeRef(ScalarTypeKind.String))
        ]);
        var shapes = domain.Shapes.Select(shape =>
            shape.Id != LoadCustomerRelationFixture.LoadShapeLocalId
                ? shape
                : new Shape(
                    shape.Id,
                    [
                        .. shape.Fields,
                        new(
                            new("Stops"),
                            stopType,
                            cardinality: FieldCardinality.Many)
                    ],
                    constraints: shape.Constraints,
                    annotations: shape.Annotations));
        var extendedDomain = new ShapeGraph(
            domain.Id,
            [.. shapes],
            domain.NamedTypes,
            annotations: domain.Annotations);
        return
        [
            ShapeGraphDocument.FromGraph(extendedDomain),
            LoadCustomerRelationFixture.DtoShapeGraphDocument
        ];
    }

    static RelationQueryDocument CreateRootPartitionedJoinRelationDocument()
    {
        var source = new QueryNodeId("root-join-source");
        var leftProject = new QueryNodeId("root-join-left");
        var rightProject = new QueryNodeId("root-join-right");
        var join = new QueryNodeId("root-join");
        var outputProject = new QueryNodeId("root-join-output");
        var left = new ValueBindingId("root-join-left-value");
        var right = new ValueBindingId("root-join-right-value");
        IRRelationDefinition definition = new(
            new("root-partitioned-join-relation"),
            new("RootPartitionedJoinRelation"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    source,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new ProjectQueryNode(
                    leftProject,
                    source,
                    left,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("assign-root-join-left-id"),
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadIdPath))
                    ]),
                new ProjectQueryNode(
                    rightProject,
                    source,
                    right,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("assign-root-join-right-id"),
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadIdPath))
                    ]),
                new JoinQueryNode(
                    join,
                    leftProject,
                    rightProject,
                    JoinKind.Inner,
                    Expr.Const(true)),
                new ProjectQueryNode(
                    outputProject,
                    join,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("assign-root-join-output-id"),
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(left, LoadCustomerRelationFixture.SearchIdPath))
                    ])
            ]),
            LoadCustomerRelationFixture.LoadBinding,
            new(
                outputProject,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                RelationOutputMode.OnePerRoot,
                Expr.Field(
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.SearchIdPath)));
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateOptionalUndefinedParameterQueryDocument(
        QueryParameterId parameter)
    {
        var source = new QueryNodeId("optional-parameter-source");
        var filter = new QueryNodeId("optional-parameter-filter");
        var project = new QueryNodeId("optional-parameter-project");
        IRQueryDefinition definition = new(
            new("optional-undefined-parameter-query"),
            new("OptionalUndefinedParameterQuery"),
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
                            Expr.Param(parameter.Value),
                            Expr.Param(parameter.Value))),
                    new ProjectQueryNode(
                        project,
                        filter,
                        LoadCustomerRelationFixture.SearchBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new(
                                new("assign-optional-parameter-load-id"),
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Field(
                                    LoadCustomerRelationFixture.LoadBinding,
                                    LoadCustomerRelationFixture.LoadIdPath))
                        ])
                ],
                parameters:
                [
                    new(
                        parameter,
                        new ScalarTypeRef(ScalarTypeKind.String),
                        FieldPresence.Optional)
                ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateUnsupportedIntrinsicQueryDocument(QueryNodeId filter)
    {
        var source = new QueryNodeId("unsupported-intrinsic-source");
        var project = new QueryNodeId("unsupported-intrinsic-project");
        IRQueryDefinition definition = new(
            new("unsupported-intrinsic-query"),
            new("UnsupportedIntrinsicQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    source,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new FilterQueryNode(
                    filter,
                    source,
                    Expr.Eq(
                        Expr.Call(
                            ExprFunctionNames.Count,
                            Expr.Call(
                                ExprFunctionNames.GroupByRows,
                                Expr.Const(ObservationValue.FromArray(
                                [
                                    ObservationValue.FromString("first"),
                                    ObservationValue.FromString("second")
                                ])),
                                Expr.CurrentItem())),
                        Expr.Const(2))),
                CreateLoadIdProjection(project, filter, "assign-unsupported-intrinsic-id")
            ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateElementPathQueryDocument(
        QueryNodeId filter,
        QueryParameterId items)
    {
        var source = new QueryNodeId("element-path-source");
        var project = new QueryNodeId("element-path-project");
        var itemPath = new FieldPath(
        [
            FieldPathSegment.ForField(ExprFieldRoots.CurrentItem),
            FieldPathSegment.Element()
        ]);
        var selected = Expr.Call(
            ExprFunctionNames.Select,
            Expr.Param(items.Value),
            Expr.Field(itemPath));
        IRQueryDefinition definition = new(
            new("element-path-query"),
            new("ElementPathQuery"),
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
                    CreateLoadIdProjection(project, filter, "assign-element-path-id")
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

    static ProjectQueryNode CreateLoadIdProjection(
        QueryNodeId node,
        QueryNodeId input,
        string assignment) =>
        new(
            node,
            input,
            LoadCustomerRelationFixture.SearchBinding,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            [
                new(
                    new(assignment),
                    LoadCustomerRelationFixture.SearchIdPath,
                    Expr.Field(
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadIdPath))
            ]);

    static RelationQueryDocument CreateOuterSafeJoinQueryDocument(JoinKind joinKind)
    {
        IRQueryDefinition definition = new(
            new("outer-safe-join-query"),
            new("OuterSafeJoinQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new SourceQueryNode(
                    LoadCustomerRelationFixture.CustomerSourceNodeId,
                    LoadCustomerRelationFixture.CustomerBinding,
                    LoadCustomerRelationFixture.CustomerShapeId),
                new JoinQueryNode(
                    LoadCustomerRelationFixture.ExplicitJoinNodeId,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.CustomerSourceNodeId,
                    joinKind,
                    Expr.Eq(
                        Expr.Field(
                            LoadCustomerRelationFixture.LoadBinding,
                            LoadCustomerRelationFixture.LoadCustomerIdPath),
                        Expr.Field(
                            LoadCustomerRelationFixture.CustomerBinding,
                            LoadCustomerRelationFixture.CustomerIdPath))),
                new ProjectQueryNode(
                    LoadCustomerRelationFixture.ProjectionNodeId,
                    LoadCustomerRelationFixture.ExplicitJoinNodeId,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("assign-optional-load-id"),
                            LoadCustomerRelationFixture.SearchCustomerTypePath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadIdPath)),
                        new(
                            LoadCustomerRelationFixture.SearchCustomerNameAssignmentId,
                            LoadCustomerRelationFixture.SearchCustomerNamePath,
                            Expr.Field(
                                LoadCustomerRelationFixture.CustomerBinding,
                                LoadCustomerRelationFixture.CustomerNamePath))
                    ])
            ]),
            [new RowsQueryResultDefinition(
                LoadCustomerRelationFixture.RowsResultId,
                LoadCustomerRelationFixture.ProjectionNodeId)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateExpandQueryDocument(QueryParameterId items)
    {
        var source = new QueryNodeId("expand-source");
        var expand = new QueryNodeId("expand-items");
        var project = new QueryNodeId("expand-project");
        var item = new ValueBindingId("item");
        var itemType = new ScalarTypeRef(ScalarTypeKind.String);
        IRQueryDefinition definition = new(
            new("expand-query"),
            new("ExpandQuery"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(
                        source,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new ExpandCollectionQueryNode(
                        expand,
                        source,
                        Expr.Param(items.Value),
                        item,
                        itemType),
                    new ProjectQueryNode(
                        project,
                        expand,
                        LoadCustomerRelationFixture.SearchBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new(
                                new("assign-expanded-item"),
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Const("expanded"))
                        ])
                ],
                parameters:
                [
                    new QueryParameterDefinition(items, new ArrayTypeRef(itemType))
                ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateDistinctQueryDocument()
    {
        var source = new QueryNodeId("distinct-source");
        var distinct = new QueryNodeId("distinct-status");
        var project = new QueryNodeId("distinct-project");
        IRQueryDefinition definition = new(
            new("distinct-query"),
            new("DistinctQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    source,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new DistinctQueryNode(
                    distinct,
                    source,
                    [
                        Expr.Field(
                            LoadCustomerRelationFixture.LoadBinding,
                            LoadCustomerRelationFixture.LoadStatusPath)
                    ]),
                new ProjectQueryNode(
                    project,
                    distinct,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new("assign-distinct-status"),
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadStatusPath))
                    ])
            ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateOffsetPageQueryDocument(bool useNullableNotes = true)
    {
        var source = new QueryNodeId("offset-source");
        var order = new QueryNodeId("offset-order");
        var page = new QueryNodeId("offset-page");
        IRQueryDefinition definition = new(
            new("offset-query"),
            new("OffsetQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    source,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new OrderQueryNode(
                    order,
                    source,
                    [
                        new QueryOrdering(
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                useNullableNotes
                                    ? LoadCustomerRelationFixture.LoadNotesPath
                                    : LoadCustomerRelationFixture.LoadStatusPath),
                            QuerySortDirection.Descending,
                            useNullableNotes
                                ? QueryNullPlacement.First
                                : QueryNullPlacement.Last)
                    ]),
                new PageQueryNode(page, order, new OffsetPageDefinition(limit: 2, offset: 1))
            ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, page)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateDuplicateKeyRelationDocument()
    {
        IRRelationDefinition definition = new(
            new("duplicate-key-relation"),
            new("DuplicateKeyRelation"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new ProjectQueryNode(
                    LoadCustomerRelationFixture.ProjectionNodeId,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            LoadCustomerRelationFixture.SearchIdAssignmentId,
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Const("duplicate"))
                    ])
            ]),
            LoadCustomerRelationFixture.LoadBinding,
            new(
                LoadCustomerRelationFixture.ProjectionNodeId,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                RelationOutputMode.OnePerRoot,
                Expr.Field(
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.SearchIdPath)));
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateInvariantRelationDocument(string invariantName)
    {
        IRRelationDefinition definition = new(
            new("invariant-relation"),
            new("InvariantRelation"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new ProjectQueryNode(
                    LoadCustomerRelationFixture.ProjectionNodeId,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            LoadCustomerRelationFixture.SearchIdAssignmentId,
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadIdPath))
                    ])
            ]),
            LoadCustomerRelationFixture.LoadBinding,
            new(
                LoadCustomerRelationFixture.ProjectionNodeId,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                RelationOutputMode.OnePerRoot,
                Expr.Field(
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.SearchIdPath)),
            [
                new InvariantDefinition(
                    invariantName,
                    Expr.Eq(
                        Expr.Field(
                            LoadCustomerRelationFixture.SearchBinding,
                            LoadCustomerRelationFixture.SearchIdPath),
                        Expr.Const("allowed")))
            ]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateExpandingRelationDocument(QueryParameterId items)
    {
        var source = new QueryNodeId("expanding-relation-source");
        var expand = new QueryNodeId("expanding-relation-items");
        var project = new QueryNodeId("expanding-relation-project");
        var item = new ValueBindingId("item");
        var itemType = new ScalarTypeRef(ScalarTypeKind.String);
        IRRelationDefinition definition = new(
            new("expanding-relation"),
            new("ExpandingRelation"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(
                        source,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new ExpandCollectionQueryNode(
                        expand,
                        source,
                        Expr.Param(items.Value),
                        item,
                        itemType),
                    new ProjectQueryNode(
                        project,
                        expand,
                        LoadCustomerRelationFixture.SearchBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new(
                                new("assign-relation-item"),
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Const("expanded"))
                        ])
                ],
                parameters:
                [
                    new QueryParameterDefinition(items, new ArrayTypeRef(itemType))
                ]),
            LoadCustomerRelationFixture.LoadBinding,
            new(
                project,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                RelationOutputMode.OnePerRoot));
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreatePartialEvidencePredicateQueryDocument(
        QueryParameterId expected)
    {
        var source = new QueryNodeId("partial-source");
        var filter = new QueryNodeId("partial-filter");
        var project = new QueryNodeId("partial-project");
        var status = Expr.Field(
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadStatusPath);
        var parameter = Expr.Param(expected.Value);
        IRQueryDefinition definition = new(
            new("partial-evidence-query"),
            new("PartialEvidenceQuery"),
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
                        Expr.And(
                            Expr.Eq(status, status),
                            Expr.Eq(parameter, parameter))),
                    new ProjectQueryNode(
                        project,
                        filter,
                        LoadCustomerRelationFixture.SearchBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new(
                                new("assign-partial-id"),
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Const("must-not-pass"))
                        ])
                ],
                parameters:
                [
                    new QueryParameterDefinition(
                        expected,
                        new ScalarTypeRef(ScalarTypeKind.String))
                ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateLazyAvailabilityFilterQueryDocument(
        bool useParameter,
        bool useAnd,
        bool leftValue)
    {
        var source = new QueryNodeId("lazy-source");
        var filter = new QueryNodeId("lazy-filter");
        var project = new QueryNodeId("lazy-project");
        var expected = new QueryParameterId("lazy-expected-status");
        Expr unavailable = useParameter
            ? Expr.Param(expected.Value)
            : Expr.Field(
                LoadCustomerRelationFixture.LoadBinding,
                LoadCustomerRelationFixture.LoadStatusPath);
        var comparison = Expr.Eq(unavailable, Expr.Const("Open"));
        var predicate = useAnd
            ? Expr.And(Expr.Const(leftValue), comparison)
            : Expr.Or(Expr.Const(leftValue), comparison);
        ImmutableArray<QueryParameterDefinition> parameters = useParameter
            ? [new(expected, new ScalarTypeRef(ScalarTypeKind.String))]
            : [];
        IRQueryDefinition definition = new(
            new("lazy-availability-query"),
            new("LazyAvailabilityQuery"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(
                        source,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new FilterQueryNode(filter, source, predicate),
                    new ProjectQueryNode(
                        project,
                        filter,
                        LoadCustomerRelationFixture.SearchBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new(
                                new("assign-lazy-load-id"),
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Field(
                                    LoadCustomerRelationFixture.LoadBinding,
                                    LoadCustomerRelationFixture.LoadIdPath))
                        ])
                ],
                parameters: parameters),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static (RelationQueryRuntimeEvidence Evidence, RelationQueryObservationOccurrence Load)
        CreateLazyAvailabilityEvidence(
            CompiledRelationQueryPlan plan,
            bool unavailableField)
    {
        var load = new RelationQueryObservationOccurrence(
            new("load/lazy"),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            "load-1");
        var source = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>(),
            static input => input.Binding == LoadCustomerRelationFixture.LoadBinding);
        ImmutableArray<RelationQueryFieldEvidence>.Builder fields =
            ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>())
        {
            if (input.Field.Path == LoadCustomerRelationFixture.LoadIdPath)
            {
                fields.Add(new(
                    input.Id,
                    load.Id,
                    RelationQueryFieldEvidenceState.Value,
                    ObservationValue.FromString("load-1")));
            }
            else if (unavailableField
                && input.Field.Path == LoadCustomerRelationFixture.LoadStatusPath)
            {
                fields.Add(new(
                    input.Id,
                    load.Id,
                    RelationQueryFieldEvidenceState.NotLoaded));
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported lazy-availability field input '{input.Field.Path}'.");
            }
        }

        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/lazy-availability-evaluation"),
            plan,
            sources:
            [
                new(
                    source.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [load])
            ],
            fields: fields.ToImmutable(),
            parameters:
            [
                .. plan.RequirementGraph.Inputs
                    .OfType<RelationQueryParameterInput>()
                    .Select(static input => new RelationQueryParameterEvidence(
                        input.Id,
                        RelationQueryParameterEvidenceState.NotProvided))
            ],
            capabilities: AvailableCapabilities(plan));
        return (evidence, load);
    }

    static ExplicitJoinEvidence CreateExplicitJoinEvidence(CompiledRelationQueryPlan plan)
    {
        var load1 = new RelationQueryObservationOccurrence(
            new("load/a"),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            "load-1");
        var load2 = new RelationQueryObservationOccurrence(
            new("load/b"),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            "load-2");
        var customer1 = new RelationQueryObservationOccurrence(
            new("customer/a"),
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerShapeId,
            "customer-1");
        var customer2 = new RelationQueryObservationOccurrence(
            new("customer/b"),
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerShapeId,
            "customer-2");

        ImmutableArray<RelationQuerySourceEvidence>.Builder sources =
            ImmutableArray.CreateBuilder<RelationQuerySourceEvidence>();
        foreach (var source in plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>())
        {
            sources.Add(source.Binding == LoadCustomerRelationFixture.LoadBinding
                ? new(
                    source.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [load2, load1])
                : new(
                    source.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [customer2, customer1]));
        }

        ImmutableArray<RelationQueryFieldEvidence>.Builder fields =
            ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>())
        {
            if (input.Binding == LoadCustomerRelationFixture.LoadBinding)
            {
                var first = input.Field.Path == LoadCustomerRelationFixture.LoadIdPath
                    ? ObservationValue.FromString("load-1")
                    : ObservationValue.FromString("customer-1");
                var second = input.Field.Path == LoadCustomerRelationFixture.LoadIdPath
                    ? ObservationValue.FromString("load-2")
                    : ObservationValue.FromString("customer-missing");
                fields.Add(new(input.Id, load1.Id, RelationQueryFieldEvidenceState.Value, first));
                fields.Add(new(input.Id, load2.Id, RelationQueryFieldEvidenceState.Value, second));
                continue;
            }

            var firstCustomer = input.Field.Path == LoadCustomerRelationFixture.CustomerIdPath
                ? ObservationValue.FromString("customer-1")
                : ObservationValue.FromString("Acme");
            var secondCustomer = input.Field.Path == LoadCustomerRelationFixture.CustomerIdPath
                ? ObservationValue.FromString("customer-2")
                : ObservationValue.FromString("Beta");
            fields.Add(new(input.Id, customer1.Id, RelationQueryFieldEvidenceState.Value, firstCustomer));
            fields.Add(new(input.Id, customer2.Id, RelationQueryFieldEvidenceState.Value, secondCustomer));
        }

        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/explicit-join-evaluation"),
            plan,
            sources: sources.ToImmutable(),
            fields: fields.ToImmutable(),
            capabilities: AvailableCapabilities(plan));
        return new(evidence, load1, load2, customer1, customer2);
    }

    static ImmutableArray<RelationQueryCapabilityEvidence> AvailableCapabilities(
        CompiledRelationQueryPlan plan) =>
    [
        .. plan.RequirementGraph.Inputs
            .OfType<RelationQueryCapabilityInput>()
            .Select(static input => new RelationQueryCapabilityEvidence(
                input.Id,
                RelationQueryCapabilityEvidenceState.Available))
    ];

    static RelationQueryExecutionResult Execute(
        CompiledRelationQueryPlan plan,
        RelationQueryRuntimeEvidence evidence,
        IRelationRequirementGapPolicy? policy = null) =>
        RelationQueryInMemoryInterpreter.Default.Execute(new(plan, evidence, policy));

    static RelationQueryRuntimeEvidence WithConversionFailures(
        CompiledRelationQueryPlan plan,
        RelationQueryRuntimeEvidence evidence,
        params RelationQueryConversionFailureEvidence[] conversionFailures) =>
        new(
            evidence.Evaluation,
            plan,
            evidence.Completeness,
            evidence.Sources,
            evidence.Fields,
            evidence.Traversals,
            evidence.Parameters,
            evidence.Capabilities,
            [.. conversionFailures]);

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

    static CompiledRelationQueryPlan CompileWithShapes(
        RelationQueryDocument document,
        ImmutableArray<ShapeGraphDocument> shapeDocuments,
        RelationQueryCompilationDemand demand)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            shapeDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument,
            demand));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static LoadCustomerEvidence CreateEvidence(
        CompiledRelationQueryPlan plan,
        ImmutableArray<LoadCustomerSpec> specs,
        IReadOnlyDictionary<QueryParameterId, ObservationValue>? parameters = null,
        RelationQueryEvidenceCompleteness completeness = RelationQueryEvidenceCompleteness.Complete)
    {
        Dictionary<string, RelationQueryObservationOccurrence> loads = new(StringComparer.Ordinal);
        Dictionary<string, RelationQueryObservationOccurrence> customers = new(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            loads.Add(
                spec.Key,
                new(
                    new($"load/{spec.Key}"),
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId,
                    spec.LoadId));
            if (spec.IncludeCustomer)
            {
                customers.Add(
                    spec.Key,
                    new(
                        new($"customer/{spec.Key}"),
                        LoadCustomerRelationFixture.CustomerBinding,
                        LoadCustomerRelationFixture.CustomerShapeId,
                        spec.CustomerId));
            }
        }

        var sourceInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>(),
            static input => input.Binding == LoadCustomerRelationFixture.LoadBinding);
        ImmutableArray<RelationQuerySourceEvidence> sources =
        [
            new(
                sourceInput.Id,
                RelationQuerySourceEvidenceState.Provided,
                [.. loads.Values.Reverse()])
        ];

        ImmutableArray<RelationQueryFieldEvidence>.Builder fields = ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>())
        {
            foreach (var spec in specs)
            {
                if (input.Binding == LoadCustomerRelationFixture.LoadBinding)
                {
                    if (input.Field.Path == LoadCustomerRelationFixture.LoadNotesPath)
                    {
                        fields.Add(spec.NotesState == RelationQueryFieldEvidenceState.Value
                            ? new(
                                input.Id,
                                loads[spec.Key].Id,
                                spec.NotesState,
                                ObservationValue.FromString(spec.Notes))
                            : new(
                                input.Id,
                                loads[spec.Key].Id,
                                spec.NotesState));
                    }
                    else
                    {
                        var state = LoadFieldState(spec, input.Field.Path);
                        fields.Add(state == RelationQueryFieldEvidenceState.Value
                            ? new(
                                input.Id,
                                loads[spec.Key].Id,
                                state,
                                LoadFieldValue(spec, input.Field.Path))
                            : new(
                                input.Id,
                                loads[spec.Key].Id,
                                state));
                    }
                    continue;
                }

                if (input.Binding != LoadCustomerRelationFixture.CustomerBinding || !spec.IncludeCustomer)
                    continue;
                if (input.Field.Path != LoadCustomerRelationFixture.CustomerNamePath)
                {
                    throw new InvalidOperationException(
                        $"Unsupported Customer field input '{input.Field.Path}' in the interpreter test fixture.");
                }

                fields.Add(spec.CustomerNameState == RelationQueryFieldEvidenceState.Value
                    ? new(
                        input.Id,
                        customers[spec.Key].Id,
                        spec.CustomerNameState,
                        ObservationValue.FromString(spec.CustomerName))
                    : new(
                        input.Id,
                        customers[spec.Key].Id,
                        spec.CustomerNameState));
            }
        }

        ImmutableArray<RelationQueryTraversalEvidence>.Builder traversals =
            ImmutableArray.CreateBuilder<RelationQueryTraversalEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryRelationshipInput>())
        {
            foreach (var spec in specs)
            {
                ImmutableArray<RelationQueryObservationOccurrence> related =
                    spec.TraversalState == RelationQueryTraversalEvidenceState.Completed && spec.IncludeCustomer
                    ? [customers[spec.Key]]
                    : [];
                traversals.Add(new(
                    input.Id,
                    loads[spec.Key].Id,
                    spec.TraversalState,
                    related,
                    spec.TraversalState == RelationQueryTraversalEvidenceState.Completed
                        ? spec.TraversalCompleteness
                        : RelationQueryEvidenceCompleteness.Partial));
            }
        }

        ImmutableArray<RelationQueryParameterEvidence>.Builder parameterEvidence =
            ImmutableArray.CreateBuilder<RelationQueryParameterEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryParameterInput>())
        {
            if (parameters is null || !parameters.TryGetValue(input.Parameter, out var value))
            {
                throw new InvalidOperationException(
                    $"Interpreter test evidence is missing required parameter '{input.Parameter.Value}'.");
            }
            parameterEvidence.Add(new(
                input.Id,
                RelationQueryParameterEvidenceState.Provided,
                value));
        }

        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/interpreter-evaluation"),
            plan,
            completeness,
            sources,
            fields.ToImmutable(),
            traversals.ToImmutable(),
            parameterEvidence.ToImmutable(),
            AvailableCapabilities(plan));
        return new(evidence, loads, customers);
    }

    static RelationQueryFieldEvidenceState LoadFieldState(
        LoadCustomerSpec spec,
        FieldPath path)
    {
        if (path == LoadCustomerRelationFixture.LoadIdPath)
            return spec.LoadIdState;
        if (path == LoadCustomerRelationFixture.LoadStatusPath)
            return spec.StatusState;
        return RelationQueryFieldEvidenceState.Value;
    }

    static ObservationValue LoadFieldValue(LoadCustomerSpec spec, FieldPath path)
    {
        if (path == LoadCustomerRelationFixture.LoadIdPath)
            return ObservationValue.FromString(spec.LoadId);
        if (path == LoadCustomerRelationFixture.LoadCustomerIdPath)
            return ObservationValue.FromString(spec.CustomerId);
        if (path == LoadCustomerRelationFixture.LoadStatusPath)
            return ObservationValue.FromString(spec.Status);
        if (path == LoadCustomerRelationFixture.LoadAmountPath)
            return ObservationValue.FromDouble(spec.Amount);
        if (path == LoadCustomerRelationFixture.LoadActivePath)
            return ObservationValue.FromBool(spec.Active);
        throw new InvalidOperationException(
            $"Unsupported Load field input '{path}' in the interpreter test fixture.");
    }

    static void AssertRelationRow(
        RelationQueryOutputRow row,
        RelationQueryObservationOccurrence load,
        RelationQueryObservationOccurrence customer,
        string loadId,
        string customerName)
    {
        Assert.Equal(load, row.Root);
        Assert.Equal(ObservationValue.FromString(loadId), row.Identity);
        Assert.True(row.IsComplete);
        Assert.Empty(row.UnresolvedGaps);
        AssertObject(
            row.Value,
            (LoadCustomerRelationFixture.SearchIdFieldName, ObservationValue.FromString(loadId)),
            (LoadCustomerRelationFixture.SearchCustomerNameFieldName, ObservationValue.FromString(customerName)));
        Assert.Equal(
            [customer.Id, load.Id],
            row.InputOccurrences.Select(static occurrence => occurrence.Id));
    }

    static void AssertObject(
        ObservationValue value,
        params (string Name, ObservationValue Value)[] expected)
    {
        Assert.Equal(ObservationValueKind.Object, value.Kind);
        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, ObservationValue>>(value.Fields);
        Assert.Equal(expected.Length, fields.Count);
        foreach (var (name, expectedValue) in expected)
        {
            Assert.True(fields.TryGetValue(name, out var actual), $"Expected output field '{name}'.");
            Assert.Equal(expectedValue, actual);
        }
    }

    static ObservationValue StructuredStop(string location, string type) =>
        ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["Location"] = ObservationValue.FromString(location),
            ["Type"] = ObservationValue.FromString(type)
        });

    sealed record LoadCustomerSpec(
        string Key,
        string LoadId,
        string CustomerId,
        string CustomerName,
        string Status = "Open",
        double Amount = 0d,
        bool Active = true,
        bool IncludeCustomer = true,
        string? Notes = "note",
        RelationQueryFieldEvidenceState NotesState = RelationQueryFieldEvidenceState.Value,
        RelationQueryFieldEvidenceState CustomerNameState = RelationQueryFieldEvidenceState.Value,
        RelationQueryEvidenceCompleteness TraversalCompleteness = RelationQueryEvidenceCompleteness.Complete,
        RelationQueryTraversalEvidenceState TraversalState = RelationQueryTraversalEvidenceState.Completed,
        RelationQueryFieldEvidenceState LoadIdState = RelationQueryFieldEvidenceState.Value,
        RelationQueryFieldEvidenceState StatusState = RelationQueryFieldEvidenceState.Value);

    sealed record LoadCustomerEvidence(
        RelationQueryRuntimeEvidence Evidence,
        IReadOnlyDictionary<string, RelationQueryObservationOccurrence> Loads,
        IReadOnlyDictionary<string, RelationQueryObservationOccurrence> Customers);

    sealed record ExplicitJoinEvidence(
        RelationQueryRuntimeEvidence Evidence,
        RelationQueryObservationOccurrence Load1,
        RelationQueryObservationOccurrence Load2,
        RelationQueryObservationOccurrence Customer1,
        RelationQueryObservationOccurrence Customer2);
}
