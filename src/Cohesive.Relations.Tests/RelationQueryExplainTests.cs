using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Explain;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExplainTests
{
    [Fact]
    public void Static_projection_round_trips_and_excludes_diagnostic_prose_from_fingerprint()
    {
        var compilation = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        var projected = RelationQueryExplainProjector.Project(compilation);
        var staticStage = Assert.IsType<RelationQueryStaticCompilationExplainStage>(Assert.Single(projected.Stages));
        Assert.Null(projected.CapabilitySummary);
        Assert.NotEmpty(staticStage.Plan!.RealizationRequirements);
        Assert.Equal(
            RelationQueryResultObservability.ExactContributors,
            staticStage.Plan.Observability);

        var withFirstMessage = new RelationQueryExplainArtifact(
            RelationQueryExplainArtifact.CurrentSchemaVersion,
            [new RelationQueryStaticCompilationExplainStage(
                RelationQueryExplainStageStatus.Complete,
                staticStage.Request,
                staticStage.Plan,
                [new("TEST-EXPLAIN", DiagnosticSeverity.Warning, "first prose", "/definition")])]);
        var withSecondMessage = new RelationQueryExplainArtifact(
            RelationQueryExplainArtifact.CurrentSchemaVersion,
            [new RelationQueryStaticCompilationExplainStage(
                RelationQueryExplainStageStatus.Complete,
                staticStage.Request,
                staticStage.Plan,
                [new("TEST-EXPLAIN", DiagnosticSeverity.Warning, "second prose", "/definition")])]);

        Assert.Equal(withFirstMessage.Fingerprint, withSecondMessage.Fingerprint);
        var json = RelationQueryExplainJsonSerializer.Serialize(withFirstMessage);
        Assert.DoesNotContain("\"definitionDocument\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"shapeDocuments\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"relationshipCatalogDocument\"", json, StringComparison.Ordinal);
        var restored = RelationQueryExplainJsonSerializer.Deserialize(json);
        Assert.Equal(withFirstMessage.Fingerprint, restored.Fingerprint);
        Assert.Equal(json, RelationQueryExplainJsonSerializer.Serialize(restored));

        var rewrittenProse = json.Replace("first prose", "second prose", StringComparison.Ordinal);
        var restoredRewritten = RelationQueryExplainJsonSerializer.Deserialize(rewrittenProse);
        Assert.Equal(withFirstMessage.Fingerprint, restoredRewritten.Fingerprint);
        Assert.Equal("second prose", Assert.Single(restoredRewritten.Diagnostics).Message);

        const string secretDefault = "static-default-must-not-appear";
        var query = Assert.IsType<QueryDefinition>(LoadCustomerRelationFixture.RepresentativeQueryDocument.Definition);
        var parameters = query.Body.Parameters
            .Select(parameter => parameter.Id == LoadCustomerRelationFixture.CursorParameterId
                ? new QueryParameterDefinition(
                    parameter.Id,
                    parameter.Type,
                    FieldPresence.Optional,
                    ObservationValue.FromString(secretDefault))
                : parameter)
            .ToImmutableArray();
        var secretDocument = RelationQueryDocument.FromDefinition(new QueryDefinition(
            query.Id,
            query.Name,
            new(query.Body.Nodes, parameters),
            query.Results));
        var secretExplainJson = RelationQueryExplainJsonSerializer.Serialize(
            RelationQueryExplainProjector.Project(Compile(secretDocument)));
        Assert.DoesNotContain(secretDefault, secretExplainJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_projection_orders_profile_placement_bound_physical_and_native_stages()
    {
        var fixture = CreateLifecycleFixture();
        var native = CreateNativeExplanation(fixture);

        var artifact = RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            fixture.Bound,
            fixture.Physical,
            native);

        Assert.Collection(
            artifact.Stages,
            static stage => Assert.IsType<RelationQueryStaticCompilationExplainStage>(stage),
            static stage => Assert.IsType<RelationQueryProfileFeasibilityExplainStage>(stage),
            static stage => Assert.IsType<RelationQuerySourcePlacementExplainStage>(stage),
            static stage => Assert.IsType<RelationQueryBoundRealizationExplainStage>(stage),
            static stage => Assert.IsType<RelationQueryPhysicalPlanningExplainStage>(stage),
            static stage => Assert.IsType<RelationQueryNativeCompilationExplainStage>(stage));
        Assert.Equal(fixture.Bound.Fingerprint, artifact.CapabilitySummary?.BoundRealization);

        var json = RelationQueryExplainJsonSerializer.Serialize(artifact);
        var restored = RelationQueryExplainJsonSerializer.Deserialize(json);
        Assert.Equal(artifact.Fingerprint, restored.Fingerprint);
        Assert.True(artifact.CapabilitySummary!.HasSameSemantics(restored.CapabilitySummary));
        Assert.Equal(json, RelationQueryExplainJsonSerializer.Serialize(restored));

        var otherCompilation = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var otherPlan = Assert.IsType<CompiledRelationQueryPlan>(otherCompilation.Plan);
        var otherPlacement = FederatedLoadPhysicalExecutionFixture.CreatePlacement(otherPlan);
        Assert.Throws<ArgumentException>(() => RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            otherPlacement,
            physicalPlanning: fixture.Physical));

        var otherStatic = Assert.IsType<RelationQueryStaticCompilationExplainStage>(
            Assert.Single(RelationQueryExplainProjector.Project(otherCompilation).Stages));
        Assert.Throws<ArgumentException>(() => new RelationQueryStaticCompilationExplainStage(
            RelationQueryExplainStageStatus.Complete,
            otherStatic.Request,
            Assert.IsType<RelationQueryStaticCompilationExplainStage>(artifact.Stages[0]).Plan,
            []));
    }

    [Fact]
    public void Native_failure_retains_attempt_and_rejects_foreign_attempt_or_artifact_binding()
    {
        var fixture = CreateLifecycleFixture();
        var request = new RelationQueryNativeCompilationRequest(fixture.Plan, fixture.Bound, fixture.Placement);
        var failed = new RelationQueryNativeCompilationExplanation(
            RelationQueryNativeCompilationStatus.Unsupported,
            [],
            [new(
                "TEST-NATIVE-UNSUPPORTED",
                DiagnosticSeverity.Error,
                "The test compiler cannot lower this request.",
                branch: request.Branches[0].Id)]);
        var failedStage = RelationQueryNativeCompilationExplainStage.Create(request, failed);
        var artifact = RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            fixture.Bound,
            nativeCompilation: failedStage);

        var restored = RelationQueryExplainJsonSerializer.Deserialize(
            RelationQueryExplainJsonSerializer.Serialize(artifact));
        var restoredNative = Assert.IsType<RelationQueryNativeCompilationExplainStage>(restored.Stages[^1]);
        Assert.Equal(request.PlanReference.DefinitionFingerprint, restoredNative.Attempt.Plan.DefinitionFingerprint);
        Assert.Equal(fixture.Bound.Fingerprint, restoredNative.Attempt.BoundRealization);
        Assert.Empty(restoredNative.Compilation.Artifacts);

        var foreignAttempt = new RelationQueryNativeCompilationAttemptReference(
            failedStage.Attempt.Plan,
            failedStage.Attempt.ProfileFeasibility,
            failedStage.Attempt.BoundRealization,
            failedStage.Attempt.Placement,
            failedStage.Attempt.AdapterBinding,
            [new("query:foreign")]);
        var foreignStage = new RelationQueryNativeCompilationExplainStage(
            RelationQueryExplainStageStatus.Unavailable,
            foreignAttempt,
            failed);
        Assert.Throws<ArgumentException>(() => RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            fixture.Bound,
            nativeCompilation: foreignStage));

        var successful = CreateNativeExplanation(fixture);
        var original = successful.Compilation.Artifacts[0];
        var alternateBinding = new RelationQueryAdapterBindingReference(
            original.Provenance.AdapterBinding.SchemaVersion,
            original.Provenance.AdapterBinding.BindingId,
            original.Provenance.AdapterBinding.Target,
            original.Provenance.AdapterBinding.TargetProfile,
            new("sha256", "tests/foreign-binding/v1", new string('f', 64)),
            original.Provenance.AdapterBinding.CompiledPlanFingerprint,
            original.Provenance.AdapterBinding.PlacementFingerprint,
            original.Provenance.AdapterBinding.Sources,
            original.Provenance.AdapterBinding.PlacementBindings,
            original.Provenance.AdapterBinding.ConfigurationDecisions);
        var foreignProvenance = new RelationQueryNativeCompilationProvenance(
            original.Provenance.Plan,
            original.Provenance.Branch,
            original.Provenance.Target,
            original.Provenance.TargetProfile,
            original.Provenance.Realization,
            original.Provenance.BoundRealization,
            original.Provenance.Placement,
            alternateBinding,
            original.Provenance.ContextEvidence,
            original.Provenance.CompilerProfile,
            original.Provenance.ConventionSetVersion,
            original.Provenance.CoveredNodes,
            original.Provenance.CoveredAssignments,
            original.Provenance.InputFields,
            original.Provenance.RealizationDecisions);
        var foreignArtifact = new RelationQueryNativeArtifactReference(
            original.Branch,
            original.ArtifactSchemaVersion,
            original.Fingerprint,
            foreignProvenance);
        var foreignArtifacts = successful.Compilation.Artifacts.SetItem(0, foreignArtifact);
        var foreignCompilation = new RelationQueryNativeCompilationExplanation(
            RelationQueryNativeCompilationStatus.Exact,
            foreignArtifacts,
            []);
        var foreignArtifactStage = new RelationQueryNativeCompilationExplainStage(
            RelationQueryExplainStageStatus.Complete,
            successful.Attempt,
            foreignCompilation);
        Assert.Throws<ArgumentException>(() => RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            fixture.Bound,
            nativeCompilation: foreignArtifactStage));

        var foreignContextProvenance = new RelationQueryNativeCompilationProvenance(
            original.Provenance.Plan,
            original.Provenance.Branch,
            original.Provenance.Target,
            original.Provenance.TargetProfile,
            original.Provenance.Realization,
            original.Provenance.BoundRealization,
            original.Provenance.Placement,
            original.Provenance.AdapterBinding,
            [new("context:foreign")],
            original.Provenance.CompilerProfile,
            original.Provenance.ConventionSetVersion,
            original.Provenance.CoveredNodes,
            original.Provenance.CoveredAssignments,
            original.Provenance.InputFields,
            original.Provenance.RealizationDecisions);
        var foreignContextArtifact = new RelationQueryNativeArtifactReference(
            original.Branch,
            original.ArtifactSchemaVersion,
            original.Fingerprint,
            foreignContextProvenance);
        var foreignContextCompilation = new RelationQueryNativeCompilationExplanation(
            RelationQueryNativeCompilationStatus.Exact,
            successful.Compilation.Artifacts.SetItem(0, foreignContextArtifact),
            []);
        var foreignContextStage = new RelationQueryNativeCompilationExplainStage(
            RelationQueryExplainStageStatus.Complete,
            successful.Attempt,
            foreignContextCompilation);
        Assert.Throws<ArgumentException>(() => RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            fixture.Bound,
            nativeCompilation: foreignContextStage));
    }

    [Fact]
    public async Task Evaluation_projection_summarizes_results_without_runtime_payload_or_identity()
    {
        var evaluation = LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new("tests/explain-evaluation-secret"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Supply(
            [
                new Observation(
                    LoadCustomerRelationFixture.LoadShapeLocalId,
                    "load-secret",
                    new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                    {
                        [LoadCustomerRelationFixture.LoadIdFieldName] = ObservationValue.FromString("load-secret"),
                        [LoadCustomerRelationFixture.LoadCustomerIdFieldName] = ObservationValue.FromString("customer-secret")
                    })
            ],
            evidenceReference: "evidence-secret")
            .Build();
        var compilation = RelationQueryStaticCompiler.Compile(evaluation.Compilation);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var placement = FederatedLoadPhysicalExecutionFixture.CreatePlacement(plan);
        var customerSource = placement.SourceInstances.Single(
            static source => source.Id == FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var customerReader = new DeterministicRelationQuerySourceReader(
            new(customerSource.Id, customerSource.ExecutionDomain, customerSource.TargetProfile),
            [DeterministicRelationQuerySourceReader.SourceRow.Create(
                "customer-secret",
                (LoadCustomerRelationFixture.CustomerIdPath, ObservationValue.FromString("customer-secret")),
                (LoadCustomerRelationFixture.CustomerNamePath, ObservationValue.FromString("Secret Customer")),
                (LoadCustomerRelationFixture.CustomerTypePath, ObservationValue.FromString("Secret Type")))]);
        RelationQueryEvaluator evaluator = new(
            static compiledPlan => FederatedLoadPhysicalExecutionFixture.CreatePlacement(compiledPlan),
            FederatedLoadPhysicalExecutionFixture.CreatePolicy(),
            [customerReader]);

        var outcome = await evaluator.EvaluateAsync(evaluation);
        var artifact = RelationQueryExplainProjector.Project(outcome);
        var stage = Assert.IsType<RelationQueryEvaluationExplainStage>(artifact.Stages[^1]);
        var summary = Assert.Single(stage.Evaluation.Results);
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, stage.Evaluation.Status);
        Assert.Equal(1, summary.RowCount);
        Assert.Empty(stage.Evaluation.RequirementGaps);

        var json = RelationQueryExplainJsonSerializer.Serialize(artifact);
        Assert.DoesNotContain("load-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("customer-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Customer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tests/explain-evaluation-secret", json, StringComparison.Ordinal);
        var restored = RelationQueryExplainJsonSerializer.Deserialize(json);
        Assert.Equal(artifact.Fingerprint, restored.Fingerprint);
        Assert.Equal(json, RelationQueryExplainJsonSerializer.Serialize(restored));

        var changedResults = stage.Evaluation.Results.SetItem(
            0,
            new(
                summary.Branch,
                summary.Kind,
                summary.Shape,
                summary.State,
                summary.RowCount + 1));
        var changedEvaluation = new RelationQueryEvaluationExplanation(
            stage.Evaluation.Evaluation,
            stage.Evaluation.Plan,
            stage.Evaluation.Status,
            changedResults,
            stage.Evaluation.RequirementGaps,
            stage.Evaluation.Diagnostics);
        var changedArtifact = new RelationQueryExplainArtifact(
            RelationQueryExplainArtifact.CurrentSchemaVersion,
            artifact.Stages.SetItem(
                artifact.Stages.Length - 1,
                new RelationQueryEvaluationExplainStage(
                    RelationQueryExplainStageStatus.Complete,
                    changedEvaluation)));
        Assert.Equal(artifact.Fingerprint, changedArtifact.Fingerprint);
        Assert.NotEqual(
            stage.Evaluation.ObservationFingerprint,
            changedEvaluation.ObservationFingerprint);

        var tampered = json.Replace("\"rowCount\": 1", "\"rowCount\": 2", StringComparison.Ordinal);
        Assert.NotEqual(json, tampered);
        Assert.Throws<System.Text.Json.JsonException>(() => RelationQueryExplainJsonSerializer.Deserialize(tampered));
    }

    [Fact]
    public void Evaluation_attribution_rejects_foreign_result_gap_and_diagnostic_identities()
    {
        var fixture = CreateLifecycleFixture();
        var evaluationRequest = LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new("tests/explain-affinity"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Build();
        var reference = RelationQueryCompiledPlanReference.From(fixture.Plan);
        var branch = Assert.Single(RelationQueryNativeCompilationRequest.CreateBranches(fixture.Plan.ExecutionSlice));

        var foreignResult = new RelationQueryEvaluationExplanation(
            evaluationRequest.Fingerprint,
            reference,
            RelationQueryExecutionStatus.Succeeded,
            [new(
                branch.Id,
                RelationQueryExecutionResultKind.Rows,
                LoadCustomerRelationFixture.CustomerShapeId,
                RelationQueryExecutionOutputState.Complete,
                0)],
            []);
        Assert.Throws<ArgumentException>(() => RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            physicalPlanning: fixture.Physical,
            evaluation: foreignResult));

        var foreignKind = new RelationQueryEvaluationExplanation(
            evaluationRequest.Fingerprint,
            reference,
            RelationQueryExecutionStatus.Succeeded,
            [new(
                branch.Id,
                RelationQueryExecutionResultKind.Aggregation,
                branch.Shape,
                RelationQueryExecutionOutputState.Complete,
                0)],
            []);
        Assert.Throws<ArgumentException>(() => RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            physicalPlanning: fixture.Physical,
            evaluation: foreignKind));

        var input = fixture.Plan.RequirementGraph.Inputs[0].Id;
        var foreignOutputGap = new RelationQueryEvaluationExplanation(
            evaluationRequest.Fingerprint,
            reference,
            RelationQueryExecutionStatus.Incomplete,
            [new(
                branch.Id,
                RelationQueryExecutionResultKind.Rows,
                branch.Shape,
                RelationQueryExecutionOutputState.Incomplete,
                0)],
            [new(
                RelationRequirementGapCause.InputNotProvided,
                input,
                [new("output:foreign")],
                1,
                [RelationRequirementGapResolutionKind.ProvideInput])]);
        Assert.Throws<ArgumentException>(() => RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            physicalPlanning: fixture.Physical,
            evaluation: foreignOutputGap));

        var foreignInputGap = new RelationQueryEvaluationExplanation(
            evaluationRequest.Fingerprint,
            reference,
            RelationQueryExecutionStatus.Incomplete,
            [new(
                branch.Id,
                RelationQueryExecutionResultKind.Rows,
                branch.Shape,
                RelationQueryExecutionOutputState.Incomplete,
                0)],
            [new(
                RelationRequirementGapCause.InputNotProvided,
                new("input:foreign"),
                [branch.Outputs[0].Id],
                1,
                [RelationRequirementGapResolutionKind.ProvideInput])]);
        Assert.Throws<ArgumentException>(() => RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            physicalPlanning: fixture.Physical,
            evaluation: foreignInputGap));

        RelationQueryExplainDiagnostic[] foreignDiagnostics =
        [
            new(
                RelationQueryExplainStageWireNames.Evaluation,
                "TEST-EVALUATION-FOREIGN-NODE",
                DiagnosticSeverity.Error,
                "The test diagnostic cites a foreign node.",
                node: new("node:foreign")),
            new(
                RelationQueryExplainStageWireNames.Evaluation,
                "TEST-EVALUATION-FOREIGN-INPUT",
                DiagnosticSeverity.Error,
                "The test diagnostic cites a foreign input.",
                input: new("input:foreign")),
            new(
                RelationQueryExplainStageWireNames.Evaluation,
                "TEST-EVALUATION-FOREIGN-OUTPUT",
                DiagnosticSeverity.Error,
                "The test diagnostic cites a foreign output.",
                output: new("output:foreign")),
            new(
                RelationQueryExplainStageWireNames.Evaluation,
                "TEST-EVALUATION-FOREIGN-REQUIREMENT",
                DiagnosticSeverity.Error,
                "The test diagnostic cites a foreign requirement.",
                requirement: new("requirement:foreign")),
            new(
                RelationQueryExplainStageWireNames.Evaluation,
                "TEST-EVALUATION-FOREIGN-STAGE",
                DiagnosticSeverity.Error,
                "The test diagnostic cites a foreign physical stage.",
                physicalStage: new("stage:foreign")),
            new(
                RelationQueryExplainStageWireNames.Evaluation,
                "TEST-EVALUATION-FOREIGN-SOURCE",
                DiagnosticSeverity.Error,
                "The test diagnostic cites a foreign source.",
                source: new("source:foreign"))
        ];
        foreach (var diagnostic in foreignDiagnostics)
        {
            var foreignDiagnostic = new RelationQueryEvaluationExplanation(
                evaluationRequest.Fingerprint,
                reference,
                RelationQueryExecutionStatus.Failed,
                [],
                [],
                [diagnostic]);
            Assert.Throws<ArgumentException>(() => RelationQueryExplainProjector.Project(
                fixture.Compilation,
                fixture.Realization,
                fixture.Placement,
                physicalPlanning: fixture.Physical,
                evaluation: foreignDiagnostic));
        }
    }

    [Fact]
    public void Bound_unavailable_explain_retains_primary_and_prerequisite_blocked_context()
    {
        var fixture = CreateLifecycleFixture();
        var request = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var failure = new RelationQueryContextualBranchFailure(
            RelationQueryBoundAssessmentStatus.Unavailable,
            RelationQueryUnavailableReason.CapabilityNotAdvertised,
            new("TEST-EXPLAIN-BOUND-UNAVAILABLE"),
            "The test binding cannot realize the selected branch.",
            "Bind the branch to a source that preserves the required capability.");
        var assessments = RelationQueryContextualAssessmentProjector.Project(
            request,
            "tests/explain-bound-unavailable",
            _ => failure,
            static (_, requirement, _) => new(
                RelationQueryConfigurationValueOrigin.AdapterConvention,
                "tests/explain-bound-unavailable/v1",
                node: requirement.Origin?.Node,
                input: requirement.Origin?.Input));
        var bound = RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(CreateBinding(request), assessments));
        Assert.False(bound.IsRealizable);
        var primary = Assert.Single(bound.Evidence.Assessments, static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Unavailable);
        var blocked = bound.Evidence.Assessments.Where(static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Blocked).ToImmutableArray();
        Assert.NotEmpty(blocked);
        Assert.All(blocked, assessment =>
        {
            Assert.Equal(primary.Id, assessment.BlockedBy);
            Assert.Equal(RelationQueryUnavailableReason.PrerequisiteBlocked, assessment.UnavailableReason);
        });

        var evaluationRequest = LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new("tests/explain-bound-unavailable"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Build();
        var evaluation = new RelationQueryEvaluationExplanation(
            evaluationRequest.Fingerprint,
            RelationQueryCompiledPlanReference.From(fixture.Plan),
            RelationQueryExecutionStatus.Failed,
            [],
            []);
        var artifact = RelationQueryExplainProjector.Project(
            fixture.Compilation,
            fixture.Realization,
            fixture.Placement,
            bound,
            evaluation: evaluation);

        Assert.Collection(
            artifact.Stages,
            static stage => Assert.IsType<RelationQueryStaticCompilationExplainStage>(stage),
            static stage => Assert.IsType<RelationQueryProfileFeasibilityExplainStage>(stage),
            static stage => Assert.IsType<RelationQuerySourcePlacementExplainStage>(stage),
            stage => Assert.Equal(RelationQueryExplainStageStatus.Unavailable, stage.Status),
            stage => Assert.Equal(RelationQueryExplainStageStatus.Failed, stage.Status));
        Assert.Equal(bound.Fingerprint, artifact.CapabilitySummary?.BoundRealization);
        var contextualDiagnostic = Assert.Single(artifact.Diagnostics, diagnostic =>
            diagnostic.Stage == RelationQueryExplainStageWireNames.BoundRealization
            && diagnostic.AdapterDecisionCode == failure.AdapterDecisionCode
            && diagnostic.ContextEvidence == primary.Id);
        Assert.Equal(failure.Resolution, contextualDiagnostic.Resolution);
        Assert.Equal(RelationQueryConfigurationValueOrigin.AdapterConvention, contextualDiagnostic.ConfigurationOrigin);
        Assert.Equal("tests/explain-bound-unavailable/v1", contextualDiagnostic.ConfigurationAuthority);
        Assert.Equal(
            artifact.Fingerprint,
            RelationQueryExplainJsonSerializer.Deserialize(
                RelationQueryExplainJsonSerializer.Serialize(artifact)).Fingerprint);
    }

    [Fact]
    public void Evaluation_projection_retains_failed_terminal_stage_at_static_profile_and_physical_boundaries()
    {
        var invalidRequest = new RelationQueryCompilationRequest(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            [
                LoadCustomerRelationFixture.DomainShapeGraphDocument,
                LoadCustomerRelationFixture.DomainShapeGraphDocument,
                LoadCustomerRelationFixture.DtoShapeGraphDocument
            ],
            LoadCustomerRelationFixture.RelationshipCatalogDocument);
        var invalidEvaluation = new RelationQueryEvaluation(
            RelationQueryEvaluation.CurrentSchemaVersion,
            invalidRequest,
            new("tests/explain-static-failure"),
            []);
        var invalidCompilation = RelationQueryStaticCompiler.Compile(invalidRequest);
        Assert.False(invalidCompilation.IsSuccessful);
        var staticFailure = RelationQueryExplainProjector.Project(
            new RelationQueryEvaluationOutcome(invalidEvaluation, invalidCompilation));
        Assert.Collection(
            staticFailure.Stages,
            static stage => Assert.Equal(RelationQueryExplainStageStatus.Invalid, stage.Status),
            static stage =>
            {
                var evaluation = Assert.IsType<RelationQueryEvaluationExplainStage>(stage);
                Assert.Equal(RelationQueryExplainStageStatus.Failed, evaluation.Status);
                Assert.Null(evaluation.Evaluation.Plan);
            });
        Assert.Null(staticFailure.CapabilitySummary);
        Assert.Equal(
            staticFailure.Fingerprint,
            RelationQueryExplainJsonSerializer.Deserialize(
                RelationQueryExplainJsonSerializer.Serialize(staticFailure)).Fingerprint);

        var evaluationRequest = LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new("tests/explain-later-failure"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Build();
        var compilation = RelationQueryStaticCompiler.Compile(evaluationRequest.Compilation);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var reference = RelationQueryCompiledPlanReference.From(plan);
        var unsupportedProfile = new RelationQueryTargetCapabilityProfile(
            new("tests/explain-unsupported"),
            new("tests/explain-unsupported/v1"),
            [reference.DefinitionSchemaVersion],
            [reference.CompilerProfile]);
        var unavailable = RelationQueryRealizationCompiler.Compile(
            plan,
            unsupportedProfile,
            new(new("tests/explain-policy/v1"), "tests/explain-conventions/v1"));
        Assert.False(unavailable.IsRealizable);
        var profileFailure = RelationQueryExplainProjector.Project(
            new RelationQueryEvaluationOutcome(evaluationRequest, compilation, unavailable));
        Assert.Collection(
            profileFailure.Stages,
            static stage => Assert.IsType<RelationQueryStaticCompilationExplainStage>(stage),
            static stage => Assert.Equal(RelationQueryExplainStageStatus.Unavailable, stage.Status),
            static stage => Assert.Equal(RelationQueryExplainStageStatus.Failed, stage.Status));

        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var placement = FederatedLoadPhysicalExecutionFixture.CreatePlacement(plan);
        var physicalFailure = new RelationQueryPhysicalPlanningResult(
            RelationQueryPhysicalPlanningStatus.Invalid,
            plan: null,
            [new(
                RelationQueryPhysicalPlanningDiagnosticCodes.PolicyInvalid,
                DiagnosticSeverity.Error,
                "The test planning policy is invalid.")]);
        var physicalArtifact = RelationQueryExplainProjector.Project(
            new RelationQueryEvaluationOutcome(
                evaluationRequest,
                compilation,
                realization,
                placement,
                physicalFailure));
        Assert.Collection(
            physicalArtifact.Stages,
            static stage => Assert.IsType<RelationQueryStaticCompilationExplainStage>(stage),
            static stage => Assert.IsType<RelationQueryProfileFeasibilityExplainStage>(stage),
            static stage => Assert.IsType<RelationQuerySourcePlacementExplainStage>(stage),
            static stage => Assert.Equal(RelationQueryExplainStageStatus.Invalid, stage.Status),
            static stage => Assert.Equal(RelationQueryExplainStageStatus.Failed, stage.Status));
    }

    static LifecycleFixture CreateLifecycleFixture()
    {
        var compilation = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var placement = FederatedLoadPhysicalExecutionFixture.CreatePlacement(plan);
        var bound = Bind(new(plan, realization, placement));
        var physical = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            placement,
            FederatedLoadPhysicalExecutionFixture.CreatePolicy());
        Assert.True(physical.IsSuccessful);
        return new(compilation, plan, realization, placement, bound, physical);
    }

    static RelationQueryNativeCompilationExplainStage CreateNativeExplanation(LifecycleFixture fixture)
    {
        var request = new RelationQueryNativeCompilationRequest(
            fixture.Plan,
            fixture.Bound,
            fixture.Placement);
        ImmutableArray<RelationQueryNativeArtifactReference>.Builder artifacts =
            ImmutableArray.CreateBuilder<RelationQueryNativeArtifactReference>(request.Branches.Length);
        foreach (var branch in request.Branches)
        {
            var selection = request.Selection.GetBranch(branch.Id);
            var provenance = RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                branch.Id,
                "tests/explain-native/v1",
                "tests/explain-native-conventions/v1",
                selection.ReachableNodes,
                [],
                [.. selection.Fields.Select(static field => field.Input.Id)]);
            artifacts.Add(new(
                branch.Id,
                "tests/native-artifact/v1",
                new(
                    "sha256",
                    "tests/native-artifact/v1-c14n/v1",
                    new string((char)('a' + artifacts.Count), 64)),
                provenance));
        }
        return RelationQueryNativeCompilationExplainStage.Create(
            request,
            new(RelationQueryNativeCompilationStatus.Exact, artifacts.MoveToImmutable(), []));
    }

    static RelationQueryBoundRealizationReport Bind(RelationQueryBoundRealizationRequest request)
    {
        const string authority = "tests/explain-binding/v1";
        var binding = CreateBinding(request);
        var decisions = request.ProfileFeasibility.Decisions.ToDictionary(static decision => decision.Requirement);
        ImmutableArray<RelationQueryBoundRequirementAssessment>.Builder assessments =
            ImmutableArray.CreateBuilder<RelationQueryBoundRequirementAssessment>();
        foreach (var branch in request.Branches)
        {
            foreach (var requirement in request.GetRequirementsForBranch(branch))
            {
                var decision = decisions[requirement.Id];
                assessments.Add(new(
                    new($"context/{Uri.EscapeDataString(branch.Id.Value)}/{Uri.EscapeDataString(requirement.Id.Value)}"),
                    branch.Id,
                    requirement.Id,
                    RelationQueryBoundAssessmentStatus.Available,
                    RelationQueryConfigurationValueOrigin.AdapterConvention,
                    authority,
                    decision.GetCapabilityEvidence(),
                    decision.GetTargetEnforcedBoundaries(),
                    decision.GetPreservedGuarantees(),
                    message: "Exact test binding evidence is available."));
            }
        }
        return RelationQueryBoundRealizationCompiler.Compile(
            request,
            new(binding, assessments.ToImmutable()));
    }

    static RelationQueryAdapterBindingReference CreateBinding(RelationQueryBoundRealizationRequest request) =>
        new(
            "tests/explain-binding/v1",
            "binding/explain-tests",
            request.ProfileFeasibility.TargetProfile.Target,
            request.ProfileFeasibility.TargetProfile.Id,
            new("sha256", "tests/explain-binding/v1-c14n/v1", new string('b', 64)),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.PlanReference),
            request.Placement.Fingerprint,
            [.. request.Placement.SourceInstances.Select(static source => source.Id)],
            [.. request.Placement.Bindings.Select(static placement => placement.Id)]);

    static RelationQueryCompilationResult Compile(RelationQueryDocument document)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return result;
    }

    sealed record LifecycleFixture(
        RelationQueryCompilationResult Compilation,
        CompiledRelationQueryPlan Plan,
        RelationQueryRealizationReport Realization,
        RelationQuerySourcePlacement Placement,
        RelationQueryBoundRealizationReport Bound,
        RelationQueryPhysicalPlanningResult Physical);
}
