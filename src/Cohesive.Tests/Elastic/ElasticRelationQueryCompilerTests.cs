using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Tests.Relations;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticRelationQueryCompilerTests
{
    internal static RelationQueryAdapterConformanceCase CreateBoundRealizationConformanceCase() => new(
        "Elasticsearch",
        ObserveSupported,
        ObserveRejected);

    static RelationQuerySupportedContextObservation ObserveSupported()
    {
        var fixture = Fixture.Row();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        ElasticRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, fixture.StorageBinding);
        var repeated = compiler.Realize(request, fixture.StorageBinding);
        var compilation = compiler.Compile(
            new RelationQueryNativeCompilationRequest(fixture.Plan, bound, fixture.Placement),
            fixture.StorageBinding);
        return new(
            bound,
            repeated,
            compilation.Status,
            [.. compilation.Artifacts.Select(static artifact => artifact.Provenance.BoundRealization)]);
    }

    static RelationQueryRejectedContextObservation ObserveRejected()
    {
        var fixture = Fixture.Row();
        var binding = fixture.StorageBindingWithoutStableUniqueOrdering();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        ElasticRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, binding);
        var compilation = compiler.Compile(request, binding);
        return new(bound, compilation.Status, compilation.Artifacts.Length);
    }

    [Fact]
    public void Realize_ExactBindingAuthorizesNativeCompilationAndFlowsIntoProvenance()
    {
        var fixture = Fixture.Row();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        ElasticRelationQueryCompiler compiler = new();

        var bound = compiler.Realize(request, fixture.StorageBinding);

        Assert.True(bound.IsRealizable, string.Join(
            Environment.NewLine,
            bound.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(fixture.Placement.Fingerprint, bound.Placement);
        Assert.Equal(fixture.StorageBinding.Id.Value, bound.Evidence.Binding.BindingId);
        Assert.Equal(
            fixture.StorageBinding.Fingerprint.Value,
            bound.Evidence.Binding.Fingerprint.Value);
        Assert.NotEmpty(bound.Evidence.Assessments);
        Assert.All(bound.Evidence.Assessments, static assessment =>
            Assert.Equal(RelationQueryBoundAssessmentStatus.Available, assessment.Status));

        RelationQueryNativeCompilationRequest nativeRequest = new(
            fixture.Plan,
            bound,
            fixture.Placement);
        var compilation = compiler.Compile(nativeRequest, fixture.StorageBinding);

        Assert.True(compilation.IsSuccessful, Diagnostics(compilation));
        var artifact = Assert.Single(compilation.Artifacts);
        Assert.Equal(bound.Fingerprint, artifact.Provenance.BoundRealization);
        Assert.Equal(bound.Evidence.Binding, artifact.Provenance.AdapterBinding);
        Assert.NotEmpty(artifact.Provenance.ContextEvidence);
    }

    [Fact]
    public void Realize_PredictsPhysicalScopeMappingPagingAndRetrievalFailures()
    {
        var collection = Fixture.CollectionMembership();
        var nested = Fixture.StructuredCollectionAny();
        var paging = Fixture.KeysetRow();
        var retrieval = Fixture.Row();
        (Fixture Fixture, ElasticRelationQueryStorageBinding Binding, string Message)[] cases =
        [
            (
                collection,
                collection.StorageBindingWithDocumentScope(
                    Fixture.StopLocationsPath,
                    ElasticRelationQueryFieldDocumentScope.NestedDocument),
                "nested-query lowering is deferred"),
            (nested, nested.StorageBindingWithFlattenedStops(), "flattened"),
            (
                paging,
                paging.StorageBindingWithPaginationConsistency(
                    ElasticRelationQueryPaginationConsistency.Unproven),
                "unchanged search-visible view"),
            (
                retrieval,
                retrieval.StorageBindingWithRetrievalEncoding(
                    Fixture.StatusPath,
                    ElasticRelationQueryFieldValueEncoding.JsonInt64),
                "does not preserve")
        ];

        foreach (var item in cases)
        {
            RelationQueryBoundRealizationRequest request = new(
                item.Fixture.Plan,
                item.Fixture.Realization,
                item.Fixture.Placement);
            ElasticRelationQueryCompiler compiler = new();

            var bound = compiler.Realize(request, item.Binding);
            var compilation = compiler.Compile(request, item.Binding);

            Assert.Equal(RelationQueryRealizationStatus.NotRealizable, bound.Status);
            Assert.Contains(bound.Diagnostics, diagnostic =>
                diagnostic.Message.Contains(item.Message, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, compilation.Status);
            Assert.Contains(compilation.Diagnostics, diagnostic =>
                diagnostic.Message.Contains(item.Message, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(compilation.Artifacts);
        }
    }

    [Fact]
    public void Realize_FirstAdapterFailureIsPrimaryAndBlocksUnexaminedRequirements()
    {
        var fixture = Fixture.Row();
        var binding = fixture.StorageBindingWithoutStableUniqueOrdering();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        ElasticRelationQueryCompiler compiler = new();

        var bound = compiler.Realize(request, binding);
        var compilation = compiler.Compile(request, binding);

        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, bound.Status);
        var primary = Assert.Single(bound.Evidence.Assessments, static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Unavailable);
        Assert.Equal(
            new RelationQueryAdapterDecisionCode(
                ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable),
            primary.AdapterDecisionCode);
        Assert.Equal(ElasticRelationQueryTargetProfile.StableOrderingBoundary, primary.FailedOperatingBoundary);
        Assert.NotNull(primary.Input);
        Assert.NotNull(primary.Field);
        Assert.NotNull(primary.PlacementBinding);
        Assert.Null(primary.ConfigurationSetting);
        Assert.NotNull(primary.FailedConfigurationSetting);
        Assert.EndsWith("/semanticCapabilities", primary.FailedConfigurationSetting, StringComparison.Ordinal);
        Assert.Equal(RelationQueryConfigurationValueOrigin.Explicit, primary.Origin);
        Assert.Equal(binding.Id.Value, primary.Authority);

        var blocked = bound.Evidence.Assessments.Where(static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Blocked).ToArray();
        Assert.Equal(bound.Evidence.Assessments.Length - 1, blocked.Length);
        Assert.DoesNotContain(bound.Evidence.Assessments, static assessment =>
            assessment.Status == RelationQueryBoundAssessmentStatus.Available);
        Assert.All(blocked, assessment =>
        {
            Assert.Equal(primary.Id, assessment.BlockedBy);
            Assert.Equal(primary.AdapterDecisionCode, assessment.AdapterDecisionCode);
            Assert.Equal(RelationQueryUnavailableReason.PrerequisiteBlocked, assessment.UnavailableReason);
            Assert.Empty(assessment.CapabilityEvidence);
            Assert.Empty(assessment.OperatingBoundaries);
            Assert.Empty(assessment.PreservedGuarantees);
        });
        Assert.Contains(bound.Diagnostics, diagnostic =>
            diagnostic.ContextEvidence == primary.Id
            && diagnostic.AdapterDecisionCode == primary.AdapterDecisionCode
            && diagnostic.ConfigurationOrigin == primary.Origin
            && diagnostic.ConfigurationAuthority == primary.Authority);
        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.AdapterDecisionCode == primary.AdapterDecisionCode);
        Assert.Empty(compilation.Artifacts);
    }

    [Fact]
    public void Realize_ProfileInfeasibilityDoesNotInvokeContextualSuccessProjection()
    {
        var fixture = Fixture.Row();
        var planReference = RelationQueryCompiledPlanReference.From(fixture.Plan);
        var unavailableProfile = new RelationQueryTargetCapabilityProfile(
            ElasticRelationQueryTargetProfile.Target,
            ElasticRelationQueryTargetProfile.ProfileId,
            [planReference.DefinitionSchemaVersion],
            [planReference.CompilerProfile]);
        var infeasible = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            unavailableProfile,
            ElasticRelationQueryTargetProfile.Policy);
        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, infeasible.Status);
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            infeasible,
            fixture.Placement);
        ElasticRelationQueryCompiler compiler = new();

        var bound = compiler.Realize(request, fixture.StorageBinding);
        var compilation = compiler.Compile(request, fixture.StorageBinding);

        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, bound.Status);
        Assert.Empty(bound.Evidence.Assessments);
        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, compilation.Status);
        Assert.Empty(compilation.Artifacts);
    }

    [Fact]
    public void Realize_SelectedIndependentSourceBranchIgnoresUnselectedSourceAndResult()
    {
        var fixture = Fixture.IndependentSources();
        var allBranches = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var loads = Assert.Single(
            allBranches.Branches,
            static branch => branch.QueryResult == new QueryResultId("load-rows"));
        var selected = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement,
            [loads.Id]);
        ElasticRelationQueryCompiler compiler = new();

        var allReport = compiler.Realize(allBranches, fixture.StorageBinding);
        var selectedReport = compiler.Realize(selected, fixture.StorageBinding);
        var selectedCompilation = compiler.Compile(selected, fixture.StorageBinding);

        Assert.Equal(RelationQueryRealizationStatus.Invalid, allReport.Status);
        Assert.True(selectedReport.IsRealizable, Diagnostics(selectedReport));
        Assert.All(selectedReport.Evidence.Assessments, assessment => Assert.Equal(loads.Id, assessment.Branch));
        Assert.True(selectedCompilation.IsSuccessful, Diagnostics(selectedCompilation));
        var artifact = Assert.Single(selectedCompilation.Artifacts);
        Assert.Equal(loads.Id, artifact.Branch.Id);
        Assert.Equal("loads-read", artifact.StorageBinding.IndexName);
    }

    [Fact]
    public void Compile_ExactNativeRequestRejectsBindingFingerprintSubstitution()
    {
        var fixture = Fixture.Row();
        ElasticRelationQueryCompiler compiler = new();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var bound = compiler.Realize(request, fixture.StorageBinding);
        RelationQueryNativeCompilationRequest nativeRequest = new(
            fixture.Plan,
            bound,
            fixture.Placement);
        var substituted = fixture.StorageBindingWithBoundaries(
            maximumResultWindow: 20_000,
            maximumPageSize: 1_000);

        var compilation = compiler.Compile(nativeRequest, substituted);

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, compilation.Status);
        Assert.Contains(compilation.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            && diagnostic.Message.Contains("fingerprint", StringComparison.Ordinal));
        Assert.Empty(compilation.Artifacts);
    }

    [Fact]
    public void Realize_BindingReferenceRetainsCompilerAndLoweringPolicyConfiguration()
    {
        var fixture = Fixture.Row();
        ElasticRelationQueryCompiler compiler = new();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);

        var bound = compiler.Realize(request, fixture.StorageBinding);

        Assert.True(bound.IsRealizable, Diagnostics(bound));
        var configuration = bound.Evidence.Binding.ConfigurationDecisions
            .ToDictionary(static decision => decision.Setting, StringComparer.Ordinal);
        Assert.Equal(
            ElasticRelationQueryCompilerOptions.CurrentCompilerProfile,
            configuration[ElasticRelationQueryCompiler.CompilerProfileSetting].Authority);
        Assert.Equal(
            ElasticRelationQueryCompilerOptions.DefaultConventionSetVersion,
            configuration[ElasticRelationQueryCompiler.CompilerConventionSetting].Authority);
        var lowering = configuration[ElasticRelationQueryCompiler.LoweringPolicySetting];
        Assert.Equal(RelationQueryConfigurationValueOrigin.AdapterConvention, lowering.Origin);
        Assert.Contains(ElasticQueryLoweringPolicy.Default.Fingerprint.Value, lowering.Authority, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCompile_RejectsBoundEvidenceAuthoredUnderDifferentCompilerPolicy()
    {
        var fixture = Fixture.Row();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var bound = new ElasticRelationQueryCompiler().Realize(request, fixture.StorageBinding);
        RelationQueryNativeCompilationRequest nativeRequest = new(
            fixture.Plan,
            bound,
            fixture.Placement);
        ElasticRelationQueryCompiler[] changedCompilers =
        [
            new(new(
                compilerProfile: "tests/elastic/compiler-v3",
                conventionSetVersion: ElasticRelationQueryCompilerOptions.DefaultConventionSetVersion)),
            new(loweringPolicy: SuffixPolicy(
                ElasticQueryLoweringFallbackPolicy.RequirePreferred,
                ElasticQueryLoweringStrategies.WildcardExactKeywordId))
        ];

        foreach (var changedCompiler in changedCompilers)
        {
            var result = changedCompiler.Compile(nativeRequest, fixture.StorageBinding);

            Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
                && diagnostic.Message.Contains("compiler-policy evidence", StringComparison.Ordinal));
            Assert.Empty(result.Artifacts);
        }
    }

    [Fact]
    public void Compile_ArtifactFingerprintCoversBoundAndContextEvidenceProvenance()
    {
        var fixture = Fixture.Row();
        ElasticRelationQueryCompiler compiler = new();
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement);
        var baseline = compiler.Realize(request, fixture.StorageBinding);
        var first = baseline.Evidence.Assessments[0];
        RelationQueryBoundRequirementAssessment additional = new(
            new($"{first.Id.Value}/additional-proof"),
            first.Branch,
            first.Requirement,
            first.Status,
            first.Origin,
            first.Authority,
            first.CapabilityEvidence,
            first.OperatingBoundaries,
            first.PreservedGuarantees,
            first.UnavailableReason,
            first.Node,
            first.Input,
            first.Field,
            first.PlacementBinding,
            first.ConfigurationSetting,
            first.Message,
            first.Resolution);
        RelationQueryContextualEvidenceProjection extendedEvidence = new(
            baseline.Evidence.Binding,
            [.. baseline.Evidence.Assessments, additional]);
        var extended = RelationQueryBoundRealizationCompiler.Compile(request, extendedEvidence);

        var baselineArtifact = Assert.Single(compiler.Compile(
            new RelationQueryNativeCompilationRequest(fixture.Plan, baseline, fixture.Placement),
            fixture.StorageBinding).Artifacts);
        var extendedArtifact = Assert.Single(compiler.Compile(
            new RelationQueryNativeCompilationRequest(fixture.Plan, extended, fixture.Placement),
            fixture.StorageBinding).Artifacts);
        Dictionary<QueryParameterId, ObservationValue> parameters = new()
        {
            [new("status")] = ObservationValue.FromString("ready")
        };

        Assert.NotEqual(baseline.Fingerprint, extended.Fingerprint);
        Assert.Equal(
            ElasticSdkRequestTestSupport.SerializeToString(baselineArtifact.Bind(parameters)),
            ElasticSdkRequestTestSupport.SerializeToString(extendedArtifact.Bind(parameters)));
        Assert.NotEqual(baselineArtifact.Fingerprint, extendedArtifact.Fingerprint);
        Assert.DoesNotContain(additional.Id, baselineArtifact.Provenance.ContextEvidence);
        Assert.Contains(additional.Id, extendedArtifact.Provenance.ContextEvidence);
    }

    [Fact]
    public void Compile_RowQuery_ProducesExactReusableArtifactAndBindsParameters()
    {
        var fixture = Fixture.Row();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        Assert.Equal(RelationQueryNativeCompilationStatus.Exact, result.Status);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(RelationQueryNativeResultKind.QueryRows, artifact.Branch.Kind);
        Assert.Equal(["id", "status"], artifact.RequestTemplate.SourceIncludes.ToArray());
        Assert.Equal(["id.keyword"], artifact.RequestTemplate.Sorts.Select(static sort => sort.Field));
        Assert.Equal([Fixture.IdPath, Fixture.StatusPath], artifact.ResultFields.Select(static field => field.Field.Path));
        Assert.Equal(new QueryParameterId("status"), Assert.Single(artifact.Parameters).Parameter);
        Assert.NotNull(artifact.Paging);
        Assert.Equal(ElasticRelationQueryPagingKind.Offset, artifact.Paging.Kind);
        Assert.Equal(5, artifact.Paging.Offset);
        Assert.Equal(25, artifact.Paging.Limit);
        Assert.Equal(["id.keyword"], artifact.Paging.SortFields.ToArray());
        Assert.Equal("id.keyword", artifact.Paging.StableUniqueFinalField);
        Assert.Equal(fixture.PlanReference, artifact.Provenance.Plan);
        Assert.Equal(fixture.Realization.Fingerprint, artifact.Provenance.Realization);
        Assert.Equal(fixture.Placement.Fingerprint, artifact.Provenance.Placement);

        var ready = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("ready")
        });
        var closed = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("closed")
        });

        Assert.Equal("loads-read", Assert.Single(ready.Indices!).ToString());
        Assert.False(ready.AllowPartialSearchResults);
        Assert.NotEqual(
            ElasticSdkRequestTestSupport.SerializeToString(ready),
            ElasticSdkRequestTestSupport.SerializeToString(closed));
        using (var request = ElasticSdkRequestTestSupport.Serialize(ready))
        {
            var root = request.RootElement;
            Assert.Equal(
                "ready",
                FirstFilter(root)
                    .GetProperty("term")
                    .GetProperty("status.keyword")
                    .GetProperty("value")
                    .GetString());
            Assert.Equal(5, root.GetProperty("from").GetInt32());
            Assert.Equal(25, root.GetProperty("size").GetInt32());
        }

        ready.Size = 1;
        var rebound = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("ready")
        });
        Assert.NotSame(ready, rebound);
        Assert.NotSame(ready.Query, rebound.Query);
        Assert.Equal(25, rebound.Size);

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
                [new("status")] = ObservationValue.FromString("ready"),
                [new("unknown")] = ObservationValue.FromString("value")
            }));
    }

    [Fact]
    public void Compile_Suffix_DefaultPrefersReversedFieldPrefix()
    {
        var fixture = Fixture.Suffix();

        var artifact = Assert.Single(fixture.Compile().Artifacts);
        var decision = Assert.Single(artifact.LoweringDecisions).Decision;
        var request = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("suffix")] = ObservationValue.FromString("A😀β")
        });

        Assert.Equal(ElasticQueryLoweringStrategies.ReversedFieldPrefixId, decision.SelectedStrategy);
        Assert.Equal(
            [ElasticQueryLoweringAttemptDisposition.Selected, ElasticQueryLoweringAttemptDisposition.NotConsidered],
            decision.Attempts.Select(static attempt => attempt.Disposition));
        var selectedByPath = artifact.SelectedFields.ToDictionary(static field => field.Field.Path);
        Assert.Equal(FieldPath.Parse("id"), selectedByPath[Fixture.IdPath].SourceField);
        Assert.Equal(
            [FieldPath.Parse("id.keyword")],
            selectedByPath[Fixture.IdPath].QueryFields.ToArray());
        Assert.Equal(FieldPath.Parse("status"), selectedByPath[Fixture.StatusPath].SourceField);
        Assert.Empty(selectedByPath[Fixture.StatusPath].QueryFields);
        Assert.Null(selectedByPath[Fixture.CustomerNamePath].SourceField);
        Assert.Equal(
            [FieldPath.Parse("customerName.reversed")],
            selectedByPath[Fixture.CustomerNamePath].QueryFields.ToArray());
        using var json = ElasticSdkRequestTestSupport.Serialize(request);
        Assert.Equal(
            "β😀A",
            FirstFilter(json.RootElement)
                .GetProperty("prefix")
                .GetProperty("customerName.reversed")
                .GetProperty("value")
                .GetString());
    }

    [Fact]
    public void Compile_Suffix_DefaultFallsBackToExactWildcard()
    {
        var fixture = Fixture.Suffix();
        var binding = fixture.StorageBindingWithSuffixCapabilities(
            ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix);

        var result = fixture.Compile(binding);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        var decision = Assert.Single(artifact.LoweringDecisions).Decision;
        Assert.Equal(ElasticQueryLoweringStrategies.WildcardExactKeywordId, decision.SelectedStrategy);
        Assert.Equal(
            [ElasticQueryLoweringAttemptDisposition.Rejected, ElasticQueryLoweringAttemptDisposition.Selected],
            decision.Attempts.Select(static attempt => attempt.Disposition));
        var request = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("suffix")] = ObservationValue.FromString("A*?\\")
        });
        using var json = ElasticSdkRequestTestSupport.Serialize(request);
        Assert.Equal(
            "*A\\*\\?\\\\",
            FirstFilter(json.RootElement)
                .GetProperty("wildcard")
                .GetProperty("customerName.keyword")
                .GetProperty("value")
                .GetString());
    }

    [Fact]
    public void Compile_Suffix_RequirePreferredFailsWithoutUsingEligibleFallback()
    {
        var fixture = Fixture.Suffix();
        var binding = fixture.StorageBindingWithSuffixCapabilities(
            ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix);
        var policy = SuffixPolicy(
            ElasticQueryLoweringFallbackPolicy.RequirePreferred,
            ElasticQueryLoweringStrategies.ReversedFieldPrefixId,
            ElasticQueryLoweringStrategies.WildcardExactKeywordId);

        var result = fixture.Compile(binding, loweringPolicy: policy);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.LoweringUnavailable);
        Assert.Contains(ElasticQueryLoweringStrategies.ReversedFieldPrefixId.Value, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("disables fallback", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_Suffix_ExplicitPreferenceCanSelectWildcardOverReversedField()
    {
        var fixture = Fixture.Suffix();
        var policy = SuffixPolicy(
            ElasticQueryLoweringFallbackPolicy.RequirePreferred,
            ElasticQueryLoweringStrategies.WildcardExactKeywordId,
            ElasticQueryLoweringStrategies.ReversedFieldPrefixId);

        var result = fixture.Compile(loweringPolicy: policy);

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var decision = Assert.Single(Assert.Single(result.Artifacts).LoweringDecisions).Decision;
        Assert.Equal(ElasticQueryLoweringPreferenceOrigin.ExplicitLocal, decision.PreferenceOrigin);
        Assert.Equal(ElasticQueryLoweringStrategies.WildcardExactKeywordId, decision.SelectedStrategy);
    }

    [Fact]
    public void Compile_TwoSuffixesInOneFilter_RetainDistinctLoweringSites()
    {
        var fixture = Fixture.TwoSuffixes();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var decisions = Assert.Single(result.Artifacts).LoweringDecisions;
        Assert.Equal(2, decisions.Length);
        Assert.Equal(2, decisions.Select(static decision => decision.SiteId).Distinct(StringComparer.Ordinal).Count());
        Assert.EndsWith("/lowering/0", decisions[0].SiteId, StringComparison.Ordinal);
        Assert.EndsWith("/lowering/1", decisions[1].SiteId, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_CollectionMembership_ProducesExactTermQuery()
    {
        var fixture = Fixture.CollectionMembership();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(new QueryParameterId("location"), Assert.Single(artifact.Parameters).Parameter);
        var selected = artifact.SelectedFields.Single(field => field.Field.Path == Fixture.StopLocationsPath);
        Assert.Null(selected.SourceField);
        Assert.Equal([FieldPath.Parse("stopLocations.keyword")], selected.QueryFields.ToArray());
        var request = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("location")] = ObservationValue.FromString("SEA")
        });

        using var json = ElasticSdkRequestTestSupport.Serialize(request);
        Assert.Equal(
            "SEA",
            FirstFilter(json.RootElement)
                .GetProperty("term")
                .GetProperty("stopLocations.keyword")
                .GetProperty("value")
                .GetString());
        Assert.Throws<ArgumentException>(() => artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("location")] = ObservationValue.FromInt64(42)
        }));
    }

    [Fact]
    public void Compile_CollectionMembership_SupportsAConstantCandidate()
    {
        var fixture = Fixture.CollectionMembership(Expr.Const("SEA"));

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Empty(artifact.Parameters);
        using var json = ElasticSdkRequestTestSupport.Serialize(
            artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>()));
        Assert.Equal(
            "SEA",
            FirstFilter(json.RootElement)
                .GetProperty("term")
                .GetProperty("stopLocations.keyword")
                .GetProperty("value")
                .GetString());
    }

    [Fact]
    public void Compile_CollectionMembership_FailsWithoutExactBindingEvidence()
    {
        var fixture = Fixture.CollectionMembership();
        var binding = fixture.StorageBindingWithoutCollectionMembership();

        var result = fixture.Compile(binding);

        Assert.NotEqual(fixture.StorageBinding.Fingerprint, binding.Fingerprint);
        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains(
            nameof(ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership),
            diagnostic.Message,
            StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_CollectionMembership_FailsForMismatchedElementAndCandidateDomains()
    {
        var fixture = Fixture.CollectionMembership(
            Expr.Param("location"),
            ScalarTypeKind.Int64);

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression
            && diagnostic.Message.Contains("same domain", StringComparison.Ordinal));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_CollectionMembership_FailsForNullCandidate()
    {
        var fixture = Fixture.CollectionMembership(Expr.Null());

        var result = fixture.Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
            && diagnostic.Message.Contains("required, non-null", StringComparison.Ordinal));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_CollectionMembership_FailsForNestedDocumentScope()
    {
        var fixture = Fixture.CollectionMembership();
        var binding = fixture.StorageBindingWithDocumentScope(
            Fixture.StopLocationsPath,
            ElasticRelationQueryFieldDocumentScope.NestedDocument);

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
            && diagnostic.Message.Contains("nested-query lowering is deferred", StringComparison.Ordinal));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_PreservesTwoFieldSameElementCorrelationInOneNestedQuery()
    {
        var fixture = Fixture.StructuredCollectionAny();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        var selected = artifact.SelectedFields.Single(field => field.Field.Path == Fixture.StopsPath);
        Assert.Null(selected.SourceField);
        Assert.Equal(
            [
                FieldPath.Parse("stops"),
                FieldPath.Parse("stops.type.keyword"),
                FieldPath.Parse("stops.location.keyword")
            ],
            selected.QueryFields.ToArray());

        var request = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("location")] = ObservationValue.FromString("SEA")
        });
        var nestedQuery = Assert.IsType<global::Elastic.Clients.Elasticsearch.QueryDsl.NestedQuery>(
            Assert.IsType<global::Elastic.Clients.Elasticsearch.QueryDsl.BoolQuery>(request.Query!.Bool)
                .Filter!
                .Single()
                .Nested);
        Assert.Equal("stops", nestedQuery.Path.ToString());
        var correlated = Assert.IsType<global::Elastic.Clients.Elasticsearch.QueryDsl.BoolQuery>(nestedQuery.Query.Bool);
        Assert.Equal(2, correlated.Filter?.Count);

        using var json = ElasticSdkRequestTestSupport.Serialize(request);
        var nested = FirstFilter(json.RootElement).GetProperty("nested");
        Assert.Equal("stops", nested.GetProperty("path").GetString());
        var clauses = nested.GetProperty("query").GetProperty("bool").GetProperty("filter");
        Assert.Equal(2, clauses.GetArrayLength());
        Assert.Equal(
            "SEA",
            clauses[0]
                .GetProperty("term")
                .GetProperty("stops.location.keyword")
                .GetProperty("value")
                .GetString());
        Assert.Equal(
            "Pickup",
            clauses[1]
                .GetProperty("term")
                .GetProperty("stops.type.keyword")
                .GetProperty("value")
                .GetString());
    }

    [Fact]
    public void TargetProfile_AdvertisesOnlyDirectCurrentItemCollectionElementReads()
    {
        var structural = ElasticRelationQueryTargetProfile.Default.Capabilities
            .Select(static evidence => evidence.Capability)
            .OfType<StructuralRelationQueryCapability>()
            .ToArray();

        Assert.Contains(
            structural,
            static capability =>
                capability.Role == RelationQueryStructuralCapabilityRole.CurrentItemRead
                && capability.PathKind == RelationQueryStructuralPathKind.CollectionElement);
        Assert.DoesNotContain(
            structural,
            static capability =>
                capability.PathKind == RelationQueryStructuralPathKind.CollectionElement
                && capability.Role != RelationQueryStructuralCapabilityRole.CurrentItemRead);
        Assert.DoesNotContain(
            structural,
            static capability =>
                capability.Role == RelationQueryStructuralCapabilityRole.CurrentItemRead
                && capability.PathKind != RelationQueryStructuralPathKind.CollectionElement);
        Assert.DoesNotContain(
            structural,
            static capability =>
                capability.PathKind == RelationQueryStructuralPathKind.NestedCollectionElement);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedForFlattenedObjectMapping()
    {
        var fixture = Fixture.StructuredCollectionAny();

        var result = fixture.Compile(fixture.StorageBindingWithFlattenedStops());

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable);
        Assert.Contains("flattened", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Contains", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWithoutNestedScopeEvidence()
    {
        var fixture = Fixture.StructuredCollectionAny();

        var result = fixture.Compile(fixture.StorageBindingWithoutNestedEvidence());

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable);
        Assert.Contains("does not provide", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("denormalized", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWithoutSameElementGuarantee()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsNestedScope;
        var binding = fixture.StorageBindingWithNestedScope(new(
            current.NestedPath,
            ElasticRelationQueryNestedCorrelationGuarantee.Unproven,
            current.NullElementBehavior,
            current.EmptyCollectionBehavior,
            current.ChildFields));

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable);
        Assert.Contains("same-nested-document", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWhenNullElementsWouldBeDropped()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsNestedScope;
        var binding = fixture.StorageBindingWithNestedScope(new(
            current.NestedPath,
            current.CorrelationGuarantee,
            ElasticRelationQueryNestedAbsenceBehavior.NotIndexed,
            current.EmptyCollectionBehavior,
            current.ChildFields));

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable);
        Assert.Contains("null collection elements", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWithoutEmptyCollectionRepresentation()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsNestedScope;
        var binding = fixture.StorageBindingWithNestedScope(new(
            current.NestedPath,
            current.CorrelationGuarantee,
            current.NullElementBehavior,
            ElasticRelationQueryEmptyCollectionBehavior.Unproven,
            current.ChildFields));

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable);
        Assert.Contains("empty collection", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWhenMissingCollectionIsTreatedAsEmpty()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var binding = fixture.StorageBindingWithStopsAbsence(
            ElasticRelationQueryMissingValueBehavior.NotIndexed,
            ElasticRelationQueryNullValueBehavior.ProhibitedByIngestion);

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable);
        Assert.Contains("treating them as empty", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWhenChildMissingBehaviorIsUnproven()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsNestedScope;
        var location = current.ResolveChild(Fixture.StopLocationPath);
        var weakLocation = new ElasticRelationQueryNestedChildFieldBinding(
            location.ElementPath,
            location.QueryField,
            location.MappingKind,
            location.SemanticCapabilities,
            location.SemanticProfile,
            ElasticRelationQueryNestedAbsenceBehavior.Unproven,
            location.NullValueBehavior);
        var binding = fixture.StorageBindingWithNestedScope(new(
            current.NestedPath,
            current.CorrelationGuarantee,
            current.NullElementBehavior,
            current.EmptyCollectionBehavior,
            [
                .. current.ChildFields.Select(child =>
                    child.ElementPath == Fixture.StopLocationPath ? weakLocation : child)
            ]));

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable);
        Assert.Contains("prohibits missing and null", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWithoutReferencedChildMapping()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsNestedScope;
        var binding = fixture.StorageBindingWithNestedScope(new(
            current.NestedPath,
            current.CorrelationGuarantee,
            current.NullElementBehavior,
            current.EmptyCollectionBehavior,
            [.. current.ChildFields.Where(child => child.ElementPath != Fixture.StopLocationPath)]));

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable);
        Assert.Contains("no terminal child mapping", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_StructuredCollectionAny_FailsClosedWithoutExactChildTermEvidence()
    {
        var fixture = Fixture.StructuredCollectionAny();
        var current = fixture.StopsNestedScope;
        var location = current.ResolveChild(Fixture.StopLocationPath);
        var weakLocation = new ElasticRelationQueryNestedChildFieldBinding(
            location.ElementPath,
            location.QueryField,
            location.MappingKind,
            ElasticRelationQueryFieldSemanticCapabilities.None,
            semanticProfile: null,
            missingValueBehavior: location.MissingValueBehavior,
            nullValueBehavior: location.NullValueBehavior);
        var binding = fixture.StorageBindingWithNestedScope(new(
            current.NestedPath,
            current.CorrelationGuarantee,
            current.NullElementBehavior,
            current.EmptyCollectionBehavior,
            [
                .. current.ChildFields.Select(child =>
                    child.ElementPath == Fixture.StopLocationPath ? weakLocation : child)
            ]));

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable);
        Assert.Contains("ExactTerm", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_LoadSearchRowsAndCount_ShareCollectionMembershipFilter()
    {
        var fixture = Fixture.LoadSearch();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        Assert.Equal(2, result.Artifacts.Length);
        var rows = result.Artifacts.Single(static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows);
        var count = result.Artifacts.Single(static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation);
        var parameters = new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("customer-name-suffix")] = ObservationValue.FromString("Inc"),
            [new("location")] = ObservationValue.FromString("SEA")
        };

        var rowsJson = ElasticSdkRequestTestSupport.SerializeToString(rows.Bind(parameters));
        using var countJson = ElasticSdkRequestTestSupport.Serialize(count.Bind(parameters));

        Assert.Contains("stopLocations.keyword", rowsJson, StringComparison.Ordinal);
        Assert.Contains("SEA", rowsJson, StringComparison.Ordinal);
        Assert.Equal(0, countJson.RootElement.GetProperty("size").GetInt32());
        Assert.True(countJson.RootElement.GetProperty("track_total_hits").GetBoolean());
        Assert.Contains("stopLocations.keyword", countJson.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RowPaging_FailsWithoutStableUniqueFinalSortEvidence()
    {
        var fixture = Fixture.Row();
        var binding = fixture.StorageBindingWithoutStableUniqueOrdering();

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains(nameof(ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering), diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_RowPaging_FailsBeyondConfiguredResultWindow()
    {
        var fixture = Fixture.Row(offset: 90, limit: 25);
        var binding = fixture.StorageBindingWithBoundaries(maximumResultWindow: 100, maximumPageSize: 100);

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable);
        Assert.Contains("index.max_result_window 100", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_RowResult_FailsWhenPhysicalRetrievalEncodingDoesNotPreserveSemanticEncoding()
    {
        var fixture = Fixture.Row();
        var binding = fixture.StorageBindingWithRetrievalEncoding(
            Fixture.StatusPath,
            ElasticRelationQueryFieldValueEncoding.JsonInt64);

        var result = fixture.Compile(binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
            && diagnostic.Message.Contains("does not preserve", StringComparison.Ordinal));
        Assert.Contains(nameof(ElasticRelationQueryResultValueEncoding.JsonString), diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ElasticRelationQueryResultValueEncoding.JsonInt64), diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Input);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_KeysetAndCompositePaging_FailWithoutStableSearchViewEvidence()
    {
        var keyset = Fixture.KeysetRow();
        var composite = Fixture.GroupedCount();

        var keysetResult = keyset.Compile(keyset.StorageBindingWithPaginationConsistency(
            ElasticRelationQueryPaginationConsistency.Unproven));
        var compositeResult = composite.Compile(composite.StorageBindingWithPaginationConsistency(
            ElasticRelationQueryPaginationConsistency.Unproven));

        AssertPaginationRequiresStableSearchView(keysetResult, "search_after");
        AssertPaginationRequiresStableSearchView(compositeResult, "composite after-key");
    }

    [Fact]
    public void Compile_TemporalComparisonOrderingAndGrouping_FailClosed()
    {
        var comparison = Fixture.TemporalComparison().Compile();
        var ordering = Fixture.TemporalOrdering().Compile();
        var grouping = Fixture.TemporalGrouping().Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, comparison.Status);
        Assert.Contains(comparison.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
            && diagnostic.Message.Contains("Temporal field input", StringComparison.Ordinal));
        Assert.Empty(comparison.Artifacts);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, ordering.Status);
        Assert.Contains(ordering.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
            && diagnostic.Message.Contains("Temporal field input", StringComparison.Ordinal));
        Assert.Empty(ordering.Artifacts);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, grouping.Status);
        Assert.Contains(grouping.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.AggregateUnsupported
            && diagnostic.Message.Contains("composite-key domain", StringComparison.Ordinal));
        Assert.Empty(grouping.Artifacts);
    }

    [Fact]
    public void Compile_CustomSuffixStrategiesFailClosedWhenInvalidOrThrowing()
    {
        var fixture = Fixture.Suffix();
        IElasticQueryLoweringStrategy[] invalidStrategies =
        [
            new UndeclaredParameterSuffixStrategy(),
            new RogueFieldSuffixStrategy(),
            new ThrowingSuffixStrategy()
        ];

        var results = invalidStrategies
            .Select(strategy => fixture.Compile(loweringPolicy: ExtensionSuffixPolicy(strategy)))
            .ToArray();

        Assert.All(results, static result =>
        {
            Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.LoweringConfigurationInvalid
                && diagnostic.AdapterDecisionCode == new RelationQueryAdapterDecisionCode(
                    ElasticRelationQueryCompilationDiagnosticCodes.LoweringConfigurationInvalid));
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.BindingSetting == ElasticRelationQueryCompiler.LoweringPolicySetting
                && diagnostic.ConfigurationOrigin == RelationQueryConfigurationValueOrigin.Explicit
                && diagnostic.ConfigurationAuthority is not null);
            Assert.Empty(result.Artifacts);
        });
        Assert.Contains(results[0].Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("parameters outside", StringComparison.Ordinal));
        Assert.Contains(results[1].Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("physical field outside", StringComparison.Ordinal));
        Assert.Contains(results[2].Diagnostics, static diagnostic =>
            diagnostic.Message.Contains(nameof(IOException), StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_NestedDocumentScope_FailsForQueriesAndScalarSourceDecoding()
    {
        var queriedFixture = Fixture.Row();
        var queriedBinding = queriedFixture.StorageBindingWithDocumentScope(
            Fixture.StatusPath,
            ElasticRelationQueryFieldDocumentScope.NestedDocument);
        var projectedFixture = Fixture.Suffix();
        var projectedBinding = projectedFixture.StorageBindingWithDocumentScope(
            Fixture.StatusPath,
            ElasticRelationQueryFieldDocumentScope.NestedDocument);

        var queried = queriedFixture.Compile(queriedBinding);
        var projected = projectedFixture.Compile(projectedBinding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, queried.Status);
        Assert.Contains(queried.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
            && diagnostic.Message.Contains("nested-query lowering is deferred", StringComparison.Ordinal));
        Assert.Empty(queried.Artifacts);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, projected.Status);
        Assert.Contains(projected.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
            && diagnostic.Message.Contains("nested source extraction is deferred", StringComparison.Ordinal));
        Assert.Empty(projected.Artifacts);
    }

    [Fact]
    public void FieldBinding_RejectsMetadataIdMisuseAndUnprovenDocumentScope()
    {
        var input = new RelationQueryInputId("load-id");

        Assert.Throws<ArgumentException>(() => new ElasticRelationQueryFieldBinding(
            input,
            FieldPath.Parse("_id"),
            FieldPath.Parse("_id"),
            ElasticRelationQueryFieldMappingKind.Keyword,
            retrievalKind: ElasticRelationQueryFieldRetrievalKind.Source,
            retrievalEncoding: ElasticRelationQueryFieldValueEncoding.JsonString,
            documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
            semanticCapabilities: ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
            semanticProfile: "tests/id-v1"));
        Assert.Throws<ArgumentException>(() => new ElasticRelationQueryFieldBinding(
            input,
            sourceField: null,
            FieldPath.Parse("_id"),
            ElasticRelationQueryFieldMappingKind.Keyword,
            ElasticRelationQueryFieldRetrievalKind.Unavailable,
            retrievalEncoding: null,
            documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
            semanticCapabilities: ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering,
            semanticProfile: "tests/id-v1"));
        Assert.Throws<ArgumentException>(() => new ElasticRelationQueryFieldBinding(
            input,
            sourceField: null,
            FieldPath.Parse("_id"),
            ElasticRelationQueryFieldMappingKind.Keyword,
            ElasticRelationQueryFieldRetrievalKind.Unavailable,
            retrievalEncoding: null,
            documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
            semanticCapabilities: ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership,
            semanticProfile: "tests/id-v1"));
        Assert.Throws<ArgumentException>(() => new ElasticRelationQueryFieldBinding(
            input,
            sourceField: null,
            FieldPath.Parse("locations"),
            ElasticRelationQueryFieldMappingKind.Double,
            ElasticRelationQueryFieldRetrievalKind.Unavailable,
            retrievalEncoding: null,
            documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
            semanticCapabilities: ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership,
            semanticProfile: "tests/array-v1"));
        Assert.Throws<ArgumentException>(() => new ElasticRelationQueryFieldBinding(
            input,
            FieldPath.Parse("id"),
            FieldPath.Parse("id.keyword"),
            ElasticRelationQueryFieldMappingKind.Keyword,
            retrievalKind: ElasticRelationQueryFieldRetrievalKind.Source,
            retrievalEncoding: ElasticRelationQueryFieldValueEncoding.JsonString,
            documentScope: ElasticRelationQueryFieldDocumentScope.Unproven,
            semanticCapabilities: ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
            semanticProfile: "tests/id-v1"));
    }

    [Fact]
    public void PagingContract_RequiresStableUniqueFieldToBeFinalSortField()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ElasticRelationQueryPagingContract(
            ElasticRelationQueryPagingKind.SearchAfter,
            offset: 0,
            limit: 25,
            ["createdAt", "id.keyword"],
            stableUniqueFinalField: "createdAt"));

        Assert.Contains("final physical sort field", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_GlobalCount_UsesExactTotalHits()
    {
        var fixture = Fixture.GlobalCount();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(RelationQueryNativeResultKind.QueryAggregation, artifact.Branch.Kind);
        Assert.Null(artifact.Paging);
        var resultField = Assert.Single(artifact.ResultFields);
        Assert.Equal(ElasticRelationQueryResultSourceKind.ExactTotalHits, resultField.SourceKind);
        Assert.Equal(ElasticRelationQueryResultValueEncoding.ExactCountInt64, resultField.Encoding);
        var request = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("ready")
        });
        using var json = ElasticSdkRequestTestSupport.Serialize(request);
        Assert.False(json.RootElement.GetProperty("_source").GetBoolean());
        Assert.Equal(0, json.RootElement.GetProperty("size").GetInt32());
        Assert.True(json.RootElement.GetProperty("track_total_hits").GetBoolean());
        Assert.Equal(
            "ready",
            FirstFilter(json.RootElement)
                .GetProperty("term")
                .GetProperty("status.keyword")
                .GetProperty("value")
                .GetString());
    }

    [Fact]
    public void Compile_FieldlessGlobalCount_UsesMatchAllAndExactTotalHits()
    {
        var fixture = Fixture.FieldlessGlobalCount();

        Assert.Empty(fixture.StorageBinding.Fields);
        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Empty(artifact.Parameters);
        Assert.Empty(artifact.SelectedFields);
        var request = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>());
        Assert.Equal(0, request.Size);
        Assert.NotNull(request.Query?.MatchAll);
        Assert.True(request.Source?.HasBoolValue);
        Assert.False(request.Source?.Value1);
        Assert.True(request.TrackTotalHits?.Value1);
        using var json = ElasticSdkRequestTestSupport.Serialize(request);
        Assert.Equal(0, json.RootElement.GetProperty("size").GetInt32());
        Assert.True(json.RootElement.GetProperty("track_total_hits").GetBoolean());
    }

    [Fact]
    public void Compile_CompositeGroupedCount_UsesExactKeysetBuckets()
    {
        var fixture = Fixture.GroupedCount();

        var result = fixture.Compile();

        Assert.True(result.IsSuccessful, Diagnostics(result));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(ElasticRelationQueryPagingKind.CompositeAfter, artifact.Paging?.Kind);
        Assert.Equal(
            [ElasticRelationQueryResultSourceKind.CompositeDocumentCount, ElasticRelationQueryResultSourceKind.CompositeKey],
            artifact.ResultFields.Select(static field => field.SourceKind));
        var request = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("cursor")] = ObservationValue.FromString("ready")
        });
        using var json = ElasticSdkRequestTestSupport.Serialize(request);
        var composite = json.RootElement
            .GetProperty("aggregations")
            .GetProperty("groups")
            .GetProperty("composite");
        Assert.Equal(20, composite.GetProperty("size").GetInt32());
        Assert.Equal("ready", composite.GetProperty("after").GetProperty("g0").GetString());
        Assert.Equal(
            "status.keyword",
            composite.GetProperty("sources")[0].GetProperty("g0").GetProperty("terms").GetProperty("field").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("size").GetInt32());
    }

    [Fact]
    public void Compile_UnsupportedCrossSourceRelationAndOptionalNull_FailClosed()
    {
        var crossSource = Fixture.CrossSourceJoin().Compile();
        var relation = Fixture.Relation().Compile();
        var optionalNull = Fixture.Row(optionalPredicate: true).Compile();

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, crossSource.Status);
        Assert.Contains(crossSource.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            && diagnostic.Message.Contains("exactly one placed source contract", StringComparison.Ordinal));
        Assert.Empty(crossSource.Artifacts);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, relation.Status);
        Assert.Contains(relation.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.RelationTerminalUnsupported);
        Assert.Empty(relation.Artifacts);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, optionalNull.Status);
        Assert.Contains(optionalNull.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
            && diagnostic.Message.Contains("missing or null", StringComparison.Ordinal));
        Assert.Empty(optionalNull.Artifacts);
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
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            && diagnostic.Message.Contains("compiled-plan affinity", StringComparison.Ordinal));
        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, placementReuse.Status);
        Assert.Contains(placementReuse.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            && diagnostic.Message.Contains("source-placement affinity", StringComparison.Ordinal));
        Assert.Empty(planReuse.Artifacts);
        Assert.Empty(placementReuse.Artifacts);
    }

    [Fact]
    public void Compile_ReorderedEquivalentBindingsHaveDeterministicFingerprints()
    {
        var fixture = Fixture.Suffix();
        var reversedBinding = fixture.StorageBindingWithFields([.. fixture.StorageBinding.Fields.Reverse()]);

        var first = Assert.Single(fixture.Compile().Artifacts);
        var second = Assert.Single(fixture.Compile(reversedBinding).Artifacts);

        Assert.Equal(first.StorageBinding.Fingerprint, second.StorageBinding.Fingerprint);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        var firstRequest = first.RequestTemplate.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("suffix")] = ObservationValue.FromString("Inc")
        });
        var secondRequest = second.RequestTemplate.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("suffix")] = ObservationValue.FromString("Inc")
        });
        Assert.Equal(
            Assert.Single(firstRequest.Indices!).ToString(),
            Assert.Single(secondRequest.Indices!).ToString());
        Assert.Equal(firstRequest.AllowPartialSearchResults, secondRequest.AllowPartialSearchResults);
        Assert.Equal(
            ElasticSdkRequestTestSupport.SerializeToString(firstRequest),
            ElasticSdkRequestTestSupport.SerializeToString(secondRequest));
        Assert.Equal(
            first.LoweringDecisions.Select(static decision => decision.Decision.Fingerprint),
            second.LoweringDecisions.Select(static decision => decision.Decision.Fingerprint));
    }

    static ElasticQueryLoweringPolicy SuffixPolicy(
        ElasticQueryLoweringFallbackPolicy fallback,
        params ElasticQueryLoweringStrategyId[] strategies) =>
        ElasticQueryLoweringPolicy.CreateConventional(
            additionalPreferences:
            [
                new(
                    ElasticQueryLoweringOperation.Suffix,
                    ElasticQueryLoweringPreferenceOrigin.ExplicitLocal,
                    fallback,
                    [.. strategies])
            ]);

    static ElasticQueryLoweringPolicy ExtensionSuffixPolicy(IElasticQueryLoweringStrategy strategy) =>
        ElasticQueryLoweringPolicy.CreateConventional(
            additionalStrategies: [strategy],
            additionalPreferences:
            [
                new(
                    ElasticQueryLoweringOperation.Suffix,
                    ElasticQueryLoweringPreferenceOrigin.ExplicitLocal,
                    ElasticQueryLoweringFallbackPolicy.RequirePreferred,
                    [strategy.Id])
            ]);

    static void AssertPaginationRequiresStableSearchView(
        ElasticRelationQueryCompilationResult result,
        string mechanism)
    {
        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable);
        Assert.Contains(mechanism, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("unchanged search-visible view", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
    }

    static string Diagnostics(ElasticRelationQueryCompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));

    static string Diagnostics(RelationQueryBoundRealizationReport report) =>
        string.Join(Environment.NewLine, report.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));

    static JsonElement FirstFilter(JsonElement root)
    {
        var filter = root.GetProperty("query").GetProperty("bool").GetProperty("filter");
        return filter.ValueKind == JsonValueKind.Array ? filter[0] : filter;
    }

    sealed class UndeclaredParameterSuffixStrategy : IElasticQueryLoweringStrategy
    {
        public ElasticQueryLoweringStrategyId Id { get; } = new("tests/suffix/undeclared-parameter/v1");

        public ElasticQueryLoweringOperation Operation => ElasticQueryLoweringOperation.Suffix;

        public ElasticQueryLoweringStrategyResult TryLower(ElasticQueryLoweringContext context) =>
            ElasticQueryLoweringStrategyResult.Eligible(
                ElasticQueryTemplate.Prefix(
                    "customerName.keyword",
                    ElasticQueryValueTemplate.FromParameter(new("undeclared"))),
                "Intentionally invalid parameter-containment test strategy.");
    }

    sealed class RogueFieldSuffixStrategy : IElasticQueryLoweringStrategy
    {
        public ElasticQueryLoweringStrategyId Id { get; } = new("tests/suffix/rogue-field/v1");

        public ElasticQueryLoweringOperation Operation => ElasticQueryLoweringOperation.Suffix;

        public ElasticQueryLoweringStrategyResult TryLower(ElasticQueryLoweringContext context) =>
            ElasticQueryLoweringStrategyResult.Eligible(
                ElasticQueryTemplate.Prefix("rogue.keyword", context.Value),
                "Intentionally invalid field-containment test strategy.");
    }

    sealed class ThrowingSuffixStrategy : IElasticQueryLoweringStrategy
    {
        public ElasticQueryLoweringStrategyId Id { get; } = new("tests/suffix/throws/v1");

        public ElasticQueryLoweringOperation Operation => ElasticQueryLoweringOperation.Suffix;

        public ElasticQueryLoweringStrategyResult TryLower(ElasticQueryLoweringContext context) =>
            throw new IOException("Intentional extension failure.");
    }

    sealed class Fixture
    {
        static readonly GraphId Graph = new("elastic-compiler-tests/v1");
        static readonly QualifiedShapeId LoadShape = new(Graph, new ShapeId("Load"));
        static readonly QualifiedShapeId CustomerShape = new(Graph, new ShapeId("Customer"));
        static readonly QualifiedShapeId RowShape = new(Graph, new ShapeId("LoadRow"));
        static readonly QualifiedShapeId TemporalRowShape = new(Graph, new ShapeId("LoadTemporalRow"));
        static readonly QualifiedShapeId CountShape = new(Graph, new ShapeId("LoadCount"));
        static readonly QualifiedShapeId GroupedCountShape = new(Graph, new ShapeId("LoadGroupedCount"));
        static readonly QualifiedShapeId TemporalGroupedCountShape = new(Graph, new ShapeId("LoadTemporalGroupedCount"));
        static readonly ValueBindingId Load = new("load");
        static readonly ValueBindingId Customer = new("customer");
        static readonly ValueBindingId RowBinding = new("row");
        static readonly ValueBindingId AggregateBinding = new("aggregate");
        static readonly QueryNodeId LoadSource = new("loads");
        static readonly QueryNodeId CustomerSource = new("customers");
        static readonly QueryNodeId Filter = new("filter-loads");
        static readonly QueryNodeId Project = new("project-row");
        static readonly QueryNodeId Aggregate = new("aggregate-loads");
        static readonly QueryNodeId Order = new("order-results");
        static readonly QueryNodeId Page = new("page-results");
        static readonly QueryResultId Rows = new("rows");
        static readonly QueryResultId Aggregations = new("aggregations");
        static readonly QueryParameterId StatusParameter = new("status");
        static readonly QueryParameterId SuffixParameter = new("suffix");
        static readonly QueryParameterId CustomerNameSuffixParameter = new("customer-name-suffix");
        static readonly QueryParameterId LocationParameter = new("location");
        static readonly QueryParameterId CursorParameter = new("cursor");
        static readonly QueryParameterId InstantParameter = new("instant");

        public static readonly FieldPath IdPath = FieldPath.FromField("Id");
        static readonly FieldPath CustomerIdPath = FieldPath.FromField("CustomerId");
        public static readonly FieldPath StatusPath = FieldPath.FromField("Status");
        public static readonly FieldPath CustomerNamePath = FieldPath.FromField("CustomerName");
        public static readonly FieldPath StopLocationsPath = FieldPath.FromField("StopLocations");
        public static readonly FieldPath StopsPath = FieldPath.FromField("Stops");
        public static readonly FieldPath StopLocationPath = FieldPath.FromField("Location");
        public static readonly FieldPath StopTypePath = FieldPath.FromField("Type");
        static readonly FieldPath NotesPath = FieldPath.FromField("Notes");
        static readonly FieldPath OccurredAtPath = FieldPath.FromField("OccurredAt");
        static readonly FieldPath CountPath = FieldPath.FromField("Count");

        Fixture(
            CompiledRelationQueryPlan plan,
            RelationQueryRealizationReport realization,
            RelationQuerySourcePlacement placement,
            ElasticRelationQueryStorageBinding storageBinding)
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

        public ElasticRelationQueryStorageBinding StorageBinding { get; }

        public ElasticRelationQueryCompilationResult Compile(
            ElasticRelationQueryStorageBinding? storageBinding = null,
            RelationQueryBoundRealizationRequest? request = null,
            ElasticQueryLoweringPolicy? loweringPolicy = null) =>
            new ElasticRelationQueryCompiler(loweringPolicy: loweringPolicy).Compile(
                request ?? new(Plan, Realization, Placement),
                storageBinding ?? StorageBinding);

        public ElasticRelationQueryStorageBinding StorageBindingWithAffinity() => new(
            StorageBinding.Id,
            StorageBinding.Source,
            StorageBinding.PlacementBinding,
            StorageBinding.Target,
            StorageBinding.TargetProfile,
            StorageBinding.IndexName,
            StorageBinding.Fields,
            StorageBinding.SourceMode,
            StorageBinding.MaximumResultWindow,
            StorageBinding.MaximumPageSize,
                StorageBinding.PaginationConsistency,
                StorageBinding.Origin,
                StorageBinding.ConventionSetVersion,
                StorageBinding.ConfigurationDecisions,
                StorageBinding.CompiledPlanFingerprint,
                StorageBinding.PlacementFingerprint);

        public ElasticRelationQueryStorageBinding StorageBindingWithFields(
            ImmutableArray<ElasticRelationQueryFieldBinding> fields) => new(
                StorageBinding.Id,
                StorageBinding.Source,
                StorageBinding.PlacementBinding,
                StorageBinding.Target,
                StorageBinding.TargetProfile,
                StorageBinding.IndexName,
                fields,
                StorageBinding.SourceMode,
                StorageBinding.MaximumResultWindow,
                StorageBinding.MaximumPageSize,
                StorageBinding.PaginationConsistency,
                StorageBinding.Origin,
                StorageBinding.ConventionSetVersion,
                StorageBinding.ConfigurationDecisions,
                StorageBinding.CompiledPlanFingerprint,
                StorageBinding.PlacementFingerprint);

        public ElasticRelationQueryStorageBinding StorageBindingWithSuffixCapabilities(
            ElasticRelationQueryFieldSemanticCapabilities suffixCapabilities)
        {
            var input = InputFor(CustomerNamePath);
            return StorageBindingWithFields(
            [
                .. StorageBinding.Fields.Select(field => field.Input == input
                    ? Rebind(
                        field,
                        (field.SemanticCapabilities
                         & ~(ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix
                             | ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix))
                        | suffixCapabilities)
                    : field)
            ]);
        }

        public ElasticRelationQueryStorageBinding StorageBindingWithoutStableUniqueOrdering()
        {
            var input = InputFor(IdPath);
            return StorageBindingWithFields(
            [
                .. StorageBinding.Fields.Select(field => field.Input == input
                    ? Rebind(
                        field,
                        field.SemanticCapabilities
                        & ~ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering)
                    : field)
            ]);
        }

        public ElasticRelationQueryStorageBinding StorageBindingWithoutCollectionMembership() =>
            StorageBindingWithField(
                StopLocationsPath,
                field => Rebind(
                    field,
                    field.SemanticCapabilities
                    & ~ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership));

        public ElasticRelationQueryStorageBinding StorageBindingWithFlattenedStops() =>
            StorageBindingWithField(
                StopsPath,
                field => new(
                    field.Input,
                    sourceField: null,
                    queryField: FieldPath.Parse("stops"),
                    mappingKind: ElasticRelationQueryFieldMappingKind.Object,
                    retrievalKind: ElasticRelationQueryFieldRetrievalKind.Unavailable,
                    retrievalEncoding: null,
                    documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument));

        public ElasticRelationQueryStorageBinding StorageBindingWithoutNestedEvidence() =>
            StorageBindingWithField(
                StopsPath,
                field => new(
                    field.Input,
                    field.SourceField,
                    field.QueryField,
                    field.MappingKind,
                    field.RetrievalKind,
                    field.RetrievalEncoding,
                    field.DocumentScope,
                    field.SemanticCapabilities,
                    field.ReversedSuffixField,
                    field.SemanticProfile,
                    field.MissingValueBehavior,
                    field.MissingValueSentinel,
                    field.NullValueBehavior,
                    field.NullValueSentinel,
                    nestedScope: null));

        public ElasticRelationQueryStorageBinding StorageBindingWithNestedScope(
            ElasticRelationQueryNestedScopeEvidence nestedScope) =>
            StorageBindingWithField(
                StopsPath,
                field => new(
                    field.Input,
                    field.SourceField,
                    field.QueryField,
                    field.MappingKind,
                    field.RetrievalKind,
                    field.RetrievalEncoding,
                    field.DocumentScope,
                    field.SemanticCapabilities,
                    field.ReversedSuffixField,
                    field.SemanticProfile,
                    field.MissingValueBehavior,
                    field.MissingValueSentinel,
                    field.NullValueBehavior,
                    field.NullValueSentinel,
                    nestedScope));

        public ElasticRelationQueryStorageBinding StorageBindingWithStopsAbsence(
            ElasticRelationQueryMissingValueBehavior missingValueBehavior,
            ElasticRelationQueryNullValueBehavior nullValueBehavior) =>
            StorageBindingWithField(
                StopsPath,
                field => new(
                    field.Input,
                    field.SourceField,
                    field.QueryField,
                    field.MappingKind,
                    field.RetrievalKind,
                    field.RetrievalEncoding,
                    field.DocumentScope,
                    field.SemanticCapabilities,
                    field.ReversedSuffixField,
                    field.SemanticProfile,
                    missingValueBehavior,
                    missingValueSentinel: null,
                    nullValueBehavior,
                    nullValueSentinel: null,
                    nestedScope: field.NestedScope));

        public ElasticRelationQueryNestedScopeEvidence StopsNestedScope =>
            StorageBinding.ResolveField(InputFor(StopsPath)).NestedScope!;

        public ElasticRelationQueryStorageBinding StorageBindingWithBoundaries(
            int maximumResultWindow,
            int maximumPageSize) => new(
                StorageBinding.Id,
                StorageBinding.Source,
                StorageBinding.PlacementBinding,
                StorageBinding.Target,
                StorageBinding.TargetProfile,
                StorageBinding.IndexName,
                StorageBinding.Fields,
                StorageBinding.SourceMode,
                maximumResultWindow,
                maximumPageSize,
                StorageBinding.PaginationConsistency,
                StorageBinding.Origin,
                StorageBinding.ConventionSetVersion,
                StorageBinding.ConfigurationDecisions,
                StorageBinding.CompiledPlanFingerprint,
                StorageBinding.PlacementFingerprint);

        public ElasticRelationQueryStorageBinding StorageBindingWithPaginationConsistency(
            ElasticRelationQueryPaginationConsistency paginationConsistency) => new(
                StorageBinding.Id,
                StorageBinding.Source,
                StorageBinding.PlacementBinding,
                StorageBinding.Target,
                StorageBinding.TargetProfile,
                StorageBinding.IndexName,
                StorageBinding.Fields,
                StorageBinding.SourceMode,
                StorageBinding.MaximumResultWindow,
                StorageBinding.MaximumPageSize,
                paginationConsistency,
                StorageBinding.Origin,
                StorageBinding.ConventionSetVersion,
                StorageBinding.ConfigurationDecisions,
                StorageBinding.CompiledPlanFingerprint,
                StorageBinding.PlacementFingerprint);

        public ElasticRelationQueryStorageBinding StorageBindingWithRetrievalEncoding(
            FieldPath path,
            ElasticRelationQueryFieldValueEncoding retrievalEncoding) =>
            StorageBindingWithField(path, field => Rebind(field, retrievalEncoding: retrievalEncoding));

        public ElasticRelationQueryStorageBinding StorageBindingWithDocumentScope(
            FieldPath path,
            ElasticRelationQueryFieldDocumentScope documentScope) =>
            StorageBindingWithField(path, field => Rebind(field, documentScope: documentScope));

        ElasticRelationQueryStorageBinding StorageBindingWithField(
            FieldPath path,
            Func<ElasticRelationQueryFieldBinding, ElasticRelationQueryFieldBinding> transform)
        {
            var input = InputFor(path);
            return StorageBindingWithFields(
            [
                .. StorageBinding.Fields.Select(field => field.Input == input ? transform(field) : field)
            ]);
        }

        RelationQueryInputId InputFor(FieldPath path) => Plan.InputContract.Sources
            .SelectMany(static source => source.Fields)
            .Single(field => field.Input.Binding == Load && field.Input.Field.Path == path)
            .Input.Id;

        static ElasticRelationQueryFieldBinding Rebind(
            ElasticRelationQueryFieldBinding field,
            ElasticRelationQueryFieldSemanticCapabilities? capabilities = null,
            ElasticRelationQueryFieldValueEncoding? retrievalEncoding = null,
            ElasticRelationQueryFieldDocumentScope? documentScope = null) => new(
                field.Input,
                field.SourceField,
                field.QueryField,
                field.MappingKind,
                field.RetrievalKind,
                retrievalEncoding ?? field.RetrievalEncoding,
                documentScope ?? field.DocumentScope,
                semanticCapabilities: capabilities ?? field.SemanticCapabilities,
                reversedSuffixField: (capabilities ?? field.SemanticCapabilities)
                    .HasFlag(ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix)
                    ? field.ReversedSuffixField ?? FieldPath.Parse("customerName.reversed")
                    : null,
                semanticProfile: field.SemanticProfile,
                missingValueBehavior: field.MissingValueBehavior,
                missingValueSentinel: field.MissingValueSentinel,
                nullValueBehavior: field.NullValueBehavior,
                nullValueSentinel: field.NullValueSentinel,
                nestedScope: field.NestedScope);

        public static Fixture Row(
            int offset = 5,
            int limit = 25,
            bool optionalPredicate = false)
        {
            var predicatePath = optionalPredicate ? NotesPath : StatusPath;
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
                            Expr.Eq(Expr.Field(Load, predicatePath), Expr.Param(StatusParameter.Value))),
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
                        new PageQueryNode(Page, Order, new OffsetPageDefinition(limit, offset))
                    ],
                    parameters:
                    [
                        new(StatusParameter, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: optionalPredicate);
        }

        public static Fixture KeysetRow()
        {
            IRQueryDefinition definition = new(
                new("keyset-row-query"),
                new("KeysetRowQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Eq(Expr.Field(Load, StatusPath), Expr.Param(StatusParameter.Value))),
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
                        new PageQueryNode(
                            Page,
                            Order,
                            new KeysetPageDefinition(25, [Expr.Param(CursorParameter.Value)]))
                    ],
                    parameters:
                    [
                        new(StatusParameter, new ScalarTypeRef(ScalarTypeKind.String)),
                        new(CursorParameter, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture IndependentSources()
        {
            QueryNodeId customerOrder = new("order-customers");
            QueryNodeId customerPage = new("page-customers");
            IRQueryDefinition definition = new(
                new("independent-source-query"),
                new("IndependentSourceQuery"),
                new(
                    nodes:
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
                        new OrderQueryNode(Order, Project, [new(Expr.Field(RowBinding, IdPath))]),
                        new PageQueryNode(Page, Order, new OffsetPageDefinition(limit: 25, offset: 0)),
                        new SourceQueryNode(CustomerSource, Customer, CustomerShape),
                        new OrderQueryNode(customerOrder, CustomerSource, [new(Expr.Field(Customer, IdPath))]),
                        new PageQueryNode(customerPage, customerOrder, new OffsetPageDefinition(limit: 25, offset: 0))
                    ]),
                [
                    new RowsQueryResultDefinition(new("load-rows"), Page),
                    new RowsQueryResultDefinition(new("customer-rows"), customerPage)
                ]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true,
                storageBinding: Load);
        }

        public static Fixture Suffix()
        {
            IRQueryDefinition definition = new(
                new("suffix-query"),
                new("SuffixQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.EndsWith(
                                Expr.Field(Load, CustomerNamePath),
                                Expr.Param(SuffixParameter.Value))),
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
                        new PageQueryNode(Page, Order, new OffsetPageDefinition(25, 0))
                    ],
                    parameters:
                    [
                        new(SuffixParameter, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture TwoSuffixes()
        {
            QueryParameterId firstSuffix = new("first-suffix");
            QueryParameterId secondSuffix = new("second-suffix");
            IRQueryDefinition definition = new(
                new("two-suffix-query"),
                new("TwoSuffixQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.And(
                                Expr.EndsWith(
                                    Expr.Field(Load, CustomerNamePath),
                                    Expr.Param(firstSuffix.Value)),
                                Expr.EndsWith(
                                    Expr.Field(Load, CustomerNamePath),
                                    Expr.Param(secondSuffix.Value)))),
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
                        new PageQueryNode(Page, Order, new OffsetPageDefinition(25, 0))
                    ],
                    parameters:
                    [
                        new(firstSuffix, new ScalarTypeRef(ScalarTypeKind.String)),
                        new(secondSuffix, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture CollectionMembership(
            Expr? value = null,
            ScalarTypeKind valueKind = ScalarTypeKind.String)
        {
            value ??= Expr.Param(LocationParameter.Value);
            ImmutableArray<QueryParameterDefinition> parameters = value is ParameterExpr
                ? [new(LocationParameter, new ScalarTypeRef(valueKind))]
                : [];
            IRQueryDefinition definition = new(
                new("collection-membership-query"),
                new("CollectionMembershipQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Contains(
                                Expr.Field(Load, StopLocationsPath),
                                value)),
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
                        new PageQueryNode(Page, Order, new OffsetPageDefinition(25, 0))
                    ],
                    parameters: parameters),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture StructuredCollectionAny()
        {
            IRQueryDefinition definition = new(
                new("structured-collection-any-query"),
                new("StructuredCollectionAnyQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Any(
                                Expr.Field(Load, StopsPath),
                                Expr.And(
                                    Expr.Eq(
                                        Expr.Field($"{ExprFieldRoots.CurrentItem}.Location"),
                                        Expr.Param(LocationParameter.Value)),
                                    Expr.Eq(
                                        Expr.Field($"{ExprFieldRoots.CurrentItem}.Type"),
                                        Expr.Const("Pickup"))))),
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
                        new PageQueryNode(Page, Order, new OffsetPageDefinition(25, 0))
                    ],
                    parameters:
                    [
                        new(LocationParameter, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture LoadSearch()
        {
            IRQueryDefinition definition = new(
                new("load-search-query"),
                new("LoadSearchQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.And(
                                Expr.EndsWith(
                                    Expr.Field(Load, CustomerNamePath),
                                    Expr.Param(CustomerNameSuffixParameter.Value)),
                                Expr.Contains(
                                    Expr.Field(Load, StopLocationsPath),
                                    Expr.Param(LocationParameter.Value)))),
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
                        new PageQueryNode(Page, Order, new OffsetPageDefinition(25, 0)),
                        new AggregateQueryNode(
                            Aggregate,
                            Filter,
                            AggregateBinding,
                            CountShape,
                            aggregates:
                            [
                                new(new("count-loads"), CountPath, AggregateOperator.Count)
                            ])
                    ],
                    parameters:
                    [
                        new(CustomerNameSuffixParameter, new ScalarTypeRef(ScalarTypeKind.String)),
                        new(LocationParameter, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [
                    new RowsQueryResultDefinition(Rows, Page),
                    new AggregationQueryResultDefinition(Aggregations, Aggregate)
                ]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture GlobalCount()
        {
            IRQueryDefinition definition = new(
                new("global-count-query"),
                new("GlobalCountQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Eq(Expr.Field(Load, StatusPath), Expr.Param(StatusParameter.Value))),
                        new AggregateQueryNode(
                            Aggregate,
                            Filter,
                            AggregateBinding,
                            CountShape,
                            aggregates:
                            [
                                new(new("count-loads"), CountPath, AggregateOperator.Count)
                            ])
                    ],
                    parameters:
                    [
                        new(StatusParameter, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new AggregationQueryResultDefinition(Aggregations, Aggregate)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture FieldlessGlobalCount()
        {
            IRQueryDefinition definition = new(
                new("fieldless-global-count-query"),
                new("FieldlessGlobalCountQuery"),
                new(nodes:
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new AggregateQueryNode(
                        Aggregate,
                        LoadSource,
                        AggregateBinding,
                        CountShape,
                        aggregates:
                        [
                            new(new("count-loads"), CountPath, AggregateOperator.Count)
                        ])
                ]),
                [new AggregationQueryResultDefinition(Aggregations, Aggregate)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture GroupedCount()
        {
            IRQueryDefinition definition = new(
                new("grouped-count-query"),
                new("GroupedCountQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new AggregateQueryNode(
                            Aggregate,
                            LoadSource,
                            AggregateBinding,
                            GroupedCountShape,
                            groupings:
                            [
                                new(new("group-status"), StatusPath, Expr.Field(Load, StatusPath))
                            ],
                            aggregates:
                            [
                                new(new("count-loads"), CountPath, AggregateOperator.Count)
                            ]),
                        new OrderQueryNode(Order, Aggregate, [new(Expr.Field(AggregateBinding, StatusPath))]),
                        new PageQueryNode(
                            Page,
                            Order,
                            new KeysetPageDefinition(20, [Expr.Param(CursorParameter.Value)]))
                    ],
                    parameters:
                    [
                        new(CursorParameter, new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                [new AggregationQueryResultDefinition(Aggregations, Page)]);
            return Create(RelationQueryDocument.FromDefinition(definition));
        }

        public static Fixture TemporalComparison()
        {
            IRQueryDefinition definition = new(
                new("temporal-comparison-query"),
                new("TemporalComparisonQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new FilterQueryNode(
                            Filter,
                            LoadSource,
                            Expr.Gt(Expr.Field(Load, OccurredAtPath), Expr.Param(InstantParameter.Value))),
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
                        new PageQueryNode(Page, Order, new OffsetPageDefinition(25, 0))
                    ],
                    parameters:
                    [
                        new(InstantParameter, new ScalarTypeRef(ScalarTypeKind.Instant))
                    ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        public static Fixture TemporalOrdering()
        {
            IRQueryDefinition definition = new(
                new("temporal-ordering-query"),
                new("TemporalOrderingQuery"),
                new(
                [
                    new SourceQueryNode(LoadSource, Load, LoadShape),
                    new ProjectQueryNode(
                        Project,
                        LoadSource,
                        RowBinding,
                        TemporalRowShape,
                        [
                            new(new("row-id"), IdPath, Expr.Field(Load, IdPath)),
                            new(new("row-occurred-at"), OccurredAtPath, Expr.Field(Load, OccurredAtPath))
                        ]),
                    new OrderQueryNode(
                        Order,
                        Project,
                        [
                            new(Expr.Field(RowBinding, OccurredAtPath)),
                            new(Expr.Field(RowBinding, IdPath))
                        ]),
                    new PageQueryNode(Page, Order, new OffsetPageDefinition(25, 0))
                ]),
                [new RowsQueryResultDefinition(Rows, Page)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
        }

        public static Fixture TemporalGrouping()
        {
            IRQueryDefinition definition = new(
                new("temporal-grouping-query"),
                new("TemporalGroupingQuery"),
                new(
                    nodes:
                    [
                        new SourceQueryNode(LoadSource, Load, LoadShape),
                        new AggregateQueryNode(
                            Aggregate,
                            LoadSource,
                            AggregateBinding,
                            TemporalGroupedCountShape,
                            groupings:
                            [
                                new(new("group-occurred-at"), OccurredAtPath, Expr.Field(Load, OccurredAtPath))
                            ],
                            aggregates:
                            [
                                new(new("count-loads"), CountPath, AggregateOperator.Count)
                            ]),
                        new OrderQueryNode(Order, Aggregate, [new(Expr.Field(AggregateBinding, OccurredAtPath))]),
                        new PageQueryNode(
                            Page,
                            Order,
                            new KeysetPageDefinition(20, [Expr.Param(InstantParameter.Value)]))
                    ],
                    parameters:
                    [
                        new(InstantParameter, new ScalarTypeRef(ScalarTypeKind.Instant))
                    ]),
                [new AggregationQueryResultDefinition(Aggregations, Page)]);
            return Create(
                RelationQueryDocument.FromDefinition(definition),
                overrideUnavailableRequirements: true);
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
            ValueBindingId? storageBinding = null)
        {
            var compilation = RelationQueryStaticCompiler.Compile(new(
                document,
                [ShapeDocument()]));
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
            var sourcePlacement = storageBinding is { } selectedBinding
                ? placement.Bindings.Single(binding =>
                    binding.Kind == RelationQuerySourcePlacementBindingKind.SourceSet
                    && binding.Binding == selectedBinding)
                : placement.Bindings.First(static binding =>
                    binding.Kind == RelationQuerySourcePlacementBindingKind.SourceSet);
            var sourceContract = plan.InputContract.Sources.Single(source => source.Node == sourcePlacement.Node);
            var storage = new ElasticRelationQueryStorageBinding(
                new("tests/elastic-binding/v1"),
                sourcePlacement.Source,
                sourcePlacement.Id,
                ElasticRelationQueryTargetProfile.Target,
                ElasticRelationQueryTargetProfile.ProfileId,
                "loads-read",
                [.. sourceContract.Fields.Select(CreateFieldBinding)],
                paginationConsistency: ElasticRelationQueryPaginationConsistency.StableSearchView,
                conventionSetVersion: ElasticRelationQueryStorageBinding.SemanticPathConventionSet,
                compiledPlanFingerprint: RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                    RelationQueryCompiledPlanReference.From(plan)),
                placementFingerprint: placement.Fingerprint);
            return new(plan, realization, placement, storage);
        }

        static ElasticRelationQueryFieldBinding CreateFieldBinding(RelationQueryFieldInputContract contract)
        {
            var path = contract.Input.Field.Path;
            if (path == StopLocationsPath)
            {
                return new(
                    contract.Input.Id,
                    sourceField: null,
                    FieldPath.Parse("stopLocations.keyword"),
                    ElasticRelationQueryFieldMappingKind.Keyword,
                    retrievalKind: ElasticRelationQueryFieldRetrievalKind.Unavailable,
                    retrievalEncoding: null,
                    documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
                    semanticCapabilities: ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership,
                    semanticProfile: "tests/ordinal-keyword-array-v1");
            }
            if (path == StopsPath)
            {
                return new(
                    contract.Input.Id,
                    sourceField: null,
                    queryField: FieldPath.Parse("stops"),
                    mappingKind: ElasticRelationQueryFieldMappingKind.Nested,
                    retrievalKind: ElasticRelationQueryFieldRetrievalKind.Unavailable,
                    retrievalEncoding: null,
                    documentScope: ElasticRelationQueryFieldDocumentScope.NestedDocument,
                    missingValueBehavior: ElasticRelationQueryMissingValueBehavior.ProhibitedByIngestion,
                    nullValueBehavior: ElasticRelationQueryNullValueBehavior.ProhibitedByIngestion,
                    nestedScope: new(
                        FieldPath.Parse("stops"),
                        ElasticRelationQueryNestedCorrelationGuarantee.SameNestedDocument,
                        ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion,
                        ElasticRelationQueryEmptyCollectionBehavior.NoNestedDocuments,
                        [
                            new(
                                StopLocationPath,
                                FieldPath.Parse("stops.location.keyword"),
                                ElasticRelationQueryFieldMappingKind.Keyword,
                                ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                                "tests/ordinal-keyword-v1",
                                ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion,
                                ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion),
                            new(
                                StopTypePath,
                                FieldPath.Parse("stops.type.keyword"),
                                ElasticRelationQueryFieldMappingKind.Keyword,
                                ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                                "tests/ordinal-keyword-v1",
                                ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion,
                                ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion)
                        ]));
            }
            var physical = path == IdPath
                ? (Source: FieldPath.Parse("id"), Query: FieldPath.Parse("id.keyword"),
                    Mapping: ElasticRelationQueryFieldMappingKind.Keyword,
                    Encoding: ElasticRelationQueryFieldValueEncoding.JsonString)
                : path == StatusPath
                    ? (Source: FieldPath.Parse("status"), Query: FieldPath.Parse("status.keyword"),
                        Mapping: ElasticRelationQueryFieldMappingKind.Keyword,
                        Encoding: ElasticRelationQueryFieldValueEncoding.JsonString)
                    : path == CustomerNamePath
                        ? (Source: FieldPath.Parse("customerName"), Query: FieldPath.Parse("customerName.keyword"),
                            Mapping: ElasticRelationQueryFieldMappingKind.Keyword,
                            Encoding: ElasticRelationQueryFieldValueEncoding.JsonString)
                        : path == CustomerIdPath
                            ? (Source: FieldPath.Parse("customerId"), Query: FieldPath.Parse("customerId.keyword"),
                                Mapping: ElasticRelationQueryFieldMappingKind.Keyword,
                                Encoding: ElasticRelationQueryFieldValueEncoding.JsonString)
                            : path == NotesPath
                                ? (Source: FieldPath.Parse("notes"), Query: FieldPath.Parse("notes.keyword"),
                                    Mapping: ElasticRelationQueryFieldMappingKind.Keyword,
                                    Encoding: ElasticRelationQueryFieldValueEncoding.JsonString)
                                : path == OccurredAtPath
                                    ? (Source: FieldPath.Parse("occurredAt"), Query: FieldPath.Parse("occurredAt"),
                                        Mapping: ElasticRelationQueryFieldMappingKind.Date,
                                        Encoding: ElasticRelationQueryFieldValueEncoding.CanonicalTemporalString)
                                    : throw new InvalidOperationException($"No test Elasticsearch field convention exists for '{path}'.");
            var capabilities = ElasticRelationQueryFieldSemanticCapabilities.ExactTerm
                               | ElasticRelationQueryFieldSemanticCapabilities.ExactRange;
            if (path == IdPath)
            {
                capabilities |= ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                                | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering
                                | ElasticRelationQueryFieldSemanticCapabilities.ExactAggregation;
            }
            else if (path == StatusPath)
            {
                capabilities |= ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                                | ElasticRelationQueryFieldSemanticCapabilities.ExactAggregation;
            }
            else if (path == CustomerNamePath)
            {
                capabilities |= ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix
                                | ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix;
            }
            else if (path == OccurredAtPath)
            {
                capabilities |= ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                                | ElasticRelationQueryFieldSemanticCapabilities.ExactAggregation;
            }
            return new(
                contract.Input.Id,
                physical.Source,
                physical.Query,
                physical.Mapping,
                retrievalKind: ElasticRelationQueryFieldRetrievalKind.Source,
                retrievalEncoding: physical.Encoding,
                documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
                semanticCapabilities: capabilities,
                reversedSuffixField: path == CustomerNamePath
                    ? FieldPath.Parse("customerName.reversed")
                    : null,
                semanticProfile: "tests/ordinal-keyword-v1");
        }

        static RelationQueryRealizationReport Realize(
            CompiledRelationQueryPlan plan,
            bool overrideUnavailableRequirements)
        {
            var baseline = RelationQueryRealizationCompiler.Compile(
                plan,
                ElasticRelationQueryTargetProfile.Default,
                ElasticRelationQueryTargetProfile.Policy,
                RelationQueryResultObservability.NotRequested);
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
                        new($"tests/elastic-unsupported-override/{index:D4}"),
                        decision.Requirement,
                        requirements[decision.Requirement].Capability,
                        preservedGuarantees: requirements[decision.Requirement].RequiredGuarantees,
                        justification: "Exercise the Elasticsearch compiler's fail-closed unsupported diagnostic."))
            ];
            var policy = new RelationQueryRealizationPolicy(
                new("tests/elastic-unsupported-policy/v1"),
                ElasticRelationQueryStorageBinding.SemanticPathConventionSet,
                constrainedRealizations: RelationQueryConstrainedRealizationPolicy.AllowValidated,
                overrides: overrides);
            return RelationQueryRealizationCompiler.Compile(
                plan,
                ElasticRelationQueryTargetProfile.Default,
                policy,
                RelationQueryResultObservability.NotRequested);
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
                        new("tests/elastic"),
                        ElasticRelationQueryTargetProfile.Default,
                        new(100, 10_000, 100, 4)))
            ];
            return new(
                RelationQuerySourcePlacement.CurrentSchemaVersion,
                RelationQueryCompiledPlanReference.From(plan),
                ElasticRelationQueryStorageBinding.SemanticPathConventionSet,
                sources,
                bindings);
        }

        static ShapeGraphDocument ShapeDocument()
        {
            var stringType = new ScalarTypeRef(ScalarTypeKind.String);
            var instantType = new ScalarTypeRef(ScalarTypeKind.Instant);
            var load = new Shape(
                LoadShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("CustomerId"), stringType),
                    new(new("Status"), stringType),
                    new(new("CustomerName"), stringType),
                    new(
                        new("StopLocations"),
                        stringType,
                        cardinality: FieldCardinality.Many),
                    new(
                        new("Stops"),
                        new ObjectTypeRef(
                        [
                            new("Location", stringType),
                            new("Type", stringType)
                        ]),
                        cardinality: FieldCardinality.Many),
                    new(new("OccurredAt"), instantType),
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
            var temporalRow = new Shape(
                TemporalRowShape.ShapeId,
                [
                    new(new("Id"), stringType, role: FieldRole.Identity),
                    new(new("OccurredAt"), instantType)
                ],
                role: ShapeRoles.Projection);
            var count = new Shape(
                CountShape.ShapeId,
                [
                    new(new("Count"), new ScalarTypeRef(ScalarTypeKind.Int64))
                ],
                role: ShapeRoles.Projection);
            var groupedCount = new Shape(
                GroupedCountShape.ShapeId,
                [
                    new(new("Status"), stringType),
                    new(new("Count"), new ScalarTypeRef(ScalarTypeKind.Int64))
                ],
                role: ShapeRoles.Projection);
            var temporalGroupedCount = new Shape(
                TemporalGroupedCountShape.ShapeId,
                [
                    new(new("OccurredAt"), instantType),
                    new(new("Count"), new ScalarTypeRef(ScalarTypeKind.Int64))
                ],
                role: ShapeRoles.Projection);
            return ShapeGraphDocument.FromGraph(new(
                Graph,
                [load, customer, row, temporalRow, count, groupedCount, temporalGroupedCount]));
        }
    }
}
