using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Elastic;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticRelationQueryBindingAuthoringTests
{
    static readonly FieldPath IdPath = FieldPath.FromField("id");
    static readonly FieldPath StatusPath = FieldPath.FromField("status");
    static readonly JsonSerializerOptions JsonOptions = RelationQueryJsonSerializer.CreateOptions();

    [Fact]
    public void Build_TypedBindingsFlowThroughPlacementAndNativeCompilation()
    {
        var fixture = CreateRowFixture();

        var authored = ConfigureExactFields(fixture).Build();

        Assert.True(authored.IsSuccess, Format(authored.Diagnostics));
        var binding = authored.RequireValue();
        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            ElasticRelationQueryTargetProfile.Default,
            ElasticRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));

        RelationQueryBoundRealizationRequest request = new(
            fixture.Plan,
            realization,
            fixture.AuthoredPlacement.Placement);
        var compilation = new ElasticRelationQueryCompiler().Compile(request, binding);

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        Assert.Single(compilation.Artifacts);
        Assert.Equal("loads-read", binding.IndexName);
        Assert.Equal(
            FieldPath.Parse("status.keyword"),
            binding.ResolveField(fixture.Placed.GetField(load => load.Status).Input.Id).QueryField);
        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(fixture.Plan)),
            binding.CompiledPlanFingerprint);
        Assert.Equal(fixture.AuthoredPlacement.Placement.Fingerprint, binding.PlacementFingerprint);
        AssertConfigurationDecisionCoverage(binding);
        AssertDecision(
            binding,
            "target",
            EffectiveConfigurationOrigin.AdapterConvention,
            ElasticRelationQueryTargetProfile.ProfileId.Value);
        AssertDecision(
            binding,
            "targetProfile",
            EffectiveConfigurationOrigin.AdapterConvention,
            ElasticRelationQueryTargetProfile.ProfileId.Value);
    }

    [Fact]
    public void Build_LocalOverridesWinOverScopedValuesAndRetainPerSettingProvenance()
    {
        var fixture = CreateRowFixture();
        var options = new ElasticRelationQueryBindingAuthoringOptions(
            "tests/elastic-profile/v4",
            indexName: "profile-loads",
            sourceMode: ElasticRelationQuerySourceMode.Synthetic,
            maximumPageSize: 500,
            paginationConsistency: ElasticRelationQueryPaginationConsistency.StableSearchView);

        var result = ElasticRelationQueryBinding.For(
                fixture.Placed,
                options,
                explicitAuthority: "tests/elastic-local/v2")
            .Index("local-loads")
            .MaximumPageSize(250)
            .Build();

        Assert.True(result.IsSuccess, Format(result.Diagnostics));
        var binding = result.RequireValue();
        Assert.Equal("local-loads", binding.IndexName);
        Assert.Equal(ElasticRelationQuerySourceMode.Synthetic, binding.SourceMode);
        Assert.Equal(250, binding.MaximumPageSize);
        AssertDecision(
            binding,
            "indexName",
            EffectiveConfigurationOrigin.Explicit,
            "tests/elastic-local/v2");
        AssertDecision(
            binding,
            "sourceMode",
            EffectiveConfigurationOrigin.ScopedProfile,
            options.Authority);
        AssertDecision(
            binding,
            "maximumPageSize",
            EffectiveConfigurationOrigin.Explicit,
            "tests/elastic-local/v2");
        Assert.All(
            binding.ConfigurationDecisions.Where(static decision =>
                decision.Setting.EndsWith("/sourceField", StringComparison.Ordinal)),
            decision => Assert.Equal(EffectiveConfigurationOrigin.AdapterConvention, decision.Origin));
    }

    [Fact]
    public void Build_TypedAndStructuralAuthoringProduceEquivalentArtifacts()
    {
        var fixture = CreateRowFixture();
        ElasticRelationQueryBindingId id = new("tests/elastic-differential/v1");

        var typed = ConfigureExactFields(fixture)
            .WithId(id)
            .Build()
            .RequireValue();
        var structural = ElasticRelationQueryBinding.For((RelationQueryPlacedInput)fixture.Placed)
            .Index("loads-read")
            .WithId(id)
            .FieldsExplicitly()
            .Keyword(
                IdPath,
                FieldPath.Parse("id.keyword"),
                ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering,
                "tests/ordinal-keyword/v1",
                IdPath)
            .Keyword(
                StatusPath,
                FieldPath.Parse("status.keyword"),
                ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                "tests/ordinal-keyword/v1",
                StatusPath)
            .Build()
            .RequireValue();

        Assert.Equal(typed.Fingerprint, structural.Fingerprint);
        Assert.Equal(
            JsonSerializer.Serialize(typed, JsonOptions),
            JsonSerializer.Serialize(structural, JsonOptions));
    }

    [Fact]
    public void Build_EquivalentDeclarationOrderProducesSameDerivedIdFingerprintAndJson()
    {
        var fixture = CreateRowFixture();

        var first = ConfigureExactFields(fixture).Build().RequireValue();
        var reordered = ElasticRelationQueryBinding.For(fixture.Placed)
            .FieldsExplicitly()
            .Keyword(
                load => load.Status,
                FieldPath.Parse("status.keyword"),
                ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                "tests/ordinal-keyword/v1",
                StatusPath)
            .Keyword(
                load => load.Id,
                FieldPath.Parse("id.keyword"),
                ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering,
                "tests/ordinal-keyword/v1",
                IdPath)
            .Index("loads-read")
            .Build()
            .RequireValue();

        Assert.Equal(first.Id, reordered.Id);
        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.Equal(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonSerializer.Serialize(reordered, JsonOptions));
    }

    [Fact]
    public void Build_DerivedIdIncludesExactPlacementFingerprint()
    {
        var firstFixture = CreateRowFixture(placementConventionSetVersion: "tests/placement/v1");
        var secondFixture = CreateRowFixture(placementConventionSetVersion: "tests/placement/v2");
        var first = ConfigureExactFields(firstFixture).Build().RequireValue();
        var second = ConfigureExactFields(secondFixture).Build().RequireValue();

        Assert.Equal(first.Source, second.Source);
        Assert.Equal(first.PlacementBinding, second.PlacementBinding);
        Assert.NotEqual(first.PlacementFingerprint, second.PlacementFingerprint);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Build_DerivedIdIncludesScalarFieldSentinelAndNestedEvidenceValues()
    {
        var fixture = CreateRowFixture();
        var baseline = ConfigureExactFields(fixture).Build().RequireValue();
        var changedScalar = ConfigureExactFields(fixture)
            .SourceMode(ElasticRelationQuerySourceMode.Synthetic)
            .Build()
            .RequireValue();
        var changedField = ConfigureExactFields(
                fixture,
                idQueryField: FieldPath.Parse("id.exact"))
            .Build()
            .RequireValue();
        var sentinelOne = ConfigureSentinelFields(fixture, "__missing_one").Build().RequireValue();
        var sentinelTwo = ConfigureSentinelFields(fixture, "__missing_two").Build().RequireValue();
        var nestedOne = ConfigureNestedEvidence(fixture, FieldPath.Parse("status.value.keyword"))
            .Build()
            .RequireValue();
        var nestedTwo = ConfigureNestedEvidence(fixture, FieldPath.Parse("status.value.exact"))
            .Build()
            .RequireValue();

        Assert.NotEqual(baseline.Id, changedScalar.Id);
        Assert.NotEqual(baseline.Id, changedField.Id);
        Assert.NotEqual(sentinelOne.Id, sentinelTwo.Id);
        Assert.NotEqual(nestedOne.Id, nestedTwo.Id);
    }

    [Fact]
    public void DerivedIdPlanSeedIncludesFingerprintProfilesCatalogAndCanonicalInputs()
    {
        var baseline = CreatePlanReference();
        var seed = PlanSeed(baseline);

        Assert.NotEqual(seed, PlanSeed(CreatePlanReference(definitionAlgorithm: "sha512")));
        Assert.NotEqual(seed, PlanSeed(CreatePlanReference(shapeCanonicalization: "tests/shapes-c14n/v2")));
        Assert.NotEqual(seed, PlanSeed(CreatePlanReference(includeCatalog: false)));
        Assert.NotEqual(seed, PlanSeed(CreatePlanReference(inputs: ["input/b"])));
    }

    [Fact]
    public void Build_DisabledFieldConventionReportsEveryMissingDemandedField()
    {
        var fixture = CreateRowFixture();

        var result = ElasticRelationQueryBinding.For(fixture.Placed)
            .Index("loads-read")
            .FieldsExplicitly()
            .SourceOnly(load => load.Id, IdPath, ElasticRelationQueryFieldValueEncoding.JsonString)
            .Build();

        Assert.False(result.IsSuccess);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing
            && diagnostic.SemanticPath == StatusPath);
        Assert.Equal(fixture.Placed.GetField(load => load.Status).Input.Id, diagnostic.Input);
    }

    [Fact]
    public void Build_UnknownAndEmptyStructuralSelectorsReturnStableDiagnostics()
    {
        var fixture = CreateRowFixture();

        var result = ElasticRelationQueryBinding.For((RelationQueryPlacedInput)fixture.Placed)
            .Index("loads-read")
            .Field(
                FieldPath.FromField("unknown"),
                field => field.QueryOnly(
                    FieldPath.Parse("unknown.keyword"),
                    ElasticRelationQueryFieldMappingKind.Keyword))
            .Field(
                (FieldPath)default,
                field => field.QueryOnly(
                    FieldPath.Parse("unused.keyword"),
                    ElasticRelationQueryFieldMappingKind.Keyword))
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.FieldUnknown
            && diagnostic.SemanticPath == FieldPath.FromField("unknown"));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid
            && diagnostic.Setting == "field/semantic/invalid");
    }

    [Fact]
    public void Build_RepeatedScalarFieldAndNestedDeclarationsReturnStableDiagnostics()
    {
        var fixture = CreateRowFixture();

        var result = ElasticRelationQueryBinding.For(fixture.Placed)
            .Index("loads-read")
            .Index("other-loads")
            .FieldsExplicitly()
            .FieldsBySemanticPath()
            .Field(load => load.Id, field => field
                .Source(IdPath, ElasticRelationQueryFieldValueEncoding.JsonString)
                .Source(FieldPath.FromField("documentId"), ElasticRelationQueryFieldValueEncoding.JsonString))
            .Nested(
                load => load.Status,
                FieldPath.FromField("status"),
                nested => nested
                    .AttestCanonicalAnyRepresentation()
                    .AttestCanonicalAnyRepresentation()
                    .Child(
                        FieldPath.FromField("value"),
                        FieldPath.Parse("status.value.keyword"),
                        ElasticRelationQueryFieldMappingKind.Keyword,
                        ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                        "tests/nested-keyword/v1"))
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate
            && diagnostic.Setting == "indexName");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate
            && diagnostic.Setting == "fieldMappingConvention");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate
            && diagnostic.Setting is not null
            && diagnostic.Setting.EndsWith("/sourceField", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate
            && diagnostic.Setting is not null
            && diagnostic.Setting.EndsWith("/nested/correlationGuarantee", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DefaultNestedPathsReturnDiagnosticsInsteadOfLeakingPathExceptions()
    {
        var fixture = CreateRowFixture();

        var result = ElasticRelationQueryBinding.For(fixture.Placed)
            .Index("loads-read")
            .FieldsExplicitly()
            .SourceOnly(load => load.Id, IdPath, ElasticRelationQueryFieldValueEncoding.JsonString)
            .Nested(
                load => load.Status,
                default,
                nested => nested.Child(
                    default,
                    default,
                    ElasticRelationQueryFieldMappingKind.Keyword,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                    "tests/nested-keyword/v1"))
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting is not null
            && diagnostic.Setting.Contains("nested", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.ArtifactInvalid);
    }

    [Fact]
    public void Build_InvalidScalarValuesReturnSpecificConfigurationDiagnostics()
    {
        var fixture = CreateRowFixture();
        ElasticRelationQueryBindingId defaultId = default;
        var options = new ElasticRelationQueryBindingAuthoringOptions(
            "tests/invalid-elastic-options/v1",
            bindingId: defaultId,
            indexName: "Invalid Index",
            sourceMode: (ElasticRelationQuerySourceMode)99,
            maximumResultWindow: 0,
            fieldMappingConvention: (ElasticRelationQueryFieldMappingConvention)99);

        var result = ElasticRelationQueryBinding.For(fixture.Placed, options)
            .Keyword(
                load => load.Id,
                queryField: FieldPath.Parse("id.integer"),
                capabilities: ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                semanticProfile: "tests/invalid-mapping/v1",
                mappingKind: ElasticRelationQueryFieldMappingKind.Integer)
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "bindingId");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "indexName");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "sourceMode");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "maximumResultWindow");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "fieldMappingConvention");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting is not null
            && diagnostic.Setting.EndsWith("/mappingKind", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.ArtifactInvalid);
    }

    [Fact]
    public void Build_NonElasticPlacedSourceReturnsProfileMismatchDiagnostic()
    {
        var fixture = CreateRowFixture(CosmosRelationQueryTargetProfile.Default);

        var result = ElasticRelationQueryBinding.For(fixture.Placed)
            .Index("loads-read")
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch);
    }

    [Fact]
    public void Build_ArtifactRoundTripPreservesGeneratedConfigurationDecisions()
    {
        var fixture = CreateRowFixture();
        var binding = ConfigureExactFields(fixture).Build().RequireValue();

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<ElasticRelationQueryStorageBinding>(json, JsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(ElasticRelationQueryStorageBinding.CurrentSchemaVersion, rehydrated.SchemaVersion);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(binding.ConfigurationDecisions.ToArray(), rehydrated.ConfigurationDecisions.ToArray());
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, JsonOptions));
    }

    [Fact]
    public void Build_NestedPathsRetainExactExplicitProvenanceAndRoundTrip()
    {
        const string authority = "tests/elastic-nested-local/v3";
        var fixture = CreateRowFixture();
        var binding = ElasticRelationQueryBinding.For(
                fixture.Placed,
                explicitAuthority: authority)
            .Index("loads-read")
            .FieldsExplicitly()
            .SourceOnly(load => load.Id, IdPath, ElasticRelationQueryFieldValueEncoding.JsonString)
            .Nested(
                load => load.Status,
                FieldPath.FromField("status"),
                nested => nested
                    .AttestCanonicalAnyRepresentation()
                    .Child(
                        FieldPath.FromField("value"),
                        FieldPath.Parse("status.value.keyword"),
                        ElasticRelationQueryFieldMappingKind.Keyword,
                        ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                        "tests/nested-keyword/v1"))
            .Build()
            .RequireValue();

        var input = fixture.Placed.GetField(load => load.Status).Input.Id;
        var nestedPrefix = $"field/{input.Value}/nested/";
        var childPrefix = nestedPrefix + "child/" + DirectFieldSettingKey("value") + "/";
        HashSet<string> expected =
        [
            nestedPrefix + "nestedPath",
            nestedPrefix + "correlationGuarantee",
            nestedPrefix + "nullElementBehavior",
            nestedPrefix + "emptyCollectionBehavior",
            childPrefix + "elementPath",
            childPrefix + "queryField",
            childPrefix + "mappingKind",
            childPrefix + "semanticCapabilities",
            childPrefix + "semanticProfile",
            childPrefix + "missingValueBehavior",
            childPrefix + "nullValueBehavior"
        ];
        var nestedDecisions = binding.ConfigurationDecisions
            .Where(decision => decision.Setting.StartsWith(nestedPrefix, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            expected.SetEquals(nestedDecisions.Select(static decision => decision.Setting)),
            string.Join(Environment.NewLine, nestedDecisions.Select(static decision => decision.Setting)));
        Assert.All(nestedDecisions, decision =>
        {
            Assert.Equal(EffectiveConfigurationOrigin.Explicit, decision.Origin);
            Assert.Equal(authority, decision.Authority);
        });

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<ElasticRelationQueryStorageBinding>(json, JsonOptions);
        Assert.NotNull(rehydrated);
        Assert.Equal(binding.ConfigurationDecisions.ToArray(), rehydrated.ConfigurationDecisions.ToArray());
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
    }

    [Fact]
    public void ConfigureCallbacksAreValidatedBeforeSelectionAndCallbackExceptionsPropagate()
    {
        var fixture = CreateRowFixture();
        var exactField = fixture.Placed.GetField(load => load.Status);

        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentNullException>(() =>
                ElasticRelationQueryBinding.For(fixture.Placed)
                    .Field(load => load.Status.ToLowerInvariant(), null!)).ParamName);
        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentNullException>(() =>
                ElasticRelationQueryBinding.For(fixture.Placed)
                    .Field(FieldPath.FromField("unknown"), null!)).ParamName);
        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentNullException>(() =>
                ElasticRelationQueryBinding.For(fixture.Placed)
                    .Nested(load => load.Status.ToLowerInvariant(), FieldPath.FromField("status"), null!)).ParamName);
        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentNullException>(() =>
                ElasticRelationQueryBinding.For((RelationQueryPlacedInput)fixture.Placed)
                    .Nested(FieldPath.FromField("unknown"), FieldPath.FromField("status"), null!)).ParamName);
        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentNullException>(() =>
                ElasticRelationQueryBinding.For((RelationQueryPlacedInput)fixture.Placed)
                    .Field(exactField, null!)).ParamName);
        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentNullException>(() =>
                ElasticRelationQueryBinding.For((RelationQueryPlacedInput)fixture.Placed)
                    .Nested(exactField, FieldPath.FromField("status"), null!)).ParamName);
        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentNullException>(() =>
                ElasticRelationQueryBinding.For(fixture.Placed)
                    .Field(
                        load => load.Status,
                        field => field.Nested(FieldPath.FromField("status"), null!))).ParamName);

        var fieldCallbackException = new InvalidOperationException("field callback failed");
        Assert.Same(
            fieldCallbackException,
            Assert.Throws<InvalidOperationException>(() =>
                ElasticRelationQueryBinding.For(fixture.Placed)
                    .Field(load => load.Status, _ => throw fieldCallbackException)));
        var nestedCallbackException = new InvalidOperationException("nested callback failed");
        Assert.Same(
            nestedCallbackException,
            Assert.Throws<InvalidOperationException>(() =>
                ElasticRelationQueryBinding.For(fixture.Placed)
                    .Nested(
                        load => load.Status,
                        FieldPath.FromField("status"),
                        _ => throw nestedCallbackException)));
    }

    static ElasticRelationQueryStorageBindingBuilder<LoadDocument> ConfigureExactFields(
        RowFixture fixture,
        FieldPath? idQueryField = null) =>
        ElasticRelationQueryBinding.For(fixture.Placed)
            .Index("loads-read")
            .FieldsExplicitly()
            .Keyword(
                load => load.Id,
                idQueryField ?? FieldPath.Parse("id.keyword"),
                ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering,
                "tests/ordinal-keyword/v1",
                IdPath)
            .Keyword(
                load => load.Status,
                FieldPath.Parse("status.keyword"),
                ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                "tests/ordinal-keyword/v1",
                StatusPath);

    static ElasticRelationQueryStorageBindingBuilder<LoadDocument> ConfigureSentinelFields(
        RowFixture fixture,
        string sentinel) =>
        ElasticRelationQueryBinding.For(fixture.Placed)
            .Index("loads-read")
            .FieldsExplicitly()
            .Field(load => load.Id, field => field
                .Source(IdPath, ElasticRelationQueryFieldValueEncoding.JsonString)
                .Query(FieldPath.Parse("id.keyword"), ElasticRelationQueryFieldMappingKind.Keyword)
                .RootDocument()
                .Attest(
                    ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                    | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering,
                    "tests/ordinal-keyword/v1")
                .MissingValues(
                    ElasticRelationQueryMissingValueBehavior.IndexedSentinel,
                    ObservationValue.FromString(sentinel)))
            .Keyword(
                load => load.Status,
                FieldPath.Parse("status.keyword"),
                ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                "tests/ordinal-keyword/v1",
                StatusPath);

    static ElasticRelationQueryStorageBindingBuilder<LoadDocument> ConfigureNestedEvidence(
        RowFixture fixture,
        FieldPath childQueryField) =>
        ElasticRelationQueryBinding.For(fixture.Placed)
            .Index("loads-read")
            .FieldsExplicitly()
            .SourceOnly(load => load.Id, IdPath, ElasticRelationQueryFieldValueEncoding.JsonString)
            .Nested(
                load => load.Status,
                FieldPath.FromField("status"),
                nested => nested
                    .AttestCanonicalAnyRepresentation()
                    .Child(
                        FieldPath.FromField("value"),
                        childQueryField,
                        ElasticRelationQueryFieldMappingKind.Keyword,
                        ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                        "tests/nested-keyword/v1"));

    static RowFixture CreateRowFixture(
        RelationQueryTargetCapabilityProfile? targetProfile = null,
        string? placementConventionSetVersion = null)
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<LoadDocument>();
        var status = author.Parameter<string>("status");
        var loads = author.Source(loadShape);
        var filtered = author.Filter(
            loads.Node,
            (LoadDocument load) => load.Status == status.Value,
            loads.Binding);
        var projected = author.Project(
            filtered,
            (LoadDocument load) => new LoadRow
            {
                Id = load.Id,
                Status = load.Status
            },
            loads.Binding);
        var ordered = author.Order(
            projected.Node,
            (LoadRow row) => row.Id,
            projected.Binding);
        var paged = author.Page(ordered, new OffsetPageDefinition(limit: 25));
        var rows = author.Rows(paged, projected.Binding, id: "rows");
        var query = author.BuildQuery(
            new("elastic-binding-authoring"),
            new("ElasticBindingAuthoring"),
            rows);
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));

        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var placementBuilder = RelationQueryPlacement.For(
            plan,
            placementConventionSetVersion is null
                ? null
                : new RelationQueryPlacementAuthoringOptions(
                    "tests/placement-profile/v1",
                    placementConventionSetVersion));
        var source = placementBuilder.Source(
            "tests/elastic/loads",
            targetProfile ?? ElasticRelationQueryTargetProfile.Default);
        var placedSource = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var authoredPlacement = placementBuilder.Build().RequireValue();
        var placed = authoredPlacement.GetInput(placedSource);
        return new(plan, authoredPlacement, placed);
    }

    static void AssertDecision(
        ElasticRelationQueryStorageBinding binding,
        string setting,
        EffectiveConfigurationOrigin origin,
        string authority)
    {
        var decision = Assert.Single(binding.ConfigurationDecisions, candidate => candidate.Setting == setting);
        Assert.Equal(origin, decision.Origin);
        Assert.Equal(authority, decision.Authority);
    }

    static RelationQueryCompiledPlanReference CreatePlanReference(
        string definitionAlgorithm = "sha256",
        string shapeCanonicalization = "tests/shapes-c14n/v1",
        bool includeCatalog = true,
        string[]? inputs = null) => new(
        "tests/compiler/v1",
        "cohesive.relations/v2",
        new RelationQueryDefinitionFingerprint(
            definitionAlgorithm,
            "tests/definition-c14n/v1",
            "definition-hash"),
        new RelationQueryPlanComponentFingerprint(
            "sha256",
            shapeCanonicalization,
            "shapes-hash"),
        includeCatalog
            ? new RelationshipCatalogFingerprint("sha256", "tests/catalog-c14n/v1", "catalog-hash")
            : null,
        new RelationQueryPlanComponentFingerprint("sha256", "tests/demand-c14n/v1", "demand-hash"),
        [.. (inputs ?? ["input/a"]).Select(static value => new RelationQueryInputId(value))]);

    static string PlanSeed(RelationQueryCompiledPlanReference reference)
    {
        var fingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(reference);
        return $"{fingerprint.Algorithm}|{fingerprint.Canonicalization}|{fingerprint.Value}";
    }

    static void AssertConfigurationDecisionCoverage(ElasticRelationQueryStorageBinding binding)
    {
        HashSet<string> expected =
        [
            "target",
            "targetProfile",
            "indexName",
            "sourceMode",
            "maximumResultWindow",
            "maximumPageSize",
            "paginationConsistency",
            "conventionSetVersion",
            "bindingId"
        ];
        string[] fieldSettings =
        [
            "sourceField",
            "queryField",
            "mappingKind",
            "retrievalKind",
            "retrievalEncoding",
            "documentScope",
            "semanticCapabilities",
            "reversedSuffixField",
            "semanticProfile",
            "missingValueBehavior",
            "missingValueSentinel",
            "nullValueBehavior",
            "nullValueSentinel",
            "nestedScope"
        ];
        foreach (var field in binding.Fields)
        {
            foreach (var setting in fieldSettings)
            {
                expected.Add($"field/{field.Input.Value}/{setting}");
            }
        }

        var actual = binding.ConfigurationDecisions
            .Select(static decision => decision.Setting)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.SetEquals(actual), string.Join(Environment.NewLine, actual.Order()));
    }

    static string Format<T>(IEnumerable<T> diagnostics) => string.Join(Environment.NewLine, diagnostics);

    static string DirectFieldSettingKey(string field) => $"0:{field.Length}:{field}";

    sealed class LoadDocument
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    sealed class LoadRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    sealed record RowFixture(
        CompiledRelationQueryPlan Plan,
        RelationQueryAuthoredPlacement AuthoredPlacement,
        RelationQueryPlacedInput<LoadDocument> Placed);
}
