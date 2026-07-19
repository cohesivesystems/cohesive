using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Tests.Relations;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresRelationQueryCompilerTests
{
    static readonly PostgresRelationQueryTextSemantics OrdinalText = new(
        "C",
        PostgresRelationQueryTextEqualitySemantics.Ordinal);
    static readonly PostgresRelationQueryTextSemantics OrdinalAsciiText = new(
        "C",
        PostgresRelationQueryTextEqualitySemantics.Ordinal,
        PostgresRelationQueryTextOrderingSemantics.Ordinal,
        new(
            "ck_test_ascii_text",
            "tests/postgres/ascii-text-domain/v1"));
    static readonly PostgresRelationQueryColumnOptions OrdinalTextOptions = new(
        scalarType: PostgresRelationQueryScalarType.Text,
        textSemantics: OrdinalText);
    static readonly PostgresRelationQueryColumnOptions StableOrdinalTextOptions = new(
        scalarType: PostgresRelationQueryScalarType.Text,
        textSemantics: OrdinalAsciiText,
        ordering: PostgresRelationQueryOrderingCapability.Exact
            | PostgresRelationQueryOrderingCapability.StableUnique);
    static readonly PostgresRelationQueryNumericDomainEvidence ExactNumericDomain = new(
        precision: 28,
        scale: 4,
        validatedConstraintName: "ck_test_finite_clr_decimal",
        authority: "tests/postgres/exact-numeric-domain/v1");
    static readonly PostgresRelationQueryColumnOptions ExactDecimalOptions = new(
        numericDomain: ExactNumericDomain);
    static readonly PostgresRelationQueryColumnOptions ExactAggregateDecimalOptions = new(
        numericDomain: ExactNumericDomain,
        decimalAggregates: new(
            PostgresRelationQueryDecimalAggregateGuarantee.SumIntermediateRange
            | PostgresRelationQueryDecimalAggregateGuarantee.AverageRounding,
            domainEvidence: "tests/postgres/aggregate-domain-analysis/v1",
            authority: "tests/postgres/exact-aggregate-domain/v1"));
    static readonly PostgresRelationQueryColumnOptions ExactTemporalOptions = new(
        temporalDomain: new(
            validatedConstraintName: "ck_test_finite_microsecond_timestamp",
            authority: "tests/postgres/exact-temporal-domain/v1"));

    internal static RelationQueryAdapterConformanceCase CreateBoundRealizationConformanceCase() => new(
        "PostgreSQL",
        ObserveSupported,
        ObserveRejected);

    static RelationQuerySupportedContextObservation ObserveSupported()
    {
        var fixture = CreateLoadSearchRelationFixture();
        var request = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement);
        PostgresRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, fixture.Storage);
        var repeated = compiler.Realize(request, fixture.Storage);
        var compilation = compiler.Compile(
            new RelationQueryNativeCompilationRequest(
                fixture.Plan,
                bound,
                fixture.Placement.Placement),
            fixture.Storage);
        return new(
            bound,
            repeated,
            compilation.Status,
            [.. compilation.Artifacts.Select(static artifact => artifact.Provenance.BoundRealization)]);
    }

    static RelationQueryRejectedContextObservation ObserveRejected()
    {
        var fixture = CreateLoadSearchRelationFixture();
        var binding = CopyStorage(
            fixture.Storage,
            tables:
            [
                .. fixture.Storage.Tables.Select(table => CopyTable(
                    table,
                    fields:
                    [
                        .. table.Fields.Where(static field => !field.SemanticPath.Matches("name"))
                    ]))
            ]);
        var request = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement);
        PostgresRelationQueryCompiler compiler = new();
        var bound = compiler.Realize(request, binding);
        var compilation = compiler.Compile(request, binding);
        return new(bound, compilation.Status, compilation.Artifacts.Length);
    }

    [Fact]
    public void Compile_ExpressionAuthoredSuppliedLoadRelationTraversesCustomerAndBindsRootFields()
    {
        var fixture = CreateLoadSearchRelationFixture();

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(RelationQueryNativeResultKind.RelationRows, artifact.Branch.Kind);
        Assert.DoesNotContain("\"transport\".\"loads\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"transport\".\"customers\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"customer_id\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"customer_name\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"customer_type\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"LoadSearchDto_result\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"Load__customerId\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"Customer__name\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("AS \"customerName\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AS \"field_", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.DoesNotMatch("\"[qv][0-9]+\"", artifact.Statement.Text);
        Assert.All(artifact.ResultFields, static result =>
            Assert.Equal(result.Field.Path.ToString(), result.Alias));

        var statement = artifact.Bind(
            new Dictionary<RelationQueryInputId, ObservationValue>
            {
                [fixture.LoadId] = ObservationValue.FromString("load-1"),
                [fixture.CustomerId] = ObservationValue.FromString("customer-1")
            },
            new Dictionary<QueryParameterId, ObservationValue>());

        Assert.Equal(artifact.Statement.Text, statement.Text);
        Assert.Contains(
            statement.Parameters,
            static parameter => Equals(parameter.Value, "load-1"));
        Assert.Contains(
            statement.Parameters,
            static parameter => Equals(parameter.Value, "customer-1"));
        Assert.Equal(fixture.Storage.Fingerprint, artifact.StorageBinding.Fingerprint);
        Assert.Equal(fixture.Plan.Provenance.DefinitionFingerprint, artifact.Provenance.Plan.DefinitionFingerprint);
        Assert.Equal(fixture.Placement.Placement.Fingerprint, artifact.Provenance.Placement);
        Assert.Contains(
            artifact.LoweringDecisions,
            static decision => decision.Kind == PostgresRelationQueryLoweringDecisionKind.RelationRootCorrelation
                && decision.Strategy == "postgres/supplied-root-invocation-correlation/v1");

        var json = PostgresRelationQueryArtifactJsonSerializer.Serialize(artifact, indented: false);
        var rehydrated = PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(json);
        var rebound = rehydrated.Bind(
            new Dictionary<RelationQueryInputId, ObservationValue>
            {
                [fixture.LoadId] = ObservationValue.FromString("load-1"),
                [fixture.CustomerId] = ObservationValue.FromString("customer-1")
            },
            new Dictionary<QueryParameterId, ObservationValue>());
        Assert.Equal(artifact.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(json, PostgresRelationQueryArtifactJsonSerializer.Serialize(rehydrated, indented: false));
        Assert.Equal(statement.Parameters.Select(static parameter => parameter.Value),
            rebound.Parameters.Select(static parameter => parameter.Value));
    }

    [Fact]
    public void Realize_ExactBindingPredictsNativeCompilationAndRetainsContextualProof()
    {
        var fixture = CreateLoadSearchRelationFixture();
        var compiler = new PostgresRelationQueryCompiler();
        var request = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement);

        var bound = compiler.Realize(request, fixture.Storage);

        Assert.True(bound.IsRealizable, Format(bound.Diagnostics));
        Assert.Equal(fixture.Storage.Id.Value, bound.Evidence.Binding.BindingId);
        Assert.Equal(fixture.Storage.Fingerprint.Value, bound.Evidence.Binding.Fingerprint.Value);
        Assert.NotEmpty(bound.Evidence.Assessments);
        Assert.All(bound.Evidence.Assessments, static assessment =>
            Assert.Equal(RelationQueryBoundAssessmentStatus.Available, assessment.Status));
        var expectedTargetBoundaries = fixture.Realization.Decisions
            .SelectMany(static decision => decision switch
            {
                ConstrainedRelationQueryRealizationDecision constrained => constrained.BoundaryValidations,
                OverrideRelationQueryRealizationDecision overridden => overridden.BoundaryValidations,
                _ => []
            })
            .Where(static validation =>
                validation.Kind == RelationQueryOperatingBoundaryValidationKind.TargetEnforced)
            .Select(static validation => validation.Boundary)
            .ToHashSet();
        Assert.NotEmpty(expectedTargetBoundaries);
        Assert.True(expectedTargetBoundaries.SetEquals(
            bound.Evidence.Assessments.SelectMany(static assessment => assessment.OperatingBoundaries)));

        var convenience = compiler.Compile(request, fixture.Storage);
        var exact = compiler.Compile(
            new RelationQueryNativeCompilationRequest(
                fixture.Plan,
                bound,
                fixture.Placement.Placement),
            fixture.Storage);

        Assert.True(convenience.IsSuccessful, Format(convenience.Diagnostics));
        Assert.True(exact.IsSuccessful, Format(exact.Diagnostics));
        Assert.All(exact.Artifacts, artifact =>
        {
            Assert.Equal(bound.Fingerprint, artifact.Provenance.BoundRealization);
            Assert.Equal(bound.Evidence.Binding.Fingerprint, artifact.Provenance.AdapterBinding.Fingerprint);
            Assert.NotEmpty(artifact.Provenance.ContextEvidence);
        });
        Assert.Equal(
            convenience.Artifacts.Select(static artifact => artifact.Fingerprint),
            exact.Artifacts.Select(static artifact => artifact.Fingerprint));
    }

    [Fact]
    public void Realize_MissingDemandedFieldPredictsNativeRejection()
    {
        var fixture = CreateLoadSearchRelationFixture();
        var missingField = CopyStorage(
            fixture.Storage,
            tables:
            [
                .. fixture.Storage.Tables.Select(table => CopyTable(
                    table,
                    fields:
                    [
                        .. table.Fields.Where(static field => !field.SemanticPath.Matches("name"))
                    ]))
            ]);
        var compiler = new PostgresRelationQueryCompiler();
        var request = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement);

        var bound = compiler.Realize(request, missingField);
        var compilation = compiler.Compile(request, missingField);

        Assert.True(
            bound.Status == RelationQueryRealizationStatus.NotRealizable,
            Format(bound.Diagnostics));
        var primary = Assert.Single(
            bound.Evidence.Assessments,
            assessment => assessment.Status == RelationQueryBoundAssessmentStatus.Unavailable
                          && assessment.Message.Contains(
                              PostgresRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                              StringComparison.Ordinal));
        Assert.Equal(
            new RelationQueryAdapterDecisionCode(PostgresRelationQueryCompilationDiagnosticCodes.FieldBindingMissing),
            primary.AdapterDecisionCode);
        Assert.NotNull(primary.Input);
        Assert.NotNull(primary.Field);
        Assert.NotNull(primary.PlacementBinding);
        Assert.Contains(
            "/columnName",
            Assert.IsType<string>(primary.FailedConfigurationSetting),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            bound.Evidence.Assessments,
            static assessment => assessment.Status == RelationQueryBoundAssessmentStatus.Available);
        Assert.All(
            bound.Evidence.Assessments.Where(assessment => assessment.Id != primary.Id),
            assessment =>
            {
                Assert.Equal(RelationQueryBoundAssessmentStatus.Blocked, assessment.Status);
                Assert.Equal(primary.Id, assessment.BlockedBy);
                Assert.Empty(assessment.CapabilityEvidence);
                Assert.Empty(assessment.OperatingBoundaries);
                Assert.Empty(assessment.PreservedGuarantees);
            });
        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, compilation.Status);
        AssertDiagnostic(compilation, PostgresRelationQueryCompilationDiagnosticCodes.FieldBindingMissing);
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.AdapterDecisionCode == primary.AdapterDecisionCode);
        Assert.Empty(compilation.Artifacts);
    }

    [Fact]
    public void Realize_SelectedRowsBranchIgnoresUnrelatedAggregateBindingGap()
    {
        var fixture = CreateRowsAndAggregatesFixture();
        var incompleteAggregateBinding = ReplaceFields(
            fixture.Storage,
            static field => field.SemanticPath.Matches("amount")
                ? CopyFieldEvidence(
                    field,
                    field.NumericDomain,
                    decimalAggregates: null,
                    field.TemporalDomain)
                : field);
        var allBranches = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement);
        var rows = Assert.Single(
            allBranches.Branches,
            static branch => branch.Kind == RelationQueryNativeResultKind.QueryRows);
        var rowsOnly = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement,
            [rows.Id]);
        var compiler = new PostgresRelationQueryCompiler();

        var completeReport = compiler.Realize(allBranches, incompleteAggregateBinding);
        var selectedReport = compiler.Realize(rowsOnly, incompleteAggregateBinding);
        var selectedCompilation = compiler.Compile(rowsOnly, incompleteAggregateBinding);

        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, completeReport.Status);
        Assert.True(selectedReport.IsRealizable, Format(selectedReport.Diagnostics));
        Assert.All(selectedReport.Evidence.Assessments, assessment => Assert.Equal(rows.Id, assessment.Branch));
        Assert.True(selectedCompilation.IsSuccessful, Format(selectedCompilation.Diagnostics));
        Assert.Equal(RelationQueryNativeResultKind.QueryRows, Assert.Single(selectedCompilation.Artifacts).Branch.Kind);
    }

    [Fact]
    public void Realize_ProfileInfeasibilityDoesNotInvokeContextualSuccessProjection()
    {
        var fixture = CreateRowsAndAggregatesFixture();
        var planReference = RelationQueryCompiledPlanReference.From(fixture.Plan);
        var unavailableProfile = new RelationQueryTargetCapabilityProfile(
            PostgresRelationQueryTargetProfile.Target,
            PostgresRelationQueryTargetProfile.ProfileId,
            [planReference.DefinitionSchemaVersion],
            [planReference.CompilerProfile]);
        var infeasible = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            unavailableProfile,
            PostgresRelationQueryTargetProfile.Policy);
        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, infeasible.Status);
        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            infeasible,
            fixture.Placement.Placement);
        PostgresRelationQueryCompiler compiler = new();

        var bound = compiler.Realize(request, fixture.Storage);
        var compilation = compiler.Compile(request, fixture.Storage);

        Assert.Equal(RelationQueryRealizationStatus.NotRealizable, bound.Status);
        Assert.Empty(bound.Evidence.Assessments);
        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, compilation.Status);
        Assert.Empty(compilation.Artifacts);
    }

    [Fact]
    public void Realize_SelectedIndependentSourceBranchDoesNotRequireUnselectedTable()
    {
        var fixture = CreateIndependentSourcesFixture();
        var allBranches = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement);
        var loads = Assert.Single(
            allBranches.Branches,
            static branch => branch.QueryResult == new QueryResultId("load-rows"));
        var selected = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement,
            [loads.Id]);
        PostgresRelationQueryCompiler compiler = new();

        var allReport = compiler.Realize(allBranches, fixture.Storage);
        var selectedReport = compiler.Realize(selected, fixture.Storage);
        var selectedCompilation = compiler.Compile(selected, fixture.Storage);

        Assert.Equal(RelationQueryRealizationStatus.Invalid, allReport.Status);
        Assert.True(selectedReport.IsRealizable, Format(selectedReport.Diagnostics));
        Assert.All(selectedReport.Evidence.Assessments, assessment => Assert.Equal(loads.Id, assessment.Branch));
        Assert.True(selectedCompilation.IsSuccessful, Format(selectedCompilation.Diagnostics));
        var artifact = Assert.Single(selectedCompilation.Artifacts);
        Assert.Equal(loads.Id, artifact.Branch.Id);
        Assert.Contains("\"transport\".\"loads\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("text_loads", artifact.Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ExactNativeRequestRejectsDifferentBindingFingerprint()
    {
        var fixture = CreateLoadSearchRelationFixture();
        var compiler = new PostgresRelationQueryCompiler();
        var request = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement);
        var bound = compiler.Realize(request, fixture.Storage);
        Assert.True(bound.IsRealizable, Format(bound.Diagnostics));
        var differentBinding = ReplaceFields(
            fixture.Storage,
            static field => field.SemanticPath.Matches("name")
                ? CopyFieldColumn(field, "customer_display_name")
                : field);

        var result = compiler.Compile(
            new RelationQueryNativeCompilationRequest(
                fixture.Plan,
                bound,
                fixture.Placement.Placement),
            differentBinding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static candidate => candidate.Code
                == PostgresRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch);
        Assert.Contains("fingerprint", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_ExactNativeRequestRejectsStaleCompilerProfileEvidence()
    {
        var fixture = CreateLoadSearchRelationFixture();
        var compiler = new PostgresRelationQueryCompiler();
        var request = new RelationQueryBoundRealizationRequest(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement);
        var bound = compiler.Realize(request, fixture.Storage);
        var original = bound.Evidence.Assessments[0];
        RelationQueryBoundRequirementAssessment stale = new(
            original.Id,
            original.Branch,
            original.Requirement,
            original.Status,
            original.Origin,
            original.Authority + "/stale-compiler",
            original.CapabilityEvidence,
            original.OperatingBoundaries,
            original.PreservedGuarantees,
            original.UnavailableReason,
            original.Node,
            original.Input,
            original.Field,
            original.PlacementBinding,
            original.ConfigurationSetting,
            original.Message,
            original.Resolution);
        RelationQueryContextualEvidenceProjection evidence = new(
            bound.Evidence.Binding,
            [stale, .. bound.Evidence.Assessments.Skip(1)]);
        var staleBound = RelationQueryBoundRealizationCompiler.Compile(request, evidence);

        Assert.True(staleBound.IsRealizable, Format(staleBound.Diagnostics));
        var result = compiler.Compile(
            new RelationQueryNativeCompilationRequest(
                fixture.Plan,
                staleBound,
                fixture.Placement.Placement),
            fixture.Storage);

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, result.Status);
        AssertDiagnostic(result, PostgresRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void ArtifactJson_RejectsDuplicateProperties()
    {
        var json = CreatePersistedArtifactJson();
        var duplicate =
            $"{{\"schemaVersion\":\"{PostgresRelationQueryCompiledArtifact.CurrentSchemaVersion}\",{json[1..]}";

        Assert.Throws<JsonException>(() =>
            PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(duplicate));
    }

    [Fact]
    public void Compile_AcquiredRootNonSetRelationFailsClosedWithoutRootCorrelationMetadata()
    {
        var fixture = CreateLoadSearchRelationFixture();
        var relation = Assert.IsType<RelationQueryRelationExecutionOutput>(fixture.Plan.ExecutionSlice.RelationOutput);
        var rootPlacement = Assert.Single(
            fixture.Placement.Placement.Bindings,
            binding => binding.Binding == relation.RootBinding);
        var acquiredRoot = new RelationQuerySourcePlacementBinding(
            rootPlacement.Id,
            rootPlacement.Input,
            rootPlacement.Node,
            rootPlacement.Binding,
            rootPlacement.Shape,
            rootPlacement.Source,
            rootPlacement.Kind,
            RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            rootPlacement.Origin,
            rootPlacement.Identity,
            rootPlacement.Fields,
            rootPlacement.RelationshipKeys,
            rootPlacement.Partition);
        var acquiredPlacement = new RelationQuerySourcePlacement(
            fixture.Placement.Placement.SchemaVersion,
            fixture.Placement.Placement.Plan,
            fixture.Placement.Placement.ConventionSetVersion,
            fixture.Placement.Placement.SourceInstances,
            [
                .. fixture.Placement.Placement.Bindings.Select(binding =>
                    binding.Id == rootPlacement.Id ? acquiredRoot : binding)
            ]);

        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);
        var traversalContract = Assert.Single(fixture.Plan.InputContract.Traversals);
        var rootFields = sourceContract.Fields.Select(field => new PostgresRelationQueryFieldBinding(
                field.Input.Id,
                field.Input.Field.Path,
                field.Input.Field.Path.Matches("customerId") ? "customer_id" : "load_id",
                PostgresRelationQueryScalarType.Text,
                PostgresRelationQueryMissingValueEncoding.Prohibited,
                PostgresRelationQueryNullValueEncoding.Prohibited,
                OrdinalText))
            .ToImmutableArray();
        var rootTable = new PostgresRelationQueryTableBinding(
            acquiredRoot.Source,
            acquiredRoot.Id,
            acquiredRoot.Input,
            acquiredRoot.Shape,
            "transport",
            "loads",
            identity: null,
            rootFields,
            [
                new(
                    traversalContract.Input.Id,
                    traversalContract.Definition.SourceReference,
                    "customer_id",
                    PostgresRelationQueryScalarType.Text,
                    traversalContract.Definition.SourceReferenceUniqueness,
                    PostgresRelationQueryMissingValueEncoding.Prohibited,
                    PostgresRelationQueryNullValueEncoding.Prohibited,
                    OrdinalText)
            ]);
        var storage = CopyStorage(
            fixture.Storage,
            tables: [.. fixture.Storage.Tables, rootTable],
            placementFingerprint: acquiredPlacement.Fingerprint);

        var result = Compile(fixture.Plan, fixture.Realization, acquiredPlacement, storage);

        Assert.True(
            result.Status == RelationQueryNativeCompilationStatus.Unsupported,
            Format(result.Diagnostics));
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static candidate => candidate.Code == PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains("supplied", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("root", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_SuppliedQuerySourceSetFailsClosedInsteadOfBecomingOneParameterRow()
    {
        var fixture = CreateRowsAndAggregatesFixture();
        var sourcePlacement = Assert.Single(fixture.Placement.Placement.Bindings);
        var suppliedSource = new RelationQuerySourcePlacementBinding(
            sourcePlacement.Id,
            sourcePlacement.Input,
            sourcePlacement.Node,
            sourcePlacement.Binding,
            sourcePlacement.Shape,
            sourcePlacement.Source,
            sourcePlacement.Kind,
            RelationQuerySourceAcquisitionKind.Supplied,
            sourcePlacement.Origin,
            sourcePlacement.Identity,
            sourcePlacement.Fields,
            sourcePlacement.RelationshipKeys,
            sourcePlacement.Partition);
        var suppliedPlacement = new RelationQuerySourcePlacement(
            fixture.Placement.Placement.SchemaVersion,
            fixture.Placement.Placement.Plan,
            fixture.Placement.Placement.ConventionSetVersion,
            fixture.Placement.Placement.SourceInstances,
            [suppliedSource]);
        var storage = CopyStorage(
            fixture.Storage,
            tables: [],
            placementFingerprint: suppliedPlacement.Fingerprint);

        var result = Compile(fixture.Plan, fixture.Realization, suppliedPlacement, storage);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                    == PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
                && diagnostic.Message.Contains("supplied source set", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void ArtifactJson_RejectsUnsupportedSchemaVersion()
    {
        var artifact = ParseArtifactJson(CreatePersistedArtifactJson());
        artifact["schemaVersion"] = "cohesive.relations.postgres-artifact/v999";

        Assert.Throws<JsonException>(() =>
            PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(artifact.ToJsonString()));
    }

    [Fact]
    public void ArtifactJson_RejectsTamperedFingerprint()
    {
        var artifact = ParseArtifactJson(CreatePersistedArtifactJson());
        var fingerprint = Assert.IsType<JsonObject>(artifact["fingerprint"]);
        fingerprint["value"] = new string('0', 64);

        Assert.Throws<JsonException>(() =>
            PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(artifact.ToJsonString()));
    }

    [Fact]
    public void ArtifactJson_RejectsTamperedCanonicalBranchMetadata()
    {
        var artifact = ParseArtifactJson(CreatePersistedArtifactJson());
        var branch = Assert.IsType<JsonObject>(artifact["branch"]);
        branch["node"] = "tampered-branch-node";

        Assert.Throws<JsonException>(() =>
            PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(artifact.ToJsonString()));
    }

    [Fact]
    public void ArtifactJson_RejectsPlaceholderParameterSlotMismatch()
    {
        var artifact = ParseArtifactJson(CreatePersistedArtifactJson());
        var statement = Assert.IsType<JsonObject>(artifact["statement"]);
        var sql = statement["text"]!.GetValue<string>();
        Assert.Contains("$1", sql, StringComparison.Ordinal);
        statement["text"] = sql.Replace("$1", "$999", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(artifact.ToJsonString()));
    }

    [Fact]
    public void Compile_ExpressionAuthoredRowsAndAllScalarAggregatesIsDeterministicAndDemandScoped()
    {
        var fixture = CreateRowsAndAggregatesFixture();

        var first = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);
        var second = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(first.IsSuccessful, Format(first.Diagnostics));
        Assert.True(second.IsSuccessful, Format(second.Diagnostics));
        Assert.Equal(2, first.Artifacts.Length);

        var rows = Assert.Single(first.Artifacts, static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows);
        Assert.Contains("\"transport\".\"loads\"", rows.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("WHERE", rows.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", rows.Statement.Text, StringComparison.Ordinal);
        Assert.Matches("ORDER BY \"[^\"]+\"\\.\"__order__", rows.Statement.Text);
        Assert.Contains("OFFSET 5", rows.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("LIMIT 25", rows.Statement.Text, StringComparison.Ordinal);

        var bound = rows.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("status")] = ObservationValue.FromString("ready")
        });
        Assert.Equal(rows.Statement.Text, bound.Text);
        Assert.Contains(
            bound.Parameters,
            static parameter => Equals(parameter.Value, "ready"));

        var aggregation = Assert.Single(first.Artifacts, static artifact =>
            artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation);
        Assert.Contains("COUNT(", aggregation.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("SUM(", aggregation.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("MIN(", aggregation.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("MAX(", aggregation.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("AVG(", aggregation.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", aggregation.Statement.Text, StringComparison.Ordinal);

        Assert.All(first.Artifacts, static artifact =>
            Assert.DoesNotContain("unused", artifact.Statement.Text, StringComparison.OrdinalIgnoreCase));
        Assert.All(first.Artifacts, static artifact =>
        {
            Assert.DoesNotContain("AS \"field_", artifact.Statement.Text, StringComparison.Ordinal);
            Assert.DoesNotMatch("\"[qv][0-9]+\"", artifact.Statement.Text);
            Assert.DoesNotContain(
                artifact.ResultFields,
                static field => field.Alias.StartsWith("field_", StringComparison.Ordinal));
        });
        Assert.All(first.Artifacts, static artifact =>
            Assert.DoesNotContain(
                artifact.SelectedFields,
                static field => field.Field.Path.Matches("unused")));
        Assert.All(first.Artifacts, static artifact =>
            Assert.Equal(
                artifact.SelectedFields.Select(static field => field.Input).OrderBy(static input => input.Value),
                artifact.Provenance.InputFields.OrderBy(static input => input.Value)));

        Assert.Equal(
            first.Artifacts.Select(static artifact => artifact.Statement.Text),
            second.Artifacts.Select(static artifact => artifact.Statement.Text));
        Assert.Equal(
            first.Artifacts.Select(static artifact => artifact.Fingerprint),
            second.Artifacts.Select(static artifact => artifact.Fingerprint));
        Assert.All(first.Artifacts, artifact =>
            Assert.Equal(fixture.Storage.Fingerprint, artifact.StorageBinding.Fingerprint));

        Assert.Contains(
            aggregation.Statement.Parameters,
            static slot => slot.Constant?.Kind == PostgresSqlConstantKind.Decimal);
        foreach (var artifact in first.Artifacts)
        {
            var json = PostgresRelationQueryArtifactJsonSerializer.Serialize(artifact, indented: false);
            var rehydrated = PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(json);

            Assert.Equal(artifact.Fingerprint, rehydrated.Fingerprint);
            Assert.Equal(json, PostgresRelationQueryArtifactJsonSerializer.Serialize(rehydrated, indented: false));
        }

        var aggregateJson = PostgresRelationQueryArtifactJsonSerializer.Serialize(aggregation, indented: false);
        var aggregateRehydrated = PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(aggregateJson);
        var aggregateStatement = aggregateRehydrated.Bind(
            new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            });
        Assert.Contains(
            aggregateStatement.Parameters,
            static parameter => parameter.Value is decimal);
    }

    [Fact]
    public void Compile_UngroupedRequiredNullableAggregatesFailClosedForEmptyInput()
    {
        var fixture = CreateUngroupedRequiredAggregateFixture();

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(result.Diagnostics, static candidate =>
            candidate.Code == PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported);
        Assert.Contains("empty input", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Branch);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_AverageRequiresBothAccumulationRangeAndRoundingEvidence()
    {
        foreach (var guarantee in new[]
                 {
                     PostgresRelationQueryDecimalAggregateGuarantee.SumIntermediateRange,
                     PostgresRelationQueryDecimalAggregateGuarantee.AverageRounding
                 })
        {
            var fixture = CreateAverageOnlyFixture(guarantee);

            var result = Compile(
                fixture.Plan,
                fixture.Realization,
                fixture.Placement.Placement,
                fixture.Storage);

            Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                static candidate => candidate.Code == PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported);
            Assert.Contains("SumIntermediateRange", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("AverageRounding", diagnostic.Message, StringComparison.Ordinal);
            Assert.Empty(result.Artifacts);
        }
    }

    [Fact]
    public void Compile_UndemandedGroupingStillPartitionsAggregation()
    {
        var fixture = CreateRowsAndAggregatesFixture(aggregateCountOnly: true);

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(RelationQueryNativeResultKind.QueryAggregation, artifact.Branch.Kind);
        Assert.Contains("GROUP BY", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(
            artifact.ResultFields,
            static field => field.Field.Path.Matches("count"));
        Assert.DoesNotContain(
            artifact.ResultFields,
            static field => field.Field.Path.Matches("status"));
    }

    [Fact]
    public void Compile_UnpagedOrderingStillRequiresAStableUniqueFinalKey()
    {
        var fixture = CreateRowsAndAggregatesFixture(includePaging: false);
        var unstableOrdering = ReplaceFields(
            fixture.Storage,
            field => field.SemanticPath.Matches("id")
                ? CopyField(field, ordering: PostgresRelationQueryOrderingCapability.Exact)
                : field);

        var result = Compile(
            fixture.Plan,
            fixture.Realization,
            fixture.Placement.Placement,
            unstableOrdering);

        Assert.True(
            result.Status == RelationQueryNativeCompilationStatus.Unsupported,
            Format(result.Diagnostics));
        AssertDiagnostic(result, PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable);
    }

    [Fact]
    public void Compile_NestedAggregateTargetUsesItsComposedResultContract()
    {
        var fixture = CreateNestedCountAggregateFixture();

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        var field = Assert.Single(artifact.ResultFields);
        Assert.True(field.Field.Path.Matches("totals.count"));
        Assert.Equal(FieldNullability.Nullable, field.ValueContract.Nullability);
    }

    [Fact]
    public void Compile_LeftInverseTraversalCorrelatesSourceReferenceAndEmitsPresenceMarker()
    {
        var fixture = CreateInverseTraversalFixture();

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("LEFT JOIN", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"customer_id\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"load_id\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(
            artifact.LoweringDecisions,
            static decision => decision.Kind == PostgresRelationQueryLoweringDecisionKind.RelationshipTraversalJoin);
    }

    [Fact]
    public void Compile_TwoLeftTraversalsEmitDedicatedPresenceMarkersAndFieldDependencies()
    {
        var fixture = CreateNestedLeftTraversalFixture();

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(2, artifact.Statement.Text.Split("LEFT JOIN", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, artifact.PresenceBindings.Length);
        Assert.Equal(
            2,
            artifact.Statement.Parameters.Count(static slot =>
                slot.Constant is { Kind: PostgresSqlConstantKind.Boolean, Value: "true" }));

        var customerName = Assert.Single(
            artifact.ResultFields,
            static field => field.Field.Path.Matches("customerName"));
        var regionName = Assert.Single(
            artifact.ResultFields,
            static field => field.Field.Path.Matches("regionName"));
        var loadId = Assert.Single(
            artifact.ResultFields,
            static field => field.Field.Path.Matches("id"));
        var presenceBindings = artifact.PresenceBindings
            .Select(static binding => binding.Binding)
            .ToHashSet();
        Assert.Single(customerName.PresenceDependencies);
        Assert.Single(regionName.PresenceDependencies);
        Assert.NotEqual(customerName.PresenceDependencies[0], regionName.PresenceDependencies[0]);
        Assert.All(customerName.PresenceDependencies, dependency => Assert.Contains(dependency, presenceBindings));
        Assert.All(regionName.PresenceDependencies, dependency => Assert.Contains(dependency, presenceBindings));
        Assert.Empty(loadId.PresenceDependencies);
    }

    [Fact]
    public void Compile_ExplicitInnerJoinLowersTheCanonicalPredicate()
    {
        var fixture = CreateExplicitJoinFixture();

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("INNER JOIN", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(" ON ", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"load_id\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"quoted_amount\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(
            artifact.LoweringDecisions,
                static decision => decision.Kind == PostgresRelationQueryLoweringDecisionKind.ExplicitJoin);
    }

    [Fact]
    public void Compile_ExplicitLeftJoinCarriesRightPresenceIntoProjectedFields()
    {
        var fixture = CreateExplicitJoinFixture(JoinKind.Left);

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("LEFT JOIN", artifact.Statement.Text, StringComparison.Ordinal);
        var presence = Assert.Single(artifact.PresenceBindings);
        var quotedAmount = Assert.Single(
            artifact.ResultFields,
            static field => field.Field.Path.Matches("quotedAmount"));
        Assert.Equal(presence.Binding, Assert.Single(quotedAmount.PresenceDependencies));
        Assert.Contains(
            artifact.LoweringDecisions,
                static decision => decision.Kind == PostgresRelationQueryLoweringDecisionKind.ExplicitJoin);
    }

    [Fact]
    public void Compile_EqualityFailsClosedWhenSqlNullWouldConflateOuterAbsenceAndExplicitNull()
    {
        var fixture = CreateExplicitJoinFixture(
            JoinKind.Left,
            ambiguousOuterNullEquality: true);

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, result.Status);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            static candidate => candidate.Code
                == PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
        Assert.Contains("different canonical meanings", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void Compile_ValueCountOverLeftJoinCountsOnlyPresentNonNullValues()
    {
        var fixture = CreateExplicitJoinFixture(JoinKind.Left, valueCount: true);

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("COUNT(", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("COUNT(*)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"quoted_amount\"", artifact.Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OrdinalTextSearchAndKeysetContinuationUseClosedExactSql()
    {
        var fixture = CreateTextSearchKeysetFixture();

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("LEFT(", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("RIGHT(", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("STRPOS(", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(" > ", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("LIMIT 10", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(PostgresRelationQueryPagingKind.Keyset, Assert.IsType<PostgresRelationQueryPagingContract>(artifact.Paging).Kind);
        Assert.Contains(
            artifact.LoweringDecisions,
            static decision => decision.Kind == PostgresRelationQueryLoweringDecisionKind.KeysetPaging
                && decision.Strategy == "postgres/null-aware-keyset/v1");

        var statement = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("prefix")] = ObservationValue.FromString("North"),
            [new("suffix")] = ObservationValue.FromString("West"),
            [new("substring")] = ObservationValue.FromString("th W"),
            [new("cursor")] = ObservationValue.FromString("load-100")
        });
        Assert.Equal(4, statement.Parameters.Count(static parameter => parameter.Binding is not null));
        Assert.Contains(statement.Parameters, static parameter => Equals(parameter.Value, "load-100"));
    }

    [Fact]
    public void Compile_WholeRowDistinctIsExactAndKeyedRepresentativeSelectionFailsClosed()
    {
        var wholeRow = CreateDistinctFixture(keyed: false);

        var exact = Compile(
            wholeRow.Plan,
            wholeRow.Realization,
            wholeRow.Placement.Placement,
            wholeRow.Storage);

        Assert.True(exact.IsSuccessful, Format(exact.Diagnostics));
        var artifact = Assert.Single(exact.Artifacts);
        Assert.Contains("SELECT DISTINCT", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(
            artifact.LoweringDecisions,
            static decision => decision.Kind == PostgresRelationQueryLoweringDecisionKind.Distinct);

        var keyed = CreateDistinctFixture(keyed: true);
        Assert.False(keyed.Realization.IsRealizable);
        var diagnostic = Assert.Single(
            keyed.Realization.Diagnostics,
            static candidate => candidate.Code == RelationQueryRealizationDiagnosticCodes.RequirementUnavailable);
        Assert.Contains("DistinctKeys", diagnostic.SemanticSite, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_LeftValidTimeJoinLowersHalfOpenUnboundedIntervalExactly()
    {
        var fixture = CreateTemporalJoinFixture();

        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);

        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        var artifact = Assert.Single(result.Artifacts);
        Assert.Contains("LEFT JOIN", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"occurred_at\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"valid_from\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("\"valid_to\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(" >= ", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(" < ", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(" IS NULL", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains(
            artifact.LoweringDecisions,
            static decision => decision.Kind == PostgresRelationQueryLoweringDecisionKind.TemporalJoin);
        var validity = Assert.Single(
            fixture.Storage.Tables.SelectMany(static table => table.IntervalValidities));
        Assert.True(validity.LowerPath.Matches("validFrom"));
        Assert.True(validity.UpperPath.Matches("validTo"));
        Assert.Equal(TemporalNullBoundBehavior.Invalid, validity.LowerNullBehavior);
        Assert.Equal(TemporalNullBoundBehavior.Unbounded, validity.UpperNullBehavior);
        Assert.Equal("ck_load_versions_valid_interval", validity.ValidatedCheckConstraintName);
        Assert.Contains(
            fixture.Storage.ConfigurationDecisions,
            static decision => decision.Setting.Contains("/interval/", StringComparison.Ordinal)
                && decision.Setting.EndsWith("/validatedCheckConstraintName", StringComparison.Ordinal)
                && decision.Origin == RelationQueryConfigurationValueOrigin.Explicit
                && decision.Authority == "tests/postgres/temporal-binding/v1");

        var jsonOptions = RelationQueryJsonSerializer.CreateOptions();
        var persisted = JsonSerializer.Serialize(fixture.Storage, jsonOptions);
        var rehydrated = Assert.IsType<PostgresRelationQueryStorageBinding>(
            JsonSerializer.Deserialize<PostgresRelationQueryStorageBinding>(persisted, jsonOptions));
        var persistedValidity = Assert.Single(
            rehydrated.Tables.SelectMany(static table => table.IntervalValidities));
        Assert.Equal(validity, persistedValidity);
        Assert.Equal(fixture.Storage.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(persisted, JsonSerializer.Serialize(rehydrated, jsonOptions));
    }

    [Fact]
    public void Compile_BindingAndBoundaryFailuresEmitExactStructuredDiagnostics()
    {
        var current = CreateRowsAndAggregatesFixture(offset: 5);
        var changed = CreateRowsAndAggregatesFixture(offset: 6);
        var stale = Compile(current.Plan, current.Realization, current.Placement.Placement, changed.Storage);

        Assert.Equal(RelationQueryNativeCompilationStatus.Invalid, stale.Status);
        AssertDiagnostic(stale, PostgresRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch);

        var noCollation = ReplaceFields(
            current.Storage,
            field => field.SemanticPath.Matches("status")
                ? CopyField(field, textSemantics: null)
                : field);
        var collation = Compile(current.Plan, current.Realization, current.Placement.Placement, noCollation);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, collation.Status);
        AssertDiagnostic(collation, PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);

        var noNumericDomain = ReplaceFields(
            current.Storage,
            field => field.SemanticPath.Matches("amount")
                ? CopyFieldEvidence(field, numericDomain: null, decimalAggregates: null, field.TemporalDomain)
                : field);
        var numericDomain = Compile(
            current.Plan,
            current.Realization,
            current.Placement.Placement,
            noNumericDomain);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, numericDomain.Status);
        AssertDiagnostic(numericDomain, PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);

        var noAggregateEvidence = ReplaceFields(
            current.Storage,
            field => field.SemanticPath.Matches("amount")
                ? CopyFieldEvidence(field, field.NumericDomain, decimalAggregates: null, field.TemporalDomain)
                : field);
        var aggregateEvidence = Compile(
            current.Plan,
            current.Realization,
            current.Placement.Placement,
            noAggregateEvidence);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, aggregateEvidence.Status);
        AssertDiagnostic(aggregateEvidence, PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported);

        var sumOnlyAggregateEvidence = ReplaceFields(
            current.Storage,
            field => field.SemanticPath.Matches("amount")
                ? CopyFieldEvidence(
                    field,
                    field.NumericDomain,
                    new(
                        PostgresRelationQueryDecimalAggregateGuarantee.SumIntermediateRange,
                        "tests/postgres/sum-only-domain/v1",
                        "tests/postgres/sum-only-authority/v1"),
                    field.TemporalDomain)
                : field);
        var averageRounding = Compile(
            current.Plan,
            current.Realization,
            current.Placement.Placement,
            sumOnlyAggregateEvidence);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, averageRounding.Status);
        var averageRoundingDiagnostic = Assert.Single(
            averageRounding.Diagnostics,
            static diagnostic => diagnostic.Code == PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported
                && diagnostic.Message.Contains("AverageRounding", StringComparison.Ordinal));
        Assert.Contains("average", averageRoundingDiagnostic.Message, StringComparison.OrdinalIgnoreCase);

        var unstableOrdering = ReplaceFields(
            current.Storage,
            field => field.SemanticPath.Matches("id")
                ? CopyField(field, ordering: PostgresRelationQueryOrderingCapability.Exact)
                : field);
        var ordering = Compile(current.Plan, current.Realization, current.Placement.Placement, unstableOrdering);

        Assert.True(
            ordering.Status == RelationQueryNativeCompilationStatus.Unsupported,
            Format(ordering.Diagnostics));
        AssertDiagnostic(ordering, PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable);

        var inverse = CreateInverseTraversalFixture();
        var missingEndpoint = CopyStorage(
            inverse.Storage,
            tables:
            [
                .. inverse.Storage.Tables.Select(table => table.RelationshipReferences.IsDefaultOrEmpty
                    ? table
                    : CopyTable(table, relationshipReferences: []))
            ]);
        var endpoint = Compile(
            inverse.Plan,
            inverse.Realization,
            inverse.Placement.Placement,
            missingEndpoint);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, endpoint.Status);
        AssertDiagnostic(endpoint, PostgresRelationQueryCompilationDiagnosticCodes.RelationshipEndpointMissing);

        var explicitJoin = CreateExplicitJoinFixture();
        var crossSourcePlacement = new RelationQuerySourcePlacement(
            explicitJoin.Placement.Placement.SchemaVersion,
            explicitJoin.Placement.Placement.Plan,
            explicitJoin.Placement.Placement.ConventionSetVersion,
            [
                .. explicitJoin.Placement.Placement.SourceInstances.Select((source, index) =>
                    new RelationQuerySourceInstance(
                        source.Id,
                        new($"tests/postgres/cross-source/{index}"),
                        source.TargetProfile,
                        source.Limits))
            ],
            explicitJoin.Placement.Placement.Bindings);
        var crossSourceStorage = CopyStorage(
            explicitJoin.Storage,
            placementFingerprint: crossSourcePlacement.Fingerprint);
        var crossSource = Compile(
            explicitJoin.Plan,
            explicitJoin.Realization,
            crossSourcePlacement,
            crossSourceStorage);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, crossSource.Status);
        AssertDiagnostic(crossSource, PostgresRelationQueryCompilationDiagnosticCodes.CrossSourceJoin);

        var temporal = CreateTemporalJoinFixture();
        var noIntervalEvidence = CopyStorage(
            temporal.Storage,
            tables:
            [
                .. temporal.Storage.Tables.Select(table => table.IntervalValidities.IsDefaultOrEmpty
                    ? table
                    : CopyTable(table, intervalValidities: []))
            ]);
        Assert.NotEqual(temporal.Storage.Fingerprint, noIntervalEvidence.Fingerprint);
        var temporalWithoutEvidence = Compile(
            temporal.Plan,
            temporal.Realization,
            temporal.Placement.Placement,
            noIntervalEvidence);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, temporalWithoutEvidence.Status);
        AssertDiagnostic(
            temporalWithoutEvidence,
            PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported);

        var noTemporalDomain = ReplaceFields(
            temporal.Storage,
            field => field.SemanticPath.Matches("occurredAt")
                ? CopyFieldEvidence(field, field.NumericDomain, field.DecimalAggregates, temporalDomain: null)
                : field);
        var temporalDomain = Compile(
            temporal.Plan,
            temporal.Realization,
            temporal.Placement.Placement,
            noTemporalDomain);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, temporalDomain.Status);
        AssertDiagnostic(temporalDomain, PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);

        var noDateDomain = ReplaceFields(
            temporal.Storage,
            field => field.SemanticPath.Matches("serviceDate")
                ? CopyFieldEvidence(field, field.NumericDomain, field.DecimalAggregates, temporalDomain: null)
                : field);
        var dateDomain = Compile(
            temporal.Plan,
            temporal.Realization,
            temporal.Placement.Placement,
            noDateDomain);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, dateDomain.Status);
        AssertDiagnostic(dateDomain, PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable);
    }

    static PostgresRelationQueryCompilationResult Compile(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement,
        PostgresRelationQueryStorageBinding storage) =>
        new PostgresRelationQueryCompiler().Compile(
            new RelationQueryBoundRealizationRequest(plan, realization, placement),
            storage);

    static string CreatePersistedArtifactJson()
    {
        var fixture = CreateLoadSearchRelationFixture();
        var result = Compile(fixture.Plan, fixture.Realization, fixture.Placement.Placement, fixture.Storage);
        Assert.True(result.IsSuccessful, Format(result.Diagnostics));
        return PostgresRelationQueryArtifactJsonSerializer.Serialize(
            Assert.Single(result.Artifacts),
            indented: false);
    }

    static JsonObject ParseArtifactJson(string json) =>
        Assert.IsType<JsonObject>(JsonNode.Parse(json));

    static LoadSearchRelationFixture CreateLoadSearchRelationFixture()
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<Load>();
        var customerShape = author.Clr.Shape<Customer>();
        var loads = author.Source(loadShape);
        var customers = author.Traverse<Load, Customer>(loads, load => load.CustomerId);
        var projected = author.Project(
            customers,
            (Load load, Customer customer) => new LoadSearchDto
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name,
                CustomerType = customer.Type
            });
        var relation = projected.BuildRelation((LoadSearchDto document) => document.Id);
        var catalog = author.CreateRelationshipCatalogDocument();
        Assert.True(relation.Validation.IsValid, Format(relation.Validation.Diagnostics));

        var compilation = RelationQueryStaticCompiler.Compile(new(
            relation.CreateDocument(),
            author.ShapeDocuments,
            catalog));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var sourceContract = Assert.Single(plan.InputContract.Sources);
        var traversalContract = Assert.Single(plan.InputContract.Traversals);
        var placementBuilder = RelationQueryPlacement.For(plan);
        var executionDomain = new RelationQueryExecutionDomainId("tests/postgres/primary");
        var loadSource = placementBuilder.Source(
            "tests/postgres/supplied-load",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var customerSource = placementBuilder.Source(
            "tests/postgres/customers",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var loadHandle = placementBuilder.Place(sourceContract, loadSource, loadShape)
            .FieldsBySemanticPath();
        var customerHandle = placementBuilder.Place(traversalContract, customerSource, customerShape)
            .Identity(customer => customer.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var loadInput = placement.GetInput(loadHandle);
        var customerInput = placement.GetInput(customerHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/load-search-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                customerInput,
                "customers",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(customer => customer.Name, "customer_name", OrdinalTextOptions)
                    .Column(customer => customer.Type, "customer_type", OrdinalTextOptions)
                    .Identity(customer => customer.Id, "customer_id", OrdinalTextOptions))
            .Build()
            .RequireValue();

        return new(
            plan,
            realization,
            placement,
            storage,
            loadInput.GetField(load => load.Id).Input.Id,
            loadInput.GetField(load => load.CustomerId).Input.Id);
    }

    static RowsAndAggregatesFixture CreateNestedLeftTraversalFixture()
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<Load>();
        var customerShape = author.Clr.Shape<Customer>();
        var regionShape = author.Clr.Shape<Region>();
        var loads = author.Source(loadShape);
        var customers = author.Traverse<Load, Customer>(loads, load => load.CustomerId);
        var regions = author.Traverse<Customer, Region>(customers, customer => customer.RegionId);
        var projected = author.Project(
            regions,
            (Load load, Customer customer, Region region) => new NestedLoadSearchDto
            {
                Id = load.Id,
                CustomerName = customer.Name,
                RegionName = region.Name
            },
            loads.Binding,
            customers.Binding);
        var relation = projected.BuildRelation((NestedLoadSearchDto document) => document.Id);
        var catalog = author.CreateRelationshipCatalogDocument();
        Assert.True(relation.Validation.IsValid, Format(relation.Validation.Diagnostics));

        var compilation = RelationQueryStaticCompiler.Compile(new(
            relation.CreateDocument(),
            author.ShapeDocuments,
            catalog));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var sourceContract = Assert.Single(plan.InputContract.Sources);
        var customerContract = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.ResultShape == customerShape.Id);
        var regionContract = Assert.Single(
            plan.InputContract.Traversals,
            traversal => traversal.ResultShape == regionShape.Id);
        var placementBuilder = RelationQueryPlacement.For(plan);
        var executionDomain = new RelationQueryExecutionDomainId("tests/postgres/primary");
        var loadSource = placementBuilder.Source(
            "tests/postgres/supplied-load",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var customerSource = placementBuilder.Source(
            "tests/postgres/customers",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var regionSource = placementBuilder.Source(
            "tests/postgres/regions",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var loadHandle = placementBuilder.Place(sourceContract, loadSource, loadShape)
            .FieldsBySemanticPath();
        var customerHandle = placementBuilder.Place(customerContract, customerSource, customerShape)
            .Identity(customer => customer.Id)
            .FieldsBySemanticPath();
        var regionHandle = placementBuilder.Place(regionContract, regionSource, regionShape)
            .Identity(region => region.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var customerInput = placement.GetInput(customerHandle);
        var regionInput = placement.GetInput(regionHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/nested-left-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                customerInput,
                "customers",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(customer => customer.Name, "customer_name", OrdinalTextOptions)
                    .Column(customer => customer.RegionId, "region_id", OrdinalTextOptions)
                    .Identity(customer => customer.Id, "customer_id", OrdinalTextOptions)
                    .RelationshipReference(
                        regionContract.Input.Id,
                        customer => customer.RegionId,
                        "region_id",
                        OrdinalTextOptions))
            .Table(
                regionInput,
                "regions",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(region => region.Name, "region_name", OrdinalTextOptions)
                    .Identity(region => region.Id, "region_id", OrdinalTextOptions))
            .Build()
            .RequireValue();

        _ = loadHandle;
        return new(plan, realization, placement, storage);
    }

    static RowsAndAggregatesFixture CreateRowsAndAggregatesFixture(
        int offset = 5,
        bool aggregateCountOnly = false,
        bool includePaging = true)
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<QueryLoad>();
        var status = author.Parameter<string>("status");
        var loads = author.Source(loadShape);
        var filtered = author.Filter(
            loads.Node,
            (QueryLoad load) => load.Status == status.Value,
            loads.Binding);
        var projected = author.Project(
            filtered,
            (QueryLoad load) => new QueryLoadRow
            {
                Id = load.Id,
                Amount = load.Amount
            },
            loads.Binding);
        var ordered = author.Order(
            projected.Node,
            (QueryLoadRow row) => row.Id,
            projected.Binding);
        var aggregates = author.Aggregate<FilterQueryNode, QueryLoadAggregates>(
            filtered,
            aggregate => aggregate
                .Group(result => result.Status, load => load.Status, loads.Binding)
                .Count(result => result.Count)
                .Value(result => result.Total, AggregateOperator.Sum, load => load.Amount, loads.Binding)
                .Value(result => result.Minimum, AggregateOperator.Min, load => load.Amount, loads.Binding)
                .Value(result => result.Maximum, AggregateOperator.Max, load => load.Amount, loads.Binding)
                .Value(result => result.Average, AggregateOperator.Average, load => load.Amount, loads.Binding));
        var rowsResult = includePaging
            ? author.Rows(
                author.Page(ordered, new OffsetPageDefinition(limit: 25, offset)),
                projected.Binding,
                id: "rows")
            : author.Rows(ordered, projected.Binding, id: "rows");
        var aggregationResult = author.Aggregation(aggregates, id: "aggregates");
        var query = author.BuildQuery(
            new("postgres-rows-and-aggregates"),
            new("PostgresRowsAndAggregates"),
            rowsResult,
            aggregationResult);
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));

        var demand = aggregateCountOnly
            ? RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    aggregationResult.Id,
                    [new(aggregationResult.Shape, FieldPath.FromField("count"))])
            ])
            : null;
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments,
            demand: demand));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var placementBuilder = RelationQueryPlacement.For(plan);
        var source = placementBuilder.Source(
            "tests/postgres/loads",
            PostgresRelationQueryTargetProfile.Default,
            new("tests/postgres/primary"));
        var loadHandle = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var loadInput = placement.GetInput(loadHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/rows-and-aggregates-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                loadInput,
                "loads",
                table =>
                {
                    var configured = table
                        .Schema("transport")
                        .ColumnsExplicitly()
                        .Column(load => load.Status, "status", OrdinalTextOptions);
                    if (!aggregateCountOnly)
                    {
                        configured
                            .Column(load => load.Id, "load_id", StableOrdinalTextOptions)
                            .Column(load => load.Amount, "amount", ExactAggregateDecimalOptions);
                    }
                    configured.Identity(load => load.Id, "load_id", StableOrdinalTextOptions);
                })
            .Build()
            .RequireValue();
        return new(plan, realization, placement, storage);
    }

    static RowsAndAggregatesFixture CreateIndependentSourcesFixture()
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<QueryLoad>();
        var textShape = author.Clr.Shape<TextLoad>();
        var loads = author.Source(loadShape, "source/independent-loads");
        var textLoads = author.Source(textShape, "source/independent-text-loads");
        var orderedLoads = author.Order(
            loads.Node,
            (QueryLoad load) => load.Id,
            loads.Binding);
        var orderedTextLoads = author.Order(
            textLoads.Node,
            (TextLoad load) => load.Id,
            textLoads.Binding);
        var loadRows = author.Rows(
            author.Page(orderedLoads, new OffsetPageDefinition(limit: 25, offset: 0)),
            loads.Binding,
            id: "load-rows");
        var textRows = author.Rows(
            author.Page(orderedTextLoads, new OffsetPageDefinition(limit: 25, offset: 0)),
            textLoads.Binding,
            id: "text-rows");
        var query = author.BuildQuery(
            new("postgres-independent-sources"),
            new("PostgresIndependentSources"),
            loadRows,
            textRows);
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));

        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var placementBuilder = RelationQueryPlacement.For(plan);
        var executionDomain = new RelationQueryExecutionDomainId("tests/postgres/primary");
        var loadSource = placementBuilder.Source(
            "tests/postgres/independent-loads",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var textSource = placementBuilder.Source(
            "tests/postgres/independent-text-loads",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var loadContract = plan.InputContract.Sources.Single(source => source.Shape == loadShape.Id);
        var textContract = plan.InputContract.Sources.Single(source => source.Shape == textShape.Id);
        var loadHandle = placementBuilder.Place(loadContract, loadSource, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var textHandle = placementBuilder.Place(textContract, textSource, textShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var loadInput = placement.GetInput(loadHandle);
        var textInput = placement.GetInput(textHandle);
        var completeStorage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/independent-source-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                loadInput,
                "loads",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(load => load.Id, "load_id", StableOrdinalTextOptions)
                    .Column(load => load.Status, "status", OrdinalTextOptions)
                    .Column(load => load.Amount, "amount", ExactDecimalOptions)
                    .Column(load => load.Unused, "unused", OrdinalTextOptions)
                    .Identity(load => load.Id, "load_id", StableOrdinalTextOptions))
            .Table(
                textInput,
                "text_loads",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(load => load.Id, "text_load_id", StableOrdinalTextOptions)
                    .Column(load => load.Name, "name", OrdinalTextOptions)
                    .Identity(load => load.Id, "text_load_id", StableOrdinalTextOptions))
            .Build()
            .RequireValue();
        var storage = CopyStorage(
            completeStorage,
            tables:
            [
                completeStorage.Tables.Single(table => table.Input == loadInput.Binding.Input)
            ]);
        return new(plan, realization, placement, storage);
    }

    static RowsAndAggregatesFixture CreateNestedCountAggregateFixture()
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<QueryLoad>();
        var loads = author.Source(loadShape);
        var aggregates = author.Aggregate<SourceQueryNode, NestedQueryLoadAggregates>(
            loads.Node,
            aggregate => aggregate.Count(result => result.Totals!.Count));
        var aggregationResult = author.Aggregation(aggregates, id: "aggregates");
        var query = author.BuildQuery(
            new("postgres-nested-count-aggregate"),
            new("PostgresNestedCountAggregate"),
            aggregationResult);
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));

        var demand = RelationQueryCompilationDemand.ForQueryResults(
        [
            QueryResultDemand.SelectedFields(
                aggregationResult.Id,
                [new(aggregationResult.Shape, FieldPath.Parse("totals.count"))])
        ]);
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments,
            demand: demand));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var placementBuilder = RelationQueryPlacement.For(plan);
        var source = placementBuilder.Source(
            "tests/postgres/nested-count-loads",
            PostgresRelationQueryTargetProfile.Default,
            new("tests/postgres/primary"));
        var loadHandle = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var loadInput = placement.GetInput(loadHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/nested-count-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                loadInput,
                "loads",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Identity(load => load.Id, "load_id", StableOrdinalTextOptions))
            .Build()
            .RequireValue();
        return new(plan, realization, placement, storage);
    }

    static PostgresRelationQueryStorageBinding ReplaceFields(
        PostgresRelationQueryStorageBinding storage,
        Func<PostgresRelationQueryFieldBinding, PostgresRelationQueryFieldBinding> replace) =>
        CopyStorage(
            storage,
            tables:
            [
                .. storage.Tables.Select(table => CopyTable(
                    table,
                    fields: [.. table.Fields.Select(replace)]))
            ]);

    static PostgresRelationQueryFieldBinding CopyField(
        PostgresRelationQueryFieldBinding field,
        PostgresRelationQueryTextSemantics? textSemantics,
        PostgresRelationQueryOrderingCapability? ordering = null) =>
        new(
            field.Input,
            field.SemanticPath,
            field.ColumnName,
            field.ScalarType,
            field.MissingValueEncoding,
            field.NullValueEncoding,
            textSemantics,
            ordering ?? field.Ordering,
            field.NumericDomain,
            field.DecimalAggregates,
            field.TemporalDomain);

    static PostgresRelationQueryFieldBinding CopyFieldEvidence(
        PostgresRelationQueryFieldBinding field,
        PostgresRelationQueryNumericDomainEvidence? numericDomain,
        PostgresRelationQueryDecimalAggregateAttestation? decimalAggregates,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain) =>
        new(
            field.Input,
            field.SemanticPath,
            field.ColumnName,
            field.ScalarType,
            field.MissingValueEncoding,
            field.NullValueEncoding,
            field.TextSemantics,
            field.Ordering,
            numericDomain,
            decimalAggregates,
            temporalDomain);

    static PostgresRelationQueryFieldBinding CopyField(
        PostgresRelationQueryFieldBinding field,
        PostgresRelationQueryOrderingCapability ordering) =>
        CopyField(field, field.TextSemantics, ordering);

    static PostgresRelationQueryFieldBinding CopyFieldColumn(
        PostgresRelationQueryFieldBinding field,
        string columnName) =>
        new(
            field.Input,
            field.SemanticPath,
            columnName,
            field.ScalarType,
            field.MissingValueEncoding,
            field.NullValueEncoding,
            field.TextSemantics,
            field.Ordering,
            field.NumericDomain,
            field.DecimalAggregates,
            field.TemporalDomain);

    static PostgresRelationQueryTableBinding CopyTable(
        PostgresRelationQueryTableBinding table,
        ImmutableArray<PostgresRelationQueryFieldBinding>? fields = null,
        ImmutableArray<PostgresRelationQueryRelationshipReferenceBinding>? relationshipReferences = null,
        ImmutableArray<PostgresRelationQueryIntervalValidityBinding>? intervalValidities = null) =>
        new(
            table.Source,
            table.PlacementBinding,
            table.Input,
            table.Shape,
            table.SchemaName,
            table.TableName,
            table.Identity,
            fields ?? table.Fields,
            relationshipReferences ?? table.RelationshipReferences,
            intervalValidities ?? table.IntervalValidities);

    static PostgresRelationQueryStorageBinding CopyStorage(
        PostgresRelationQueryStorageBinding storage,
        ImmutableArray<PostgresRelationQueryTableBinding>? tables = null,
        RelationQuerySourcePlacementFingerprint? placementFingerprint = null) =>
        new(
            storage.Id,
            storage.Database,
            storage.Target,
            storage.TargetProfile,
            tables ?? storage.Tables,
            storage.Origin,
            storage.ConventionSetVersion,
            storage.ConfigurationDecisions,
            storage.CompiledPlanFingerprint,
            placementFingerprint ?? storage.PlacementFingerprint);

    static void AssertDiagnostic(PostgresRelationQueryCompilationResult result, string code)
    {
        var diagnostics = result.Diagnostics.Where(candidate => candidate.Code == code).ToArray();
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, static diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
        var affectedBranches = diagnostics
            .Where(static diagnostic => diagnostic.Branch is not null)
            .Select(static diagnostic => diagnostic.Branch!.Value)
            .ToHashSet();
        Assert.DoesNotContain(result.Artifacts, artifact => affectedBranches.Contains(artifact.Branch.Id));
        if (diagnostics.All(static diagnostic => diagnostic.Branch is null))
        {
            Assert.Empty(result.Artifacts);
        }
    }

    static RelationQueryClrShape<LoadVersion> RegisterTemporalVersionShape(
        RelationQueryExpressionAuthoring author)
    {
        GraphId graph = new("tests/postgres/temporal-shapes/v1");
        ShapeId shape = new("load-version");
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graph,
            [
                new Shape(
                    shape,
                    [
                        new FieldDefinition(new("id"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new FieldDefinition(new("loadId"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new FieldDefinition(new("validFrom"), new ScalarTypeRef(ScalarTypeKind.Instant)),
                        new FieldDefinition(
                            new("validTo"),
                            new ScalarTypeRef(ScalarTypeKind.Instant),
                            nullability: FieldNullability.Nullable),
                        new FieldDefinition(new("status"), new ScalarTypeRef(ScalarTypeKind.String))
                    ])
            ]));
        return author.Clr.Shape<LoadVersion>(document, new(graph, shape));
    }

    static RowsAndAggregatesFixture CreateInverseTraversalFixture()
    {
        var author = RelationQuery.Expression();
        var customerShape = author.Clr.Shape<Customer>();
        var loadShape = author.Clr.Shape<Load>();
        var loadCustomer = author.Relationship<Load, string, Customer>(load => load.CustomerId);
        var customers = author.Source(customerShape);
        var loads = author.TraverseInverse(
            customers,
            loadCustomer,
            JoinKind.Left,
            QueryInputRequirement.Optional);
        var projected = author.Project(
            loads,
            (Customer customer, Load load) => new CustomerLoadRow
            {
                CustomerId = customer.Id,
                LoadId = load.Id
            });
        var query = author.BuildQuery(
            new("postgres-inverse-loads"),
            new("PostgresInverseLoads"),
            author.Rows(projected, id: "rows"));
        var catalog = author.CreateRelationshipCatalogDocument();
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments,
            catalog));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var sourceContract = Assert.Single(plan.InputContract.Sources);
        var traversalContract = Assert.Single(plan.InputContract.Traversals);
        var placementBuilder = RelationQueryPlacement.For(plan);
        var executionDomain = new RelationQueryExecutionDomainId("tests/postgres/primary");
        var customerSource = placementBuilder.Source(
            "tests/postgres/customers",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var loadSource = placementBuilder.Source(
            "tests/postgres/loads",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var customerHandle = placementBuilder.Place(sourceContract, customerSource, customerShape)
            .Identity(customer => customer.Id)
            .FieldsBySemanticPath();
        var loadHandle = placementBuilder.Place(traversalContract, loadSource, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var customerInput = placement.GetInput(customerHandle);
        var loadInput = placement.GetInput(loadHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/inverse-load-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                customerInput,
                "customers",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(customer => customer.Id, "customer_id", OrdinalTextOptions)
                    .Identity(customer => customer.Id, "customer_id", OrdinalTextOptions))
            .Table(
                loadInput,
                "loads",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(load => load.Id, "load_id", OrdinalTextOptions)
                    .Column(load => load.CustomerId, "customer_id", OrdinalTextOptions)
                    .Identity(load => load.Id, "load_id", OrdinalTextOptions)
                    .RelationshipReference(
                        traversalContract.Input.Id,
                        load => load.CustomerId,
                        "customer_id",
                        OrdinalTextOptions))
            .Build()
            .RequireValue();
        return new(plan, realization, placement, storage);
    }

    static RowsAndAggregatesFixture CreateUngroupedRequiredAggregateFixture()
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<QueryLoad>();
        var loads = author.Source(loadShape);
        var aggregates = author.Aggregate<SourceQueryNode, UngroupedRequiredAggregates>(
            loads.Node,
            aggregate => aggregate
                .Value(result => result.Minimum, AggregateOperator.Min, load => load.Amount, loads.Binding)
                .Value(result => result.Maximum, AggregateOperator.Max, load => load.Amount, loads.Binding)
                .Value(result => result.Average, AggregateOperator.Average, load => load.Amount, loads.Binding));
        var query = author.BuildQuery(
            new("postgres-ungrouped-required-aggregates"),
            new("PostgresUngroupedRequiredAggregates"),
            author.Aggregation(aggregates, id: "aggregates"));
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));
        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var placementBuilder = RelationQueryPlacement.For(plan);
        var source = placementBuilder.Source(
            "tests/postgres/loads",
            PostgresRelationQueryTargetProfile.Default,
            new("tests/postgres/primary"));
        var loadHandle = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var loadInput = placement.GetInput(loadHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/ungrouped-aggregates-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                loadInput,
                "loads",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(load => load.Amount, "amount", ExactAggregateDecimalOptions)
                    .Identity(load => load.Id, "load_id", StableOrdinalTextOptions))
            .Build()
            .RequireValue();
        return new(plan, realization, placement, storage);
    }

    static RowsAndAggregatesFixture CreateAverageOnlyFixture(
        PostgresRelationQueryDecimalAggregateGuarantee guarantee)
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<QueryLoad>();
        var loads = author.Source(loadShape);
        var aggregates = author.Aggregate<SourceQueryNode, AverageAggregate>(
            loads.Node,
            aggregate => aggregate
                .Group(result => result.Status, load => load.Status, loads.Binding)
                .Value(
                    result => result.Average,
                    AggregateOperator.Average,
                    load => load.Amount,
                    loads.Binding));
        var query = author.BuildQuery(
            new("postgres-average-evidence"),
            new("PostgresAverageEvidence"),
            author.Aggregation(aggregates, id: "aggregates"));
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));
        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var placementBuilder = RelationQueryPlacement.For(plan);
        var source = placementBuilder.Source(
            "tests/postgres/loads",
            PostgresRelationQueryTargetProfile.Default,
            new("tests/postgres/primary"));
        var loadHandle = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var loadInput = placement.GetInput(loadHandle);
        var aggregateOptions = new PostgresRelationQueryColumnOptions(
            numericDomain: ExactNumericDomain,
            decimalAggregates: new(
                guarantee,
                "tests/postgres/partial-average-domain/v1",
                "tests/postgres/partial-average-authority/v1"));
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/average-evidence-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                loadInput,
                "loads",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(load => load.Status, "status", OrdinalTextOptions)
                    .Column(load => load.Amount, "amount", aggregateOptions)
                    .Identity(load => load.Id, "load_id", StableOrdinalTextOptions))
            .Build()
            .RequireValue();
        return new(plan, realization, placement, storage);
    }

    static RowsAndAggregatesFixture CreateExplicitJoinFixture(
        JoinKind kind = JoinKind.Inner,
        bool valueCount = false,
        bool ambiguousOuterNullEquality = false)
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<Load>();
        var quoteShape = author.Clr.Shape<LoadQuote>();
        var loads = author.Source(loadShape, sourceReference: "postgres/explicit-join/loads");
        var quotes = author.Source(quoteShape, sourceReference: "postgres/explicit-join/quotes");
        var joined = author.Join(
            loads.Node,
            quotes.Node,
            kind,
            (Load load, LoadQuote quote) => load.Id == quote.LoadId,
            loads.Binding,
            quotes.Binding);
        RelationQueryAuthoringResult<QueryDefinition> query;
        if (ambiguousOuterNullEquality)
        {
            var filtered = author.Structural.Filter(
                joined,
                Expr.Eq(
                    quotes.Binding.Structural.Field("amount"),
                    new LiteralExpr(new ScalarTypeRef(ScalarTypeKind.Decimal), ObservationValue.Null)));
            var projected = author.Project(
                filtered,
                (Load load, LoadQuote quote) => new LoadQuoteRow
                {
                    LoadId = load.Id,
                    QuotedAmount = quote.Amount
                },
                loads.Binding,
                quotes.Binding);
            query = author.BuildQuery(
                new("postgres-explicit-left-null-equality"),
                new("PostgresExplicitLeftNullEquality"),
                author.Rows(projected, id: "rows"));
        }
        else if (valueCount)
        {
            var aggregate = author.Aggregate<JoinQueryNode, ValueCountAggregate>(
                joined,
                builder => builder.Count(
                    result => result.Count,
                    (LoadQuote quote) => quote.Amount,
                    quotes.Binding));
            query = author.BuildQuery(
                new("postgres-explicit-left-value-count"),
                new("PostgresExplicitLeftValueCount"),
                author.Aggregation(aggregate, id: "aggregates"));
        }
        else
        {
            var projected = author.Project(
                joined,
                (Load load, LoadQuote quote) => new LoadQuoteRow
                {
                    LoadId = load.Id,
                    QuotedAmount = quote.Amount
                },
                loads.Binding,
                quotes.Binding);
            query = author.BuildQuery(
                new(kind == JoinKind.Left ? "postgres-explicit-left-join" : "postgres-explicit-join"),
                new(kind == JoinKind.Left ? "PostgresExplicitLeftJoin" : "PostgresExplicitJoin"),
                author.Rows(projected, id: "rows"));
        }
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));
        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var contracts = plan.InputContract.Sources.OrderBy(static contract => contract.Node.Value).ToArray();
        Assert.Equal(2, contracts.Length);
        var loadContract = Assert.Single(contracts, contract => contract.Shape == loadShape.Id);
        var quoteContract = Assert.Single(contracts, contract => contract.Shape == quoteShape.Id);
        var placementBuilder = RelationQueryPlacement.For(plan);
        var executionDomain = new RelationQueryExecutionDomainId("tests/postgres/primary");
        var loadSource = placementBuilder.Source(
            "tests/postgres/loads",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var quoteSource = placementBuilder.Source(
            "tests/postgres/load-quotes",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var loadHandle = placementBuilder.Place(loadContract, loadSource, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var quoteHandle = placementBuilder.Place(quoteContract, quoteSource, quoteShape)
            .Identity(quote => quote.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var loadInput = placement.GetInput(loadHandle);
        var quoteInput = placement.GetInput(quoteHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/explicit-join-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                loadInput,
                "loads",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(load => load.Id, "load_id", OrdinalTextOptions)
                    .Identity(load => load.Id, "load_id", OrdinalTextOptions))
            .Table(
                quoteInput,
                "load_quotes",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(quote => quote.LoadId, "load_id", OrdinalTextOptions)
                    .Column(quote => quote.Amount, "quoted_amount", ExactDecimalOptions)
                    .Identity(quote => quote.Id, "quote_id", OrdinalTextOptions))
            .Build()
            .RequireValue();
        return new(plan, realization, placement, storage);
    }

    static RowsAndAggregatesFixture CreateTextSearchKeysetFixture()
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<TextLoad>();
        var prefix = author.Parameter<string>("prefix");
        var suffix = author.Parameter<string>("suffix");
        var substring = author.Parameter<string>("substring");
        var cursor = author.Parameter<string>("cursor");
        var loads = author.Source(loadShape);
        var filtered = author.Filter(
            loads.Node,
            (TextLoad load) => load.Name.StartsWith(prefix.Value, StringComparison.Ordinal)
                && load.Name.EndsWith(suffix.Value, StringComparison.Ordinal)
                && load.Name.Contains(substring.Value, StringComparison.Ordinal),
            loads.Binding);
        var projected = author.Project(
            filtered,
            (TextLoad load) => new TextLoadRow
            {
                Id = load.Id,
                Name = load.Name
            },
            loads.Binding);
        var ordered = author.Order(
            projected.Node,
            (TextLoadRow row) => row.Id,
            projected.Binding);
        var paged = author.Page(
            ordered,
            new KeysetPageDefinition(limit: 10, after: [Expr.Param(cursor.Id.Value)]));
        var query = author.BuildQuery(
            new("postgres-text-keyset"),
            new("PostgresTextKeyset"),
            author.Rows(paged, projected.Binding));
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));
        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var placementBuilder = RelationQueryPlacement.For(plan);
        var source = placementBuilder.Source(
            "tests/postgres/text-loads",
            PostgresRelationQueryTargetProfile.Default,
            new("tests/postgres/primary"));
        var loadHandle = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var loadInput = placement.GetInput(loadHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/text-keyset-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                loadInput,
                "loads",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(load => load.Id, "load_id", StableOrdinalTextOptions)
                    .Column(load => load.Name, "load_name", OrdinalTextOptions)
                    .Identity(load => load.Id, "load_id", StableOrdinalTextOptions))
            .Build()
            .RequireValue();
        return new(plan, realization, placement, storage);
    }

    static RowsAndAggregatesFixture CreateDistinctFixture(bool keyed)
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<TextLoad>();
        var loads = author.Source(loadShape, sourceReference: "postgres/distinct/loads");
        var distinct = keyed
            ? author.Distinct(
                loads.Node,
                (TextLoad load) => load.Id,
                loads.Binding)
            : author.Structural.Distinct(loads.Node);
        var query = author.BuildQuery(
            new(keyed ? "postgres-keyed-distinct" : "postgres-whole-row-distinct"),
            new(keyed ? "PostgresKeyedDistinct" : "PostgresWholeRowDistinct"),
            author.Rows(distinct, loads.Binding));
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));
        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = RelationQueryRealizationCompiler.Compile(
            plan,
            PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        if (!keyed)
        {
            Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
        }

        var placementBuilder = RelationQueryPlacement.For(plan);
        var source = placementBuilder.Source(
            "tests/postgres/distinct-loads",
            PostgresRelationQueryTargetProfile.Default,
            new("tests/postgres/primary"));
        var loadHandle = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var loadInput = placement.GetInput(loadHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/distinct-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                loadInput,
                "loads",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(load => load.Id, "load_id", OrdinalTextOptions)
                    .Column(load => load.Name, "load_name", OrdinalTextOptions)
                    .Identity(load => load.Id, "load_id", StableOrdinalTextOptions))
            .Build()
            .RequireValue();
        return new(plan, realization, placement, storage);
    }

    static RowsAndAggregatesFixture CreateTemporalJoinFixture()
    {
        var author = RelationQuery.Expression();
        var eventShape = author.Clr.Shape<LoadEvent>();
        var versionShape = RegisterTemporalVersionShape(author);
        var events = author.Source(eventShape, sourceReference: "postgres/temporal/events");
        var versions = author.Source(versionShape, sourceReference: "postgres/temporal/versions");
        var temporal = author.TemporalJoin(
            events.Node,
            versions.Node,
            JoinKind.Left,
            (LoadEvent occurrence, LoadVersion version) => occurrence.LoadId == version.LoadId,
            events.Binding,
            versions.Binding,
            match => match.PointInInterval(
                (LoadEvent occurrence) => occurrence.OccurredAt,
                events.Binding,
                match.Interval(
                    match.Bound(
                        (LoadVersion version) => version.ValidFrom,
                        versions.Binding,
                        TemporalBoundaryInclusion.Inclusive),
                    match.Bound(
                        (LoadVersion version) => version.ValidTo,
                        versions.Binding,
                        TemporalBoundaryInclusion.Exclusive,
                        TemporalNullBoundBehavior.Unbounded))));
        var projected = author.Project(
            temporal,
            (LoadEvent occurrence, LoadVersion version) => new TemporalLoadRow
            {
                LoadId = occurrence.LoadId,
                ServiceDate = occurrence.ServiceDate,
                Status = version.Status
            },
            events.Binding,
            versions.Binding);
        var query = author.BuildQuery(
            new("postgres-temporal-loads"),
            new("PostgresTemporalLoads"),
            author.Rows(projected, id: "rows"));
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));
        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var realization = Realize(plan);

        var eventContract = Assert.Single(plan.InputContract.Sources, contract => contract.Shape == eventShape.Id);
        var versionContract = Assert.Single(plan.InputContract.Sources, contract => contract.Shape == versionShape.Id);
        var placementBuilder = RelationQueryPlacement.For(plan);
        var executionDomain = new RelationQueryExecutionDomainId("tests/postgres/primary");
        var eventSource = placementBuilder.Source(
            "tests/postgres/load-events",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var versionSource = placementBuilder.Source(
            "tests/postgres/load-versions",
            PostgresRelationQueryTargetProfile.Default,
            executionDomain);
        var eventHandle = placementBuilder.Place(eventContract, eventSource, eventShape)
            .Identity(value => value.Id)
            .FieldsBySemanticPath();
        var versionHandle = placementBuilder.Place(versionContract, versionSource, versionShape)
            .Identity(value => value.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var eventInput = placement.GetInput(eventHandle);
        var versionInput = placement.GetInput(versionHandle);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/temporal-binding/v1")
            .Database(new("tests/postgres/primary"))
            .Table(
                eventInput,
                "load_events",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(value => value.LoadId, "load_id", OrdinalTextOptions)
                    .Column(value => value.OccurredAt, "occurred_at", ExactTemporalOptions)
                    .Column(value => value.ServiceDate, "service_date", ExactTemporalOptions)
                    .Identity(value => value.Id, "event_id", OrdinalTextOptions))
            .Table(
                versionInput,
                "load_versions",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(value => value.LoadId, "load_id", OrdinalTextOptions)
                    .Column(value => value.ValidFrom, "valid_from", ExactTemporalOptions)
                    .Column(value => value.ValidTo, "valid_to", ExactTemporalOptions)
                    .Column(value => value.Status, "status", OrdinalTextOptions)
                    .ValidInterval(
                        value => value.ValidFrom,
                        value => value.ValidTo,
                        "ck_load_versions_valid_interval",
                        TemporalNullBoundBehavior.Invalid,
                        TemporalNullBoundBehavior.Unbounded)
                    .Identity(value => value.Id, "version_id", OrdinalTextOptions))
            .Build()
            .RequireValue();
        return new(plan, realization, placement, storage);
    }

    static RelationQueryRealizationReport Realize(CompiledRelationQueryPlan plan)
    {
        var realization = RelationQueryRealizationCompiler.Compile(
            plan,
            PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
        return realization;
    }

    static string Format<T>(IEnumerable<T> diagnostics) => string.Join(Environment.NewLine, diagnostics);

    sealed class Load
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerId")]
        public required string CustomerId { get; init; }
    }

    sealed class Customer
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("regionId")]
        public required string RegionId { get; init; }
    }

    sealed class Region
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class LoadSearchDto
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerId")]
        public required string CustomerId { get; init; }

        [JsonPropertyName("customerName")]
        public required string CustomerName { get; init; }

        [JsonPropertyName("customerType")]
        public required string CustomerType { get; init; }
    }

    sealed class NestedLoadSearchDto
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerName")]
        public required string CustomerName { get; init; }

        [JsonPropertyName("regionName")]
        public required string RegionName { get; init; }
    }

    sealed class QueryLoad
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("amount")]
        public decimal Amount { get; init; }

        [JsonPropertyName("unused")]
        public required string Unused { get; init; }
    }

    sealed class TextLoad
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class TextLoadRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class QueryLoadRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("amount")]
        public decimal Amount { get; init; }
    }

    sealed class QueryLoadAggregates
    {
        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("count")]
        public long Count { get; init; }

        [JsonPropertyName("total")]
        public decimal Total { get; init; }

        [JsonPropertyName("minimum")]
        public decimal Minimum { get; init; }

        [JsonPropertyName("maximum")]
        public decimal Maximum { get; init; }

        [JsonPropertyName("average")]
        public decimal Average { get; init; }
    }

    sealed class NestedQueryLoadAggregates
    {
        [JsonPropertyName("totals")]
        public QueryLoadAggregateTotals? Totals { get; init; }
    }

    sealed class QueryLoadAggregateTotals
    {
        [JsonPropertyName("count")]
        public long Count { get; init; }
    }

    sealed class UngroupedRequiredAggregates
    {
        [JsonPropertyName("minimum")]
        public decimal Minimum { get; init; }

        [JsonPropertyName("maximum")]
        public decimal Maximum { get; init; }

        [JsonPropertyName("average")]
        public decimal Average { get; init; }
    }

    sealed class AverageAggregate
    {
        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("average")]
        public decimal Average { get; init; }
    }

    sealed class CustomerLoadRow
    {
        [JsonPropertyName("customerId")]
        public required string CustomerId { get; init; }

        [JsonPropertyName("loadId")]
        public string? LoadId { get; init; }
    }

    sealed class LoadQuote
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("loadId")]
        public required string LoadId { get; init; }

        [JsonPropertyName("amount")]
        public decimal Amount { get; init; }
    }

    sealed class LoadQuoteRow
    {
        [JsonPropertyName("loadId")]
        public required string LoadId { get; init; }

        [JsonPropertyName("quotedAmount")]
        public decimal? QuotedAmount { get; init; }
    }

    sealed class ValueCountAggregate
    {
        [JsonPropertyName("count")]
        public long Count { get; init; }
    }

    sealed class LoadEvent
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("loadId")]
        public required string LoadId { get; init; }

        [JsonPropertyName("occurredAt")]
        public DateTimeOffset OccurredAt { get; init; }

        [JsonPropertyName("serviceDate")]
        public DateOnly ServiceDate { get; init; }
    }

    sealed class LoadVersion
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("loadId")]
        public required string LoadId { get; init; }

        [JsonPropertyName("validFrom")]
        public DateTimeOffset ValidFrom { get; init; }

        [JsonPropertyName("validTo")]
        public DateTimeOffset? ValidTo { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    sealed class TemporalLoadRow
    {
        [JsonPropertyName("loadId")]
        public required string LoadId { get; init; }

        [JsonPropertyName("serviceDate")]
        public DateOnly ServiceDate { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }

    sealed record LoadSearchRelationFixture(
        CompiledRelationQueryPlan Plan,
        RelationQueryRealizationReport Realization,
        RelationQueryAuthoredPlacement Placement,
        PostgresRelationQueryStorageBinding Storage,
        RelationQueryInputId LoadId,
        RelationQueryInputId CustomerId);

    sealed record RowsAndAggregatesFixture(
        CompiledRelationQueryPlan Plan,
        RelationQueryRealizationReport Realization,
        RelationQueryAuthoredPlacement Placement,
        PostgresRelationQueryStorageBinding Storage);
}
