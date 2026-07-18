using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Adapters.Cosmos;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Tests.Model;

public sealed class CosmosRelationQueryCompilerTests
{
    [Fact]
    public void Compile_RowQuery_ProducesExactReusableArtifact()
    {
        var fixture = Fixture.Row();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        Assert.Equal(RelationQueryNativeCompilationStatus.Exact, result.Status);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c "
            + "WHERE (c[\"Status\"] = @p0) ORDER BY c[\"Id\"] ASC OFFSET 5 LIMIT 25",
            artifact.Statement.Text);
        Assert.Equal(["Id", "Status"], artifact.SelectedFields.Select(FieldPathText));
        Assert.Equal(["f0", "f1"], artifact.ResultFields.Select(static field => field.Alias));
        Assert.Equal(
            [CosmosRelationQueryResultValueEncoding.JsonString, CosmosRelationQueryResultValueEncoding.JsonString],
            artifact.ResultFields.Select(static field => field.Encoding));
        Assert.All(artifact.ResultFields, static field =>
            Assert.Equal(
                new ScalarTypeRef(ScalarTypeKind.String),
                field.ValueContract.GetEffectiveType()));
        Assert.Equal(new QueryParameterId("status"), Assert.Single(artifact.Parameters).Parameter);
        Assert.Equal(new CosmosRelationQueryPagingContract(5, 25, Fixture.IdPath), artifact.Paging);
        Assert.Equal(fixture.PlanReference, artifact.Provenance.Plan);
        Assert.Equal(fixture.Realization.Fingerprint, artifact.Provenance.Realization);
        Assert.Equal(fixture.Placement.Fingerprint, artifact.Provenance.Placement);
        Assert.NotEmpty(artifact.Provenance.RealizationDecisions);
        Assert.NotEmpty(artifact.Provenance.CapabilityEvidence);
        Assert.NotEmpty(artifact.Provenance.CoveredNodes);
        Assert.Equal(
            artifact.SelectedFields.Select(static field => field.Input).OrderBy(static input => input.Value),
            artifact.Provenance.InputFields.OrderBy(static input => input.Value));
    }

    [Fact]
    public void Compile_Aggregation_EmitsGroupedCountAndMinimumWithResultBindings()
    {
        var fixture = Fixture.Aggregation();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(RelationQueryNativeResultKind.QueryAggregation, artifact.Branch.Kind);
        Assert.Equal(
            "SELECT COUNT(1) AS f0, MIN(c[\"Amount\"]) AS f1, c[\"Status\"] AS f2 "
            + "FROM c GROUP BY c[\"Status\"]",
            artifact.Statement.Text);
        Assert.Equal(["Amount", "Status"], artifact.SelectedFields.Select(FieldPathText));
        Assert.Equal(["Count", "Total", "Status"], artifact.ResultFields.Select(field => field.Field.Path.ToString()));
        Assert.Equal(
            [
                CosmosRelationQueryResultValueEncoding.ExactCountInteger,
                CosmosRelationQueryResultValueEncoding.JsonInt32,
                CosmosRelationQueryResultValueEncoding.JsonString
            ],
            artifact.ResultFields.Select(static field => field.Encoding));
        Assert.Equal(
            new ScalarTypeRef(ScalarTypeKind.Int64),
            artifact.ResultFields[0].ValueContract.GetEffectiveType());
        Assert.Equal(3, artifact.Provenance.CoveredAssignments.Length);
        Assert.Null(artifact.Paging);
    }

    [Fact]
    public void Compile_GroupedMaximum_UsesExactInt32AggregateEncoding()
    {
        var result = Fixture.Aggregation(AggregateOperator.Max).Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("MAX(c[\"Amount\"]) AS f1", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(
            CosmosRelationQueryResultValueEncoding.JsonInt32,
            artifact.ResultFields[1].Encoding);
    }

    [Fact]
    public void Compile_RowCountWithoutExactInputBound_FailsClosed()
    {
        var fixture = Fixture.Aggregation();

        var result = fixture.Compile(fixture.StorageBindingWithMaximumInputRows(null));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported);
        Assert.Contains("maximumInputRows", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_Sum_FailsClosedForInexactNumericAccumulation()
    {
        var result = Fixture.Aggregation(AggregateOperator.Sum).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported);
        Assert.Contains("decimal SUM", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_RelationRows_FailsClosedUntilRootSemanticsAreRepresented()
    {
        var fixture = Fixture.Relation();

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.RelationTerminalUnsupported);
        Assert.Contains("root correlation", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Branch);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_RepeatedAndReorderedEquivalentBindings_AreDeterministic()
    {
        var fixture = Fixture.Row();
        var reversedBinding = fixture.StorageBindingWithFields(
            [.. fixture.StorageBinding.Fields.Reverse()]);

        var first = Assert.Single(fixture.Compile().Artifacts);
        var second = Assert.Single(fixture.Compile(reversedBinding).Artifacts);

        Assert.Equal(first.Statement.Text, second.Statement.Text);
        Assert.Equal(first.Statement.Parameters.ToArray(), second.Statement.Parameters.ToArray());
        Assert.Equal(first.StorageBinding.Fingerprint, second.StorageBinding.Fingerprint);
        Assert.Equal(first.Provenance.Plan, second.Provenance.Plan);
        Assert.Equal(first.Provenance.Realization, second.Provenance.Realization);
        Assert.Equal(first.Provenance.Placement, second.Provenance.Placement);
        Assert.Equal(first.Provenance.CoveredNodes.ToArray(), second.Provenance.CoveredNodes.ToArray());
        Assert.Equal(first.Provenance.CoveredAssignments.ToArray(), second.Provenance.CoveredAssignments.ToArray());
        Assert.Equal(first.Provenance.InputFields.ToArray(), second.Provenance.InputFields.ToArray());
        Assert.Equal(
            first.Provenance.RealizationDecisions.Select(DecisionKey),
            second.Provenance.RealizationDecisions.Select(DecisionKey));
        Assert.Equal(first.Provenance.CapabilityEvidence.ToArray(), second.Provenance.CapabilityEvidence.ToArray());
        Assert.Equal(first.Provenance.OperatingBoundaries.ToArray(), second.Provenance.OperatingBoundaries.ToArray());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void NativeCompilationProvenance_RejectsUnknownCoveredNode()
    {
        var fixture = Fixture.Row();
        var request = new RelationQueryNativeCompilationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var branch = Assert.Single(request.Branches);

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                branch.Id,
                "tests/cosmos/compiler-v1",
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                [new QueryNodeId("unknown-node")],
                [],
                []));

        Assert.Equal("coveredNodes", exception.ParamName);
        Assert.Contains("reachable by the selected branch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCompilationProvenance_RejectsCoveredNodeFromAnotherBranch()
    {
        var fixture = Fixture.IndependentBranches();
        var request = new RelationQueryNativeCompilationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var selectedBranch = Assert.Single(request.Branches, static branch => branch.QueryResult == new QueryResultId("rows"));
        var unrelatedBranch = Assert.Single(request.Branches, branch => branch.Id != selectedBranch.Id);

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                selectedBranch.Id,
                "tests/cosmos/compiler-v1",
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                [unrelatedBranch.Node],
                [],
                []));

        Assert.Equal("coveredNodes", exception.ParamName);
        Assert.Contains("reachable by the selected branch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCompilationProvenance_RejectsAssignmentOutsideCoveredNodes()
    {
        var fixture = Fixture.Row();
        var request = new RelationQueryNativeCompilationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var branch = Assert.Single(request.Branches);
        var projectionNode = Assert.Single(
            fixture.Plan.ExecutionSlice.Nodes,
            static node => !node.ProjectionAssignments.IsDefaultOrEmpty);
        var assignment = projectionNode.ProjectionAssignments[0].Definition.Id;
        var coveredNode = Assert.Single(
            fixture.Plan.ExecutionSlice.Nodes,
            static node => node.CanonicalNode is SourceQueryNode);

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                branch.Id,
                "tests/cosmos/compiler-v1",
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                [coveredNode.Id],
                [assignment],
                []));

        Assert.Equal("coveredAssignments", exception.ParamName);
        Assert.Contains("belonging to covered branch nodes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCompilationProvenance_RejectsPlanInputReadOnlyByAnotherBranch()
    {
        var fixture = Fixture.IndependentBranches();
        var request = new RelationQueryNativeCompilationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var selectedBranch = Assert.Single(request.Branches, static branch => branch.QueryResult == new QueryResultId("rows"));
        var selectedOutputs = selectedBranch.Outputs.Select(static output => output.Id).ToHashSet();
        var unrelatedInput = Assert.Single(
            fixture.Plan.InputContract.Sources.SelectMany(static source => source.Fields),
            field => field.Uses.All(use => !selectedOutputs.Contains(use.Output.Id)));

        var exception = Assert.Throws<ArgumentException>(() =>
            RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                selectedBranch.Id,
                "tests/cosmos/compiler-v1",
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                [selectedBranch.Node],
                [],
                [unrelatedInput.Input.Id]));

        Assert.Equal("inputFields", exception.ParamName);
        Assert.Contains("read by the selected branch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_RuntimeParameter_ReusesTemplateAndRejectsInexactInvocations()
    {
        var artifact = Assert.Single(Fixture.Row().Compile().Artifacts);

        var first = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("ready")
        });
        var second = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("closed")
        });

        Assert.Equal(artifact.Statement.Text, first.Text);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal("ready", Assert.Single(first.Parameters).Value);
        Assert.Equal("closed", Assert.Single(second.Parameters).Value);
        Assert.NotNull(first.ToQueryDefinition());
        Assert.Throws<ArgumentException>(() => artifact.Bind(
            new Dictionary<QueryParameterId, ObservationValue>()));
        Assert.Throws<ArgumentException>(() => artifact.Bind(
            new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromInt64(42)
            }));
        Assert.Throws<ArgumentException>(() => artifact.Bind(
            new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.Undefined
            }));
        Assert.Throws<ArgumentException>(() => artifact.Bind(
            new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready"),
                [new("unknown")] = ObservationValue.FromString("value")
            }));
    }

    [Fact]
    public void Compile_StaleRealizationOrPlacement_IsInvalidBeforeLowering()
    {
        var current = Fixture.Row(offset: 5);
        var changed = Fixture.Row(offset: 6);

        var staleRealization = current.Compile(
            request: new(changed.Plan, current.Realization, changed.Placement));
        var stalePlacement = current.Compile(
            request: new(current.Plan, current.Realization, changed.Placement));

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, staleRealization.Status);
        Assert.Contains(staleRealization.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryNativeCompilationDiagnosticCodes.RealizationPlanMismatch);
        Assert.Empty(staleRealization.Artifacts);
        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, stalePlacement.Status);
        Assert.Contains(stalePlacement.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryNativeCompilationDiagnosticCodes.PlacementPlanMismatch);
        Assert.Empty(stalePlacement.Artifacts);
    }

    [Fact]
    public void Compile_ExplicitIdBindingAffinityRejectsReuseAcrossAlignedPlanAndPlacementSnapshots()
    {
        var current = Fixture.Row(offset: 5);
        var changedPlan = Fixture.Row(offset: 6);
        var verified = current.StorageBindingWithAffinity();
        var changedPlacement = new RelationQuerySourcePlacement(
            current.Placement.SchemaVersion,
            current.Placement.Plan,
            current.Placement.ConventionSetVersion + "/changed",
            current.Placement.SourceInstances,
            current.Placement.Bindings);

        var planReuse = current.Compile(
            verified,
            new(changedPlan.Plan, changedPlan.Realization, changedPlan.Placement));
        var placementReuse = current.Compile(
            verified,
            new(current.Plan, current.Realization, changedPlacement));

        Assert.Equal(current.StorageBinding.Id, verified.Id);
        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, planReuse.Status);
        Assert.Contains(planReuse.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            && diagnostic.Message.Contains("compiled-plan affinity", StringComparison.Ordinal));
        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, placementReuse.Status);
        Assert.Contains(placementReuse.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            && diagnostic.Message.Contains("source-placement affinity", StringComparison.Ordinal));
        Assert.Empty(planReuse.Artifacts);
        Assert.Empty(placementReuse.Artifacts);
    }

    [Fact]
    public void Compile_UnrealizableButAlignedInputs_AreUnsupportedRatherThanInvalid()
    {
        var fixture = Fixture.Row(keyset: true, overrideUnavailableRequirements: true);
        var unrealizable = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.False(unrealizable.IsRealizable);

        var result = fixture.Compile(
            request: new(fixture.Plan, unrealizable, fixture.Placement));

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryNativeCompilationDiagnosticCodes.RealizationUnavailable);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_MismatchedStorageBinding_IsInvalidAndAttributable()
    {
        var fixture = Fixture.Row();
        var mismatched = fixture.StorageBindingWithTarget(new("different-target"));

        var result = fixture.Compile(mismatched);

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_CrossSourceJoin_IsRejectedByTheSingleContainerBoundary()
    {
        var fixture = Fixture.CrossSourceJoin();

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            && diagnostic.Message.Contains("exactly one source contract", StringComparison.Ordinal));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_KeysetPage_IsRejectedWithStructuredOperatorDiagnostic()
    {
        var fixture = Fixture.Row(keyset: true, overrideUnavailableRequirements: true);

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator);
        Assert.Contains("offset paging only", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Branch);
        Assert.NotNull(diagnostic.Node);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_OptionalNullablePredicate_IsRejectedWithoutClaimingExactMissingNullSemantics()
    {
        var fixture = Fixture.Row(optionalPredicate: true);

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains("may be missing or null", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_DefaultExactContributorObservability_IsRejectedByNativeCompiler()
    {
        var fixture = Fixture.Row();
        var realization = fixture.RealizeExactContributors();

        var result = fixture.Compile(
            request: new(fixture.Plan, realization, fixture.Placement));

        Assert.True(realization.IsRealizable);
        Assert.Equal(
            RelationQueryOccurrenceProvenanceMode.ExactContributors,
            realization.Observability.OccurrenceProvenance);
        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.ResultObservabilityUnsupported);
        Assert.Contains("contributor-occurrence lineage", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_WholeRowDistinct_RetainsUndemandedProjectionFieldsInSqlShape()
    {
        var result = Fixture.DistinctSelectedId().Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT DISTINCT c[\"Id\"] AS f0, c[\"Status\"] AS __distinct0 FROM c",
            artifact.Statement.Text);
        Assert.Equal(["Id", "Status"], artifact.SelectedFields.Select(FieldPathText));
        Assert.Equal("Id", Assert.Single(artifact.ResultFields).Field.Path.ToString());
    }

    [Fact]
    public void Compile_ExpansionFieldWithExplicitItemBinding_UsesJoinAlias()
    {
        var result = Fixture.ExpandedItemField().Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, j0[\"Name\"] AS f1 FROM c JOIN j0 IN c[\"Items\"]",
            artifact.Statement.Text);
        Assert.Equal(
            [CosmosRelationQueryResultValueEncoding.JsonString, CosmosRelationQueryResultValueEncoding.JsonString],
            artifact.ResultFields.Select(static field => field.Encoding));
    }

    [Theory]
    [InlineData(ScalarTypeKind.Instant)]
    [InlineData(ScalarTypeKind.DateTime)]
    public void Compile_TemporalOrdering_FailsClosedWhenPhysicalOrderIsNotProven(
        ScalarTypeKind temporalKind)
    {
        var result = Fixture.TemporalOrdering(temporalKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains("ordering", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("temporal ordering is not exact", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(AggregateOperator.Min)]
    [InlineData(AggregateOperator.Max)]
    public void Compile_NonNumericMinimumOrMaximum_FailsClosed(AggregateOperator operation)
    {
        var result = Fixture.NonNumericAggregation(operation).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported);
        Assert.Contains("known Int32 value", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_DeterministicSqlAndFingerprint_AreCultureIndependent()
    {
        var fixture = Fixture.Row();
        CosmosRelationQueryCompiledArtifact first;
        CosmosRelationQueryCompiledArtifact second;
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            first = Assert.Single(fixture.Compile().Artifacts);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            second = Assert.Single(fixture.Compile().Artifacts);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        Assert.Equal(first.Statement.Text, second.Statement.Text);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Compile_ArtifactFingerprint_ChangesWithCompilerProfileOrStorageBinding()
    {
        var fixture = Fixture.Row();
        var baseline = Assert.Single(fixture.Compile().Artifacts);
        var changedCompiler = Assert.Single(fixture.Compile(
            options: new(
                compilerProfile: "tests/cosmos/compiler-v2",
                conventionSetVersion: CosmosRelationQueryStorageBinding.SemanticPathConventionSet)).Artifacts);
        var changedBinding = Assert.Single(fixture.Compile(
            fixture.StorageBindingWithContainer("loads-v2")).Artifacts);

        Assert.Equal(baseline.Statement.Text, changedCompiler.Statement.Text);
        Assert.NotEqual(baseline.Fingerprint, changedCompiler.Fingerprint);
        Assert.Equal(baseline.Statement.Text, changedBinding.Statement.Text);
        Assert.NotEqual(baseline.StorageBinding.Fingerprint, changedBinding.StorageBinding.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, changedBinding.Fingerprint);
    }

    [Fact]
    public void Compile_UnpagedOrderBy_RejectsNonUniqueFinalKey()
    {
        var fixture = Fixture.OrderingByStatus();
        var binding = fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [Fixture.IdPath],
            exactOrderingPaths: [Fixture.IdPath, Fixture.StatusPath]);

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains("final stable unique source path", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_UnpagedOrderBy_AcceptsExplicitlyProvenUniqueFinalKey()
    {
        var fixture = Fixture.OrderingByStatus();
        var binding = fixture.StorageBindingWithOrderingProofs(
            stableUniqueOrderingPaths: [Fixture.IdPath, Fixture.StatusPath],
            exactOrderingPaths: [Fixture.IdPath, Fixture.StatusPath]);

        var result = fixture.Compile(binding);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c ORDER BY c[\"Status\"] ASC",
            artifact.Statement.Text);
        Assert.Null(artifact.Paging);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Int64)]
    [InlineData(ScalarTypeKind.Decimal)]
    public void Compile_PrecisionUnsafeNumericComparison_FailsClosed(ScalarTypeKind numericKind)
    {
        var result = Fixture.PrecisionUnsafeComparison(numericKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression);
        Assert.Contains("proven exact Cosmos JSON value domain", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Int64)]
    [InlineData(ScalarTypeKind.Decimal)]
    public void Compile_PrecisionUnsafeNumericOrdering_FailsClosed(ScalarTypeKind numericKind)
    {
        var result = Fixture.PrecisionUnsafeOrdering(numericKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains("wider numeric", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Int64)]
    [InlineData(ScalarTypeKind.Decimal)]
    public void Compile_PrecisionUnsafeNumericResult_FailsClosed(ScalarTypeKind numericKind)
    {
        var result = Fixture.PrecisionUnsafeProjection(numericKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains("physical result encoding", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_BytesRuntimeParameter_IsRejectedBeforeArtifactConstruction()
    {
        var result = Fixture.BytesParameterProjection().Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.ParameterUnsupported);
        Assert.Contains("does not have a Cosmos SQL v1 parameter encoding", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void CanonicalDocument_UndefinedConstant_IsRejectedBeforeNativeArtifactConstruction()
    {
        var exception = Assert.Throws<ArgumentException>(Fixture.UndefinedConstantProjection);

        Assert.Contains("relationQuery.value.kindUnsupported", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Undefined", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalDocument_BytesConstant_IsRejectedBeforeNativeArtifactConstruction()
    {
        var exception = Assert.Throws<ArgumentException>(Fixture.BytesConstantProjection);

        Assert.Contains("relationQuery.value.kindUnsupported", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Bytes", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_KeyedDistinct_FailsClosedAsUnsupportedLogicalOperator()
    {
        var result = Fixture.DistinctSelectedId(keyed: true).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator);
        Assert.Contains("explicit distinct keys are unsupported", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData(UnsafeDistinctDomain.NullableString)]
    [InlineData(UnsafeDistinctDomain.NestedArray)]
    [InlineData(UnsafeDistinctDomain.Int64)]
    [InlineData(UnsafeDistinctDomain.Decimal)]
    public void Compile_WholeRowDistinct_RejectsInexactEqualityDomains(UnsafeDistinctDomain domain)
    {
        var result = Fixture.UnsafeDistinct(domain).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains("DISTINCT assignment", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("exact scalar equality domain", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_ContainsExactStringArray_EmitsArrayContains()
    {
        var result = Fixture.ContainsFilter(ScalarTypeKind.String).Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "SELECT c[\"Id\"] AS f0, c[\"Status\"] AS f1 FROM c "
            + "WHERE ARRAY_CONTAINS(@p0, c[\"Status\"])",
            artifact.Statement.Text);
        Assert.Equal(new QueryParameterId("contains-values"), Assert.Single(artifact.Parameters).Parameter);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Int64)]
    [InlineData(ScalarTypeKind.Decimal)]
    public void Compile_ContainsPrecisionUnsafeDomain_FailsClosed(ScalarTypeKind numericKind)
    {
        var result = Fixture.ContainsFilter(numericKind).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression);
        Assert.Contains("proven exact scalar equality domain", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    static string FieldPathText(CosmosRelationQuerySelectedField field) => field.DocumentPath.ToString();

    static string DecisionKey(RelationQueryNativeCompilationDecisionReference decision) => string.Join(
        "|",
        decision.Requirement.Value,
        decision.Kind,
        string.Join(",", decision.CapabilityEvidence.Select(static evidence => evidence.Value)),
        string.Join(",", decision.CompositionRules.Select(static rule => rule.Value)),
        decision.Override?.Value ?? string.Empty,
        string.Join(",", decision.OperatingBoundaries.Select(static boundary => boundary.Value)),
        string.Join(",", decision.PreservedGuarantees));

    static string Diagnostics(CosmosRelationQueryCompilationResult result) => string.Join(
        Environment.NewLine,
        result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    public enum UnsafeDistinctDomain
    {
        NullableString,
        NestedArray,
        Int64,
        Decimal
    }

    sealed class Fixture
    {
        static readonly GraphId Graph = new("cosmos-compiler-tests/v1");
        static readonly QualifiedShapeId LoadShape = new(Graph, new ShapeId("Load"));
        static readonly QualifiedShapeId CustomerShape = new(Graph, new ShapeId("Customer"));
        static readonly QualifiedShapeId RowShape = new(Graph, new ShapeId("LoadRow"));
        static readonly QualifiedShapeId InstantRowShape = new(Graph, new ShapeId("LoadInstantRow"));
        static readonly QualifiedShapeId DateTimeRowShape = new(Graph, new ShapeId("LoadDateTimeRow"));
        static readonly QualifiedShapeId Int64RowShape = new(Graph, new ShapeId("LoadInt64Row"));
        static readonly QualifiedShapeId DecimalRowShape = new(Graph, new ShapeId("LoadDecimalRow"));
        static readonly QualifiedShapeId NullableRowShape = new(Graph, new ShapeId("LoadNullableRow"));
        static readonly QualifiedShapeId NestedRowShape = new(Graph, new ShapeId("LoadNestedRow"));
        static readonly QualifiedShapeId BytesRowShape = new(Graph, new ShapeId("LoadBytesRow"));
        static readonly QualifiedShapeId UndefinedRowShape = new(Graph, new ShapeId("LoadUndefinedRow"));
        static readonly QualifiedShapeId AggregateShape = new(Graph, new ShapeId("LoadAggregate"));
        static readonly QualifiedShapeId StringAggregateShape = new(Graph, new ShapeId("LoadStringAggregate"));
        static readonly ValueBindingId Load = new("load");
        static readonly ValueBindingId Customer = new("customer");
        static readonly ValueBindingId RowBinding = new("row");
        static readonly ValueBindingId AggregateBinding = new("aggregate");
        static readonly ValueBindingId ExpandedItemBinding = new("expanded-item");
        static readonly QueryNodeId LoadSource = new("loads");
        static readonly QueryNodeId CustomerSource = new("customers");
        static readonly QueryNodeId Filter = new("status-filter");
        static readonly QueryNodeId Project = new("project-row");
        static readonly QueryNodeId Order = new("order-row");
        static readonly QueryNodeId Page = new("page-row");
        static readonly QueryNodeId Aggregate = new("aggregate-loads");
        static readonly QueryResultId Rows = new("rows");
        static readonly QueryResultId CustomerRows = new("customer-rows");
        static readonly QueryResultId Aggregations = new("aggregations");
        static readonly QueryParameterId StatusParameter = new("status");
        static readonly QueryParameterId NumericParameter = new("numeric-value");
        static readonly QueryParameterId BytesParameter = new("bytes-value");
        static readonly QueryParameterId ContainsValuesParameter = new("contains-values");

        public static readonly FieldPath IdPath = FieldPath.FromField("Id");
        static readonly FieldPath CustomerIdPath = FieldPath.FromField("CustomerId");
        public static readonly FieldPath StatusPath = FieldPath.FromField("Status");
        static readonly FieldPath AmountPath = FieldPath.FromField("Amount");
        static readonly FieldPath NotesPath = FieldPath.FromField("Notes");
        static readonly FieldPath Int64ValuePath = FieldPath.FromField("Int64Value");
        static readonly FieldPath DecimalValuePath = FieldPath.FromField("DecimalValue");
        static readonly FieldPath TagsPath = FieldPath.FromField("Tags");
        static readonly FieldPath ItemsPath = FieldPath.FromField("Items");
        static readonly FieldPath NamePath = FieldPath.FromField("Name");
        static readonly FieldPath ValuePath = FieldPath.FromField("Value");
        static readonly FieldPath PayloadPath = FieldPath.FromField("Payload");
        static readonly FieldPath ObservedInstantPath = FieldPath.FromField("ObservedInstant");
        static readonly FieldPath ObservedDateTimePath = FieldPath.FromField("ObservedDateTime");
        static readonly FieldPath OccurredAtPath = FieldPath.FromField("OccurredAt");
        static readonly FieldPath CountPath = FieldPath.FromField("Count");
        static readonly FieldPath TotalPath = FieldPath.FromField("Total");
        static readonly FieldPath MinimumStatusPath = FieldPath.FromField("MinimumStatus");

        Fixture(
            CompiledRelationQueryPlan plan,
            RelationQueryRealizationReport realization,
            RelationQuerySourcePlacement placement,
            CosmosRelationQueryStorageBinding storageBinding)
        {
            Plan = plan;
            Realization = realization;
            Placement = placement;
            StorageBinding = storageBinding;
        }

        public CompiledRelationQueryPlan Plan { get; }

        public RelationQueryCompiledPlanReference PlanReference => RelationQueryCompiledPlanReference.From(Plan);

        public RelationQueryRealizationReport Realization { get; }

        public RelationQuerySourcePlacement Placement { get; }

        public CosmosRelationQueryStorageBinding StorageBinding { get; }

        public CosmosRelationQueryCompilationResult Compile(
            CosmosRelationQueryStorageBinding? storageBinding = null,
            RelationQueryNativeCompilationRequest? request = null,
            CosmosRelationQueryCompilerOptions? options = null) =>
            new CosmosRelationQueryCompiler(options).Compile(
                request ?? new(Plan, Realization, Placement),
                storageBinding ?? StorageBinding);

        public CosmosRelationQueryStorageBinding StorageBindingWithAffinity() => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            StorageBinding.Target,
            StorageBinding.TargetProfile,
            StorageBinding.ContainerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            StorageBinding.StableUniqueOrderingPaths,
            StorageBinding.ExactOrderingPaths,
            StorageBinding.MaximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion,
            StorageBinding.ConfigurationDecisions,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(PlanReference),
            Placement.Fingerprint);

        public RelationQueryRealizationReport RealizeExactContributors() => Realize(
            Plan,
            overrideUnavailableRequirements: true,
            observability: RelationQueryResultObservability.ExactContributors);

        public CosmosRelationQueryStorageBinding StorageBindingWithFields(
            ImmutableArray<CosmosRelationQueryFieldBinding> fields) => new(
                StorageBinding.Id,
                StorageBinding.Source,
                StorageBinding.PlacementBinding,
                StorageBinding.Target,
                StorageBinding.TargetProfile,
                StorageBinding.ContainerName,
                StorageBinding.RootAlias,
                StorageBinding.IdentityPath,
                fields,
                StorageBinding.DocumentRoot,
                StorageBinding.PartitionPath,
                StorageBinding.StableUniqueOrderingPaths,
                StorageBinding.ExactOrderingPaths,
                StorageBinding.MaximumInputRows,
                StorageBinding.MissingValueEncoding,
                StorageBinding.NullValueEncoding,
                StorageBinding.Origin,
                StorageBinding.ConventionSetVersion);

        public CosmosRelationQueryStorageBinding StorageBindingWithTarget(RelationQueryTargetId target) => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            target,
            StorageBinding.TargetProfile,
            StorageBinding.ContainerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            StorageBinding.StableUniqueOrderingPaths,
            StorageBinding.ExactOrderingPaths,
            StorageBinding.MaximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion);

        public CosmosRelationQueryStorageBinding StorageBindingWithContainer(string containerName) => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            StorageBinding.Target,
            StorageBinding.TargetProfile,
            containerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            StorageBinding.StableUniqueOrderingPaths,
            StorageBinding.ExactOrderingPaths,
            StorageBinding.MaximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion);

        public CosmosRelationQueryStorageBinding StorageBindingWithOrderingProofs(
            ImmutableArray<FieldPath> stableUniqueOrderingPaths,
            ImmutableArray<FieldPath> exactOrderingPaths) => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            StorageBinding.Target,
            StorageBinding.TargetProfile,
            StorageBinding.ContainerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            stableUniqueOrderingPaths,
            exactOrderingPaths,
            StorageBinding.MaximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion);

        public CosmosRelationQueryStorageBinding StorageBindingWithMaximumInputRows(
            long? maximumInputRows) => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            StorageBinding.Target,
            StorageBinding.TargetProfile,
            StorageBinding.ContainerName,
            StorageBinding.RootAlias,
            StorageBinding.IdentityPath,
            StorageBinding.Fields,
            StorageBinding.DocumentRoot,
            StorageBinding.PartitionPath,
            StorageBinding.StableUniqueOrderingPaths,
            StorageBinding.ExactOrderingPaths,
            maximumInputRows,
            StorageBinding.MissingValueEncoding,
            StorageBinding.NullValueEncoding,
            StorageBinding.Origin,
            StorageBinding.ConventionSetVersion);

        public static Fixture Row(
            int offset = 5,
            bool keyset = false,
            bool optionalPredicate = false,
            bool overrideUnavailableRequirements = false)
        {
            QueryPageDefinition page = keyset
                ? new KeysetPageDefinition(25, [Expr.Param("cursor")])
                : new OffsetPageDefinition(25, offset);
            List<QueryParameterDefinition> parameters =
            [
                new(StatusParameter, new ScalarTypeRef(ScalarTypeKind.String))
            ];
            if (keyset)
            {
                parameters.Add(new(new("cursor"), new ScalarTypeRef(ScalarTypeKind.String)));
            }

            IRQueryDefinition definition = new(
                new("row-query"),
                new("RowQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Eq(
                                Expr.Field(Load, optionalPredicate ? NotesPath : StatusPath),
                                Expr.Param(StatusParameter.Value))),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ]),
                        new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, IdPath))]),
                        new PageQueryNode(Page, Order, page)
                    ],
                    parameters: [.. parameters]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: overrideUnavailableRequirements);
        }

        public static Fixture IndependentBranches()
        {
            IRQueryDefinition definition = new(
                new("independent-branches-query"),
                new("IndependentBranchesQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ]),
                    new SourceQueryNode(CustomerSource, Customer, CustomerShape)
                ]),
                [
                    new RowsQueryResultDefinition(Rows, Project),
                    new RowsQueryResultDefinition(CustomerRows, CustomerSource)
                ]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture OrderingByStatus()
        {
            IRQueryDefinition definition = new(
                new("status-ordering-query"),
                new("StatusOrderingQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ]),
                    new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, StatusPath))])
                ]),
                [new RowsQueryResultDefinition(Rows, Order)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture PrecisionUnsafeComparison(ScalarTypeKind numericKind)
        {
            var (sourcePath, scalarType) = PrecisionUnsafeNumeric(numericKind);
            IRQueryDefinition definition = new(
                new($"{numericKind}-comparison-query"),
                new($"{numericKind}ComparisonQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Eq(
                                Expr.Field(Load, sourcePath),
                                Expr.Param(NumericParameter.Value))),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ])
                    ],
                    parameters: [new(NumericParameter, scalarType)]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture PrecisionUnsafeOrdering(ScalarTypeKind numericKind)
        {
            var (sourcePath, scalarType) = PrecisionUnsafeNumeric(numericKind);
            var rowShape = numericKind == ScalarTypeKind.Int64 ? Int64RowShape : DecimalRowShape;
            IRQueryDefinition definition = new(
                new($"{numericKind}-ordering-query"),
                new($"{numericKind}OrderingQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-value"), ValuePath, Expr.Field(Load, sourcePath))
                        ]),
                    new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, ValuePath))])
                ]),
                [new RowsQueryResultDefinition(Rows, Order)]);
            var demand = RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    Rows,
                    [new RelationQueryFieldReference(rowShape, IdPath)])
            ]);
            return Create(RelationQueryDocument.FromDefinition(definition), demand: demand);
        }

        public static Fixture PrecisionUnsafeProjection(ScalarTypeKind numericKind)
        {
            var (sourcePath, _) = PrecisionUnsafeNumeric(numericKind);
            var rowShape = numericKind == ScalarTypeKind.Int64 ? Int64RowShape : DecimalRowShape;
            IRQueryDefinition definition = new(
                new($"{numericKind}-result-query"),
                new($"{numericKind}ResultQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-value"), ValuePath, Expr.Field(Load, sourcePath))
                        ])
                ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        public static Fixture BytesParameterProjection()
        {
            IRQueryDefinition definition = new(
                new("bytes-parameter-query"),
                new("BytesParameterQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new ProjectQueryNode(
                            Project,
                            LoadSource,
                            RowBinding,
                            BytesRowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-payload"), PayloadPath, Expr.Param(BytesParameter.Value))
                            ])
                    ],
                    parameters:
                    [
                        new(BytesParameter, new ScalarTypeRef(ScalarTypeKind.Bytes))
                    ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture UndefinedConstantProjection() => ConstantProjection(
            "undefined-constant-query",
            UndefinedRowShape,
            ValuePath,
            Expr.Const(ObservationValue.Undefined));

        public static Fixture BytesConstantProjection() => ConstantProjection(
            "bytes-constant-query",
            BytesRowShape,
            PayloadPath,
            Expr.Const(ObservationValue.FromBytes(new byte[] { 1, 2, 3 })));

        static Fixture ConstantProjection(
            string queryId,
            QualifiedShapeId rowShape,
            FieldPath target,
            Expr value)
        {
            IRQueryDefinition definition = new(
                new(queryId),
                new(queryId),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [new(new("constant-value"), target, value)])
                ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture DistinctSelectedId(bool keyed = false)
        {
            var distinct = new QueryNodeId("distinct-row");
            ImmutableArray<Expr> keys = keyed ? [Expr.Field(RowBinding, IdPath)] : [];
            IRQueryDefinition definition = new(
                new("distinct-row-query"),
                new("DistinctRowQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ]),
                    new DistinctQueryNode(distinct, Project, keys)
                ]),
                [new RowsQueryResultDefinition(Rows, distinct)]);
            var demand = RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    Rows,
                    [new RelationQueryFieldReference(RowShape, IdPath)])
            ]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: keyed,
                demand: demand);
        }

        public static Fixture ExpandedItemField()
        {
            var expand = new QueryNodeId("expand-items");
            var itemType = new ObjectTypeRef(
            [
                new ObjectFieldTypeDef("Name", new ScalarTypeRef(ScalarTypeKind.String))
            ]);
            IRQueryDefinition definition = new(
                new("expanded-item-query"),
                new("ExpandedItemQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ExpandCollectionQueryNode(
                        expand,
                        LoadSource,
                        Expr.Field(Load, ItemsPath),
                        ExpandedItemBinding,
                        itemType),
                    new ProjectQueryNode(
                        Project,
                        expand,
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(ExpandedItemBinding, NamePath))
                        ])
                ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        public static Fixture UnsafeDistinct(UnsafeDistinctDomain domain)
        {
            var (rowShape, value) = domain switch
            {
                UnsafeDistinctDomain.NullableString =>
                    (NullableRowShape, Expr.Field(Load, NotesPath)),
                UnsafeDistinctDomain.NestedArray =>
                    (NestedRowShape, Expr.Field(Load, TagsPath)),
                UnsafeDistinctDomain.Int64 =>
                    (Int64RowShape, Expr.Field(Load, Int64ValuePath)),
                UnsafeDistinctDomain.Decimal =>
                    (DecimalRowShape, Expr.Field(Load, DecimalValuePath)),
                _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unsupported DISTINCT fixture domain.")
            };
            var distinct = new QueryNodeId("unsafe-distinct-row");
            IRQueryDefinition definition = new(
                new($"{domain}-distinct-query"),
                new($"{domain}DistinctQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-value"), ValuePath, value)
                        ]),
                    new DistinctQueryNode(distinct, Project)
                ]),
                [new RowsQueryResultDefinition(Rows, distinct)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture ContainsFilter(ScalarTypeKind elementKind)
        {
            var (sourcePath, scalarType) = elementKind switch
            {
                ScalarTypeKind.String => (StatusPath, (TypeRef)new ScalarTypeRef(ScalarTypeKind.String)),
                ScalarTypeKind.Int64 or ScalarTypeKind.Decimal => PrecisionUnsafeNumeric(elementKind),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(elementKind),
                    elementKind,
                    "The contains fixture supports string, Int64, and Decimal values.")
            };
            IRQueryDefinition definition = new(
                new($"{elementKind}-contains-query"),
                new($"{elementKind}ContainsQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Call(
                                ExprFunctionNames.Contains,
                                Expr.Param(ContainsValuesParameter.Value),
                                Expr.Field(Load, sourcePath))),
                        new ProjectQueryNode(
                            Project,
                            Filter,
                            RowBinding,
                            RowShape,
                            [
                                new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                                new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ])
                    ],
                    parameters:
                    [
                        new(ContainsValuesParameter, new ArrayTypeRef(scalarType))
                    ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        static (FieldPath Path, ScalarTypeRef Type) PrecisionUnsafeNumeric(ScalarTypeKind numericKind) =>
            numericKind switch
            {
                ScalarTypeKind.Int64 => (Int64ValuePath, new(ScalarTypeKind.Int64)),
                ScalarTypeKind.Decimal => (DecimalValuePath, new(ScalarTypeKind.Decimal)),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(numericKind),
                    numericKind,
                    "The precision-unsafe fixture supports Int64 and Decimal values only.")
            };

        public static Fixture TemporalOrdering(ScalarTypeKind temporalKind)
        {
            var (sourcePath, rowShape) = temporalKind switch
            {
                ScalarTypeKind.Instant => (ObservedInstantPath, InstantRowShape),
                ScalarTypeKind.DateTime => (ObservedDateTimePath, DateTimeRowShape),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(temporalKind),
                    temporalKind,
                    "The fixture supports instant and date-time ordering only.")
            };
            IRQueryDefinition definition = new(
                new($"{temporalKind}-ordering-query"),
                new($"{temporalKind}OrderingQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        rowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-occurred-at"), OccurredAtPath, Expr.Field(Load, sourcePath))
                        ]),
                    new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, OccurredAtPath))])
                ]),
                [new RowsQueryResultDefinition(Rows, Order)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture Aggregation(AggregateOperator operation = AggregateOperator.Min)
        {
            IRQueryDefinition definition = new(
                new("aggregate-query"),
                new("AggregateQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new AggregateQueryNode(
                        Aggregate,
                        LoadSource,
                        AggregateBinding,
                        AggregateShape,
                        groupings:
                        [
                            new(new("group-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ],
                        aggregates:
                        [
                            new(new("count-loads"), CountPath, AggregateOperator.Count),
                            new(new("numeric-amount"), TotalPath, operation, Expr.Field(Load, AmountPath))
                        ])
                ]),
                [new AggregationQueryResultDefinition(Aggregations, Aggregate)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: operation == AggregateOperator.Sum);
        }

        public static Fixture NonNumericAggregation(AggregateOperator operation)
        {
            IRQueryDefinition definition = new(
                new($"string-{operation}-query"),
                new($"String{operation}Query"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new AggregateQueryNode(
                        Aggregate,
                        LoadSource,
                        AggregateBinding,
                        StringAggregateShape,
                        groupings:
                        [
                            new(new("group-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ],
                        aggregates:
                        [
                            new(
                                new("string-extreme"),
                                MinimumStatusPath,
                                operation,
                                Expr.Field(Load, StatusPath))
                        ])
                ]),
                [new AggregationQueryResultDefinition(Aggregations, Aggregate)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture Relation()
        {
            IRRelationDefinition definition = new(
                new("load-relation"),
                new("LoadRelation"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ])
                ]),
                Load,
                new(Project, RowShape, RelationOutputMode.OnePerRoot, Expr.Field(RowBinding, IdPath)));
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        public static Fixture CrossSourceJoin()
        {
            IRQueryDefinition definition = new(
                new("cross-source-query"),
                new("CrossSourceQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new SourceQueryNode(CustomerSource, Customer, CustomerShape),
                    new JoinQueryNode(
                        new("join-customer"),
                        LoadSource,
                        CustomerSource,
                        JoinKind.Inner,
                        Expr.Eq(Expr.Field(Load, CustomerIdPath), Expr.Field(Customer, IdPath))),
                    new ProjectQueryNode(
                        Project,
                        new("join-customer"),
                        RowBinding,
                        RowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-status"), StatusPath, Expr.Field(Load, StatusPath))
                        ])
                ]),
                [new RowsQueryResultDefinition(Rows, Project)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        static Fixture Create(
            RelationQueryDocument document,
            bool overrideUnavailableRequirements = false,
            RelationQueryCompilationDemand? demand = null)
        {
            var compilation = RelationQueryStaticCompiler.Compile(new(
                document,
                [ShapeDocument()],
                demand: demand));
            Assert.True(
                compilation.IsSuccessful,
                string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
            var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
            var realization = Realize(plan, overrideUnavailableRequirements);
            Assert.True(realization.IsRealizable, string.Join(
                Environment.NewLine,
                realization.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            var placement = CreatePlacement(plan);
            var sourcePlacement = placement.Bindings.First(static binding =>
                binding.Kind == RelationQuerySourcePlacementBindingKind.SourceSet);
            var storage = CosmosRelationQueryStorageBinding.FromSemanticPathConvention(
                new("tests/cosmos-binding/v1"),
                sourcePlacement,
                CosmosRelationQueryTargetProfile.Target,
                CosmosRelationQueryTargetProfile.ProfileId,
                "loads",
                IdPath,
                stableUniqueOrderingPaths: [IdPath],
                exactOrderingPaths: [IdPath],
                maximumInputRows: 10_000);
            return new(plan, realization, placement, storage);
        }

        static RelationQueryRealizationReport Realize(
            CompiledRelationQueryPlan plan,
            bool overrideUnavailableRequirements,
            RelationQueryResultObservability? observability = null)
        {
            var effectiveObservability = observability ?? RelationQueryResultObservability.NotRequested;
            var baseline = RelationQueryRealizationCompiler.Compile(
                plan,
                CosmosRelationQueryTargetProfile.Default,
                CosmosRelationQueryTargetProfile.Policy,
                effectiveObservability);
            if (!overrideUnavailableRequirements || baseline.IsRealizable)
            {
                return baseline;
            }

            var requirements = baseline.Requirements.ToDictionary(static requirement => requirement.Id);
            ImmutableArray<RelationQueryRealizationOverride> overrides =
            [
                .. baseline.Decisions
                    .OfType<UnavailableRelationQueryRealizationDecision>()
                    .Select((decision, index) => new RelationQueryRealizationOverride(
                        new($"tests/cosmos-unsupported-override/{index:D4}"),
                        decision.Requirement,
                        requirements[decision.Requirement].Capability,
                        preservedGuarantees: requirements[decision.Requirement].RequiredGuarantees,
                        justification: "Exercise the Cosmos compiler's fail-closed unsupported diagnostic."))
            ];
            var policy = new RelationQueryRealizationPolicy(
                new("tests/cosmos-unsupported-policy/v1"),
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                constrainedRealizations: RelationQueryConstrainedRealizationPolicy.AllowValidated,
                overrides: overrides);
            return RelationQueryRealizationCompiler.Compile(
                plan,
                CosmosRelationQueryTargetProfile.Default,
                policy,
                effectiveObservability);
        }

        static RelationQuerySourcePlacement CreatePlacement(CompiledRelationQueryPlan plan)
        {
            ImmutableArray<RelationQuerySourcePlacementBinding> bindings =
            [
                .. plan.InputContract.Sources.Select(source => new RelationQuerySourcePlacementBinding(
                    new($"placement/{source.Binding.Value}"),
                    source.Input.Id,
                    source.Node,
                    source.Binding,
                    source.Shape,
                    new($"source/{source.Binding.Value}"),
                    RelationQuerySourcePlacementBindingKind.SourceSet,
                    RelationQuerySourceAcquisitionKind.BoundedEnumeration,
                    RelationQuerySourcePlacementOrigin.Explicit,
                    new(source.Shape, "Id"),
                    [
                        .. source.Fields.Select(static field => new RelationQuerySourceFieldBinding(
                            field.Input.Id,
                            field.Input.Field.Path,
                            field.Input.Field.Path.ToString()))
                    ]))
            ];
            ImmutableArray<RelationQuerySourceInstance> sources =
            [
                .. bindings.Select(static binding => binding.Source)
                    .Distinct()
                    .Select(source => new RelationQuerySourceInstance(
                        source,
                        new("tests/cosmos"),
                        CosmosRelationQueryTargetProfile.Default,
                        new(100, 10_000, 100, 4)))
            ];
            return new(
                RelationQuerySourcePlacement.CurrentSchemaVersion,
                RelationQueryCompiledPlanReference.From(plan),
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                sources,
                bindings);
        }

        static ShapeGraphDocument ShapeDocument()
        {
            var stringType = new ScalarTypeRef(ScalarTypeKind.String);
            var itemType = new ObjectTypeRef(
            [
                new ObjectFieldTypeDef("Name", stringType)
            ]);
            var load = new Shape(
                LoadShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("CustomerId"), stringType),
                    new(new("Status"), stringType),
                    new(new("Amount"), new ScalarTypeRef(ScalarTypeKind.Int32)),
                    new(new("Int64Value"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                    new(new("DecimalValue"), new ScalarTypeRef(ScalarTypeKind.Decimal)),
                    new(new("Tags"), new ArrayTypeRef(stringType)),
                    new(new("Items"), new ArrayTypeRef(itemType)),
                    new(new("ObservedInstant"), new ScalarTypeRef(ScalarTypeKind.Instant)),
                    new(new("ObservedDateTime"), new ScalarTypeRef(ScalarTypeKind.DateTime)),
                    new(
                        new("Notes"),
                        stringType,
                        presence: FieldPresence.Optional,
                        nullability: FieldNullability.Nullable)
                ],
                role: ShapeRoles.Entity);
            var customer = new Shape(
                CustomerShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity)
                ],
                role: ShapeRoles.Entity);
            var row = new Shape(
                RowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Status"), stringType)
                ],
                role: ShapeRoles.Projection);
            var instantRow = new Shape(
                InstantRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("OccurredAt"), new ScalarTypeRef(ScalarTypeKind.Instant))
                ],
                role: ShapeRoles.Projection);
            var dateTimeRow = new Shape(
                DateTimeRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("OccurredAt"), new ScalarTypeRef(ScalarTypeKind.DateTime))
                ],
                role: ShapeRoles.Projection);
            var int64Row = new Shape(
                Int64RowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Value"), new ScalarTypeRef(ScalarTypeKind.Int64))
                ],
                role: ShapeRoles.Projection);
            var decimalRow = new Shape(
                DecimalRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Value"), new ScalarTypeRef(ScalarTypeKind.Decimal))
                ],
                role: ShapeRoles.Projection);
            var nullableRow = new Shape(
                NullableRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(
                        new("Value"),
                        stringType,
                        presence: FieldPresence.Optional,
                        nullability: FieldNullability.Nullable)
                ],
                role: ShapeRoles.Projection);
            var nestedRow = new Shape(
                NestedRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Value"), new ArrayTypeRef(stringType))
                ],
                role: ShapeRoles.Projection);
            var bytesRow = new Shape(
                BytesRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("Payload"), new ScalarTypeRef(ScalarTypeKind.Bytes))
                ],
                role: ShapeRoles.Projection);
            var undefinedRow = new Shape(
                UndefinedRowShape.ShapeId,
                [
                    new(new("Value"), stringType, presence: FieldPresence.Optional)
                ],
                role: ShapeRoles.Projection);
            var aggregate = new Shape(
                AggregateShape.ShapeId,
                [
                    new(new("Count"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                    new(new("Status"), stringType),
                    new(new("Total"), new ScalarTypeRef(ScalarTypeKind.Int32))
                ],
                role: ShapeRoles.Projection);
            var stringAggregate = new Shape(
                StringAggregateShape.ShapeId,
                [
                    new(new("Status"), stringType),
                    new(new("MinimumStatus"), stringType)
                ],
                role: ShapeRoles.Projection);
            return ShapeGraphDocument.FromGraph(new(
                Graph,
                [
                    load,
                    customer,
                    row,
                    instantRow,
                    dateTimeRow,
                    int64Row,
                    decimalRow,
                    nullableRow,
                    nestedRow,
                    bytesRow,
                    undefinedRow,
                    aggregate,
                    stringAggregate
                ]));
        }
    }
}
