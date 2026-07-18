using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Elastic;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Model;

public sealed class CosmosRelationQueryBindingAuthoringTests
{
    static readonly FieldPath IdPath = FieldPath.FromField("id");
    static readonly FieldPath StatusPath = FieldPath.FromField("status");

    [Fact]
    public void Build_TypedOverridesDriveEffectiveOrderingEvidenceAndNativeCompilation()
    {
        var fixture = CreateRowFixture();

        var authored = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .Identity(load => load.Id)
            .Field(load => load.Id, FieldPath.FromField("documentId"))
            .StableUnique(load => load.Id)
            .ExactOrdering(load => load.Id)
            .Build();

        Assert.True(authored.IsSuccess, Format(authored.Diagnostics));
        var binding = authored.RequireValue();
        Assert.Equal(FieldPath.FromField("documentId"), binding.IdentityPath);
        Assert.Equal(
            FieldPath.FromField("documentId"),
            binding.ResolveField(fixture.Placed.GetField(load => load.Id).Input.Id));
        Assert.Equal(["documentId"], binding.StableUniqueOrderingPaths.Select(static path => path.ToString()));
        Assert.Equal(["documentId"], binding.ExactOrderingPaths.Select(static path => path.ToString()));
        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(fixture.Plan)),
            binding.CompiledPlanFingerprint);
        Assert.Equal(fixture.AuthoredPlacement.Placement.Fingerprint, binding.PlacementFingerprint);

        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
        var compilation = new CosmosRelationQueryCompiler().Compile(
            new(fixture.Plan, realization, fixture.AuthoredPlacement.Placement),
            binding);

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        Assert.Contains("c[\"documentId\"]", Assert.Single(compilation.Artifacts).Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ExpressionAuthoredAggregationFlowsThroughPlacementBindingAndCosmosCompilation()
    {
        var fixture = CreateAggregationFixture();
        var binding = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .IdentityDocumentPath(IdPath)
            .MaximumInputRows(10_000)
            .Build()
            .RequireValue();

        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
        var compilation = new CosmosRelationQueryCompiler().Compile(
            new(fixture.Plan, realization, fixture.AuthoredPlacement.Placement),
            binding);

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var artifact = Assert.Single(compilation.Artifacts);
        Assert.Equal(RelationQueryNativeResultKind.QueryAggregation, artifact.Branch.Kind);
        Assert.Contains("COUNT(1)", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("GROUP BY c[\"status\"]", artifact.Statement.Text, StringComparison.Ordinal);
        AssertDecision(
            binding,
            "maximumInputRows",
            RelationQueryConfigurationValueOrigin.Explicit,
            CosmosRelationQueryBinding.LocalDeclarationAuthority);
    }

    [Fact]
    public void Build_DefaultConventionsDoNotInventOrderingOrCardinalityProofs()
    {
        var fixture = CreateRowFixture();

        var binding = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .Identity(load => load.Id)
            .Build()
            .RequireValue();

        Assert.Empty(binding.StableUniqueOrderingPaths);
        Assert.Empty(binding.ExactOrderingPaths);
        Assert.Null(binding.MaximumInputRows);
    }

    [Fact]
    public void Build_PlacementSelectorsAndEffectiveFieldOverridesProducePhysicalIdentityAndPartitionPaths()
    {
        var fixture = CreateRowFixture(
            identitySourceSelector: "physicalId",
            partitionByStatus: true);

        var binding = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .AtDocumentRoot(FieldPath.FromField("payload"))
            .Field(load => load.Status, FieldPath.FromField("state"))
            .Build()
            .RequireValue();

        Assert.Equal(FieldPath.FromField("physicalId"), binding.IdentityPath);
        Assert.Equal(FieldPath.Parse("payload.state"), binding.PartitionPath);
    }

    [Fact]
    public void Build_LocalOverridesWinOverScopedProfileAndRetainPerSettingProvenance()
    {
        var fixture = CreateRowFixture();
        var options = new CosmosRelationQueryBindingAuthoringOptions(
            "tests/cosmos-profile/v1",
            containerName: "profile-loads",
            rootAlias: "profileRoot",
            identityPath: FieldPath.FromField("profileId"),
            fieldPaths: new Dictionary<FieldPath, FieldPath>
            {
                [StatusPath] = FieldPath.FromField("profileStatus")
            },
            maximumInputRows: 5_000);

        var result = CosmosRelationQueryBinding.For(
                fixture.Placed,
                options,
                explicitAuthority: "tests/local-overrides/v1")
            .Container("loads")
            .RootAlias("localRoot")
            .Identity(load => load.Id)
            .Field(load => load.Status, FieldPath.FromField("localStatus"))
            .Build();

        Assert.True(result.IsSuccess, Format(result.Diagnostics));
        var binding = result.RequireValue();
        Assert.Equal(CosmosRelationQueryBindingOrigin.Explicit, binding.Origin);
        Assert.Equal("localRoot", binding.RootAlias);
        Assert.Equal(IdPath, binding.IdentityPath);
        Assert.Equal(
            FieldPath.FromField("localStatus"),
            binding.ResolveField(fixture.Placed.GetField(load => load.Status).Input.Id));
        AssertDecision(binding, "rootAlias", RelationQueryConfigurationValueOrigin.Explicit, "tests/local-overrides/v1");
        AssertDecision(binding, "containerName", RelationQueryConfigurationValueOrigin.Explicit, "tests/local-overrides/v1");
        AssertDecision(binding, "identityPath", RelationQueryConfigurationValueOrigin.Explicit, "tests/local-overrides/v1");
        AssertDecision(binding, "maximumInputRows", RelationQueryConfigurationValueOrigin.ScopedProfile, "tests/cosmos-profile/v1");
        AssertDecision(
            binding,
            "field/" + fixture.Placed.GetField(load => load.Status).Input.Id.Value,
            RelationQueryConfigurationValueOrigin.Explicit,
            "tests/local-overrides/v1");
        AssertDecision(
            binding,
            "field/" + fixture.Placed.GetField(load => load.Id).Input.Id.Value,
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            CosmosRelationQueryStorageBinding.SemanticPathConventionSet);
        AssertDecision(
            binding,
            "target",
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            CosmosRelationQueryTargetProfile.ProfileId.Value);
        AssertDecision(
            binding,
            "targetProfile",
            RelationQueryConfigurationValueOrigin.AdapterConvention,
            CosmosRelationQueryTargetProfile.ProfileId.Value);

        HashSet<string> expectedSettings =
        [
            "target",
            "targetProfile",
            "containerName",
            "rootAlias",
            "identityPath",
            "documentRoot",
            "partitionPath",
            "maximumInputRows",
            "missingValueEncoding",
            "nullValueEncoding",
            "conventionSetVersion",
            "bindingId",
            "field/" + fixture.Placed.GetField(load => load.Id).Input.Id.Value,
            "field/" + fixture.Placed.GetField(load => load.Status).Input.Id.Value
        ];
        Assert.True(
            expectedSettings.SetEquals(binding.ConfigurationDecisions.Select(static decision => decision.Setting)),
            string.Join(", ", binding.ConfigurationDecisions.Select(static decision => decision.Setting)));
    }

    [Fact]
    public void CompiledPlanReferenceFingerprint_IncludesFingerprintVersionsCatalogPresenceAndInputs()
    {
        var fixture = CreateRowFixture();
        var reference = RelationQueryCompiledPlanReference.From(fixture.Plan);
        var changedDefinitionAlgorithm = new RelationQueryCompiledPlanReference(
            reference.CompilerProfile,
            reference.DefinitionSchemaVersion,
            new RelationQueryDefinitionFingerprint(
                "sha512",
                reference.DefinitionFingerprint.Canonicalization,
                reference.DefinitionFingerprint.Value),
            reference.ShapeSnapshotsFingerprint,
            reference.RelationshipCatalogFingerprint,
            reference.DemandFingerprint,
            reference.Inputs);
        var changedCatalogPresence = new RelationQueryCompiledPlanReference(
            reference.CompilerProfile,
            reference.DefinitionSchemaVersion,
            reference.DefinitionFingerprint,
            reference.ShapeSnapshotsFingerprint,
            new RelationshipCatalogFingerprint("sha256", "tests/catalog/v1", "same-value"),
            reference.DemandFingerprint,
            reference.Inputs);
        var changedInputs = new RelationQueryCompiledPlanReference(
            reference.CompilerProfile,
            reference.DefinitionSchemaVersion,
            reference.DefinitionFingerprint,
            reference.ShapeSnapshotsFingerprint,
            reference.RelationshipCatalogFingerprint,
            reference.DemandFingerprint,
            [.. reference.Inputs, new RelationQueryInputId("tests/additional-input")]);

        var canonical = RelationQueryCompiledPlanReferenceFingerprinter.Compute(reference);
        Assert.NotEqual(
            canonical,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(changedDefinitionAlgorithm));
        Assert.NotEqual(
            canonical,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(changedCatalogPresence));
        Assert.NotEqual(
            canonical,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(changedInputs));
    }

    [Fact]
    public void Build_TypedAndStructuralAuthoringProduceEquivalentArtifacts()
    {
        var fixture = CreateRowFixture();
        CosmosRelationQueryBindingId id = new("tests/cosmos-differential/v1");

        var typed = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .WithId(id)
            .Identity(load => load.Id)
            .Field(load => load.Status, FieldPath.FromField("state"))
            .ExactOrdering(load => load.Id)
            .Build()
            .RequireValue();
        var structural = CosmosRelationQueryBinding.For((RelationQueryPlacedInput)fixture.Placed)
            .Container("loads")
            .WithId(id)
            .Identity(fixture.Placed.GetField(IdPath))
            .Field(StatusPath, FieldPath.FromField("state"))
            .ExactOrdering(fixture.Placed.GetField(IdPath))
            .Build()
            .RequireValue();

        Assert.Equal(typed.Fingerprint, structural.Fingerprint);
        Assert.Equal(
            JsonSerializer.Serialize(typed, JsonOptions),
            JsonSerializer.Serialize(structural, JsonOptions));
    }

    [Fact]
    public void Build_EquivalentConfigurationOrderProducesSameIdFingerprintAndJson()
    {
        var fixture = CreateRowFixture();
        var firstOptions = new CosmosRelationQueryBindingAuthoringOptions(
            "tests/order/v1",
            fieldPaths: new Dictionary<FieldPath, FieldPath>
            {
                [IdPath] = FieldPath.FromField("documentId"),
                [StatusPath] = FieldPath.FromField("state")
            },
            stableUniqueOrderingPaths: [FieldPath.FromField("documentId"), FieldPath.FromField("state")],
            exactOrderingPaths: [FieldPath.FromField("documentId"), FieldPath.FromField("state")]);
        var reversedOptions = new CosmosRelationQueryBindingAuthoringOptions(
            "tests/order/v1",
            fieldPaths: new Dictionary<FieldPath, FieldPath>
            {
                [StatusPath] = FieldPath.FromField("state"),
                [IdPath] = FieldPath.FromField("documentId")
            },
            stableUniqueOrderingPaths: [FieldPath.FromField("state"), FieldPath.FromField("documentId")],
            exactOrderingPaths: [FieldPath.FromField("state"), FieldPath.FromField("documentId")]);

        var first = CosmosRelationQueryBinding.For(fixture.Placed, firstOptions)
            .Container("loads")
            .Identity(load => load.Id)
            .Build()
            .RequireValue();
        var reversed = CosmosRelationQueryBinding.For(fixture.Placed, reversedOptions)
            .Identity(load => load.Id)
            .Container("loads")
            .Build()
            .RequireValue();

        Assert.Equal(first.Id, reversed.Id);
        Assert.Equal(first.Fingerprint, reversed.Fingerprint);
        Assert.Equal(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonSerializer.Serialize(reversed, JsonOptions));
    }

    [Fact]
    public void Build_DerivedIdIncludesExactPlacementFingerprint()
    {
        var firstFixture = CreateRowFixture(placementConventionSetVersion: "tests/placement/v1");
        var secondFixture = CreateRowFixture(placementConventionSetVersion: "tests/placement/v2");
        var first = CosmosRelationQueryBinding.For(firstFixture.Placed)
            .Container("loads")
            .Identity(load => load.Id)
            .Build()
            .RequireValue();
        var second = CosmosRelationQueryBinding.For(secondFixture.Placed)
            .Container("loads")
            .Identity(load => load.Id)
            .Build()
            .RequireValue();

        Assert.Equal(first.Source, second.Source);
        Assert.Equal(first.PlacementBinding, second.PlacementBinding);
        Assert.NotEqual(first.PlacementFingerprint, second.PlacementFingerprint);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Build_DisabledFieldConventionReportsEveryMissingDemandedField()
    {
        var fixture = CreateRowFixture();

        var result = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .IdentityDocumentPath(IdPath)
            .FieldsExplicitly()
            .Field(load => load.Id, IdPath)
            .Build();

        Assert.False(result.IsSuccess);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing
            && diagnostic.SemanticPath == StatusPath);
        Assert.Equal(fixture.Placed.GetField(load => load.Status).Input.Id, diagnostic.Input);
    }

    [Fact]
    public void Build_DuplicateAndUnknownFieldDeclarationsReturnStructuredDiagnostics()
    {
        var fixture = CreateRowFixture();

        var result = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .Identity(load => load.Id)
            .Field(load => load.Status, FieldPath.FromField("state"))
            .Field(load => load.Status, FieldPath.FromField("state2"))
            .Field(FieldPath.FromField("unknown"), FieldPath.FromField("unknown"))
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate
            && diagnostic.Input == fixture.Placed.GetField(load => load.Status).Input.Id);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.FieldUnknown
            && diagnostic.SemanticPath == FieldPath.FromField("unknown"));
    }

    [Fact]
    public void Build_RepeatedAndMutuallyExclusiveScalarDeclarationsReturnStableDiagnostics()
    {
        var fixture = CreateRowFixture();

        var result = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .Container("other-loads")
            .WithId(new("tests/first"))
            .WithId(new("tests/second"))
            .RootAlias("c")
            .RootAlias("document")
            .Identity(load => load.Id)
            .IdentityDocumentPath(FieldPath.FromField("documentId"))
            .AtDocumentRoot()
            .AtDocumentRoot(FieldPath.FromField("payload"))
            .Partition(load => load.Id)
            .PartitionDocumentPath(FieldPath.FromField("partition"))
            .FieldsBySemanticPath()
            .FieldsExplicitly()
            .MaximumInputRows(100)
            .MaximumInputRows(200)
            .MissingValues(CosmosMissingValueEncoding.OmittedProperty)
            .MissingValues(CosmosMissingValueEncoding.OmittedProperty)
            .NullValues(CosmosNullValueEncoding.JsonNull)
            .NullValues(CosmosNullValueEncoding.JsonNull)
            .ConventionSetVersion("tests/convention/first")
            .ConventionSetVersion("tests/convention/second")
            .Build();

        Assert.False(result.IsSuccess);
        var duplicateSettings = result.Diagnostics
            .Where(static diagnostic =>
                diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate)
            .Select(static diagnostic => diagnostic.Setting!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> expectedSettings =
        [
            "containerName",
            "bindingId",
            "rootAlias",
            "identityPath",
            "documentRoot",
            "partitionPath",
            "maximumInputRows",
            "missingValueEncoding",
            "nullValueEncoding",
            "conventionSetVersion",
            "fieldMappingConvention"
        ];
        Assert.True(expectedSettings.SetEquals(duplicateSettings), string.Join(", ", duplicateSettings));
    }

    [Fact]
    public void Build_InvalidScopedPathsReturnDiagnosticsInsteadOfLeakingPathExceptions()
    {
        var fixture = CreateRowFixture();
        var options = new CosmosRelationQueryBindingAuthoringOptions(
            "tests/invalid-options/v1",
            fieldPaths: new Dictionary<FieldPath, FieldPath>
            {
                [StatusPath] = default
            },
            stableUniqueOrderingPaths: [default]);

        var result = CosmosRelationQueryBinding.For(fixture.Placed, options)
            .Container("loads")
            .Identity(load => load.Id)
            .StableUnique(load => load.Status)
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid
            && diagnostic.Message.Contains("cannot be empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RepeatedScopedOrderingEvidenceReturnsSameTierDuplicateDiagnostic()
    {
        var fixture = CreateRowFixture();
        var status = FieldPath.FromField("status");
        var options = new CosmosRelationQueryBindingAuthoringOptions(
            "tests/repeated-scoped-evidence/v1",
            containerName: "loads",
            stableUniqueOrderingPaths: [status, status]);

        var result = CosmosRelationQueryBinding.For(fixture.Placed, options)
            .Identity(load => load.Id)
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate
            && diagnostic.Setting == "stableUniqueOrderingPath/"
            + CosmosRelationQueryStorageBinding.FieldPathKey(status));
    }

    [Fact]
    public void Build_InvalidScopedContainerAndBindingIdReturnSpecificConfigurationDiagnostics()
    {
        var fixture = CreateRowFixture();
        CosmosRelationQueryBindingId defaultId = default;
        var options = new CosmosRelationQueryBindingAuthoringOptions(
            "tests/invalid-scalars/v1",
            bindingId: defaultId,
            containerName: " ");

        var result = CosmosRelationQueryBinding.For(fixture.Placed, options)
            .Identity(load => load.Id)
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "containerName");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "bindingId");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.ArtifactInvalid);
    }

    [Fact]
    public void Build_UndefinedScopedFieldMappingConventionReturnsConfigurationDiagnostic()
    {
        var fixture = CreateRowFixture();
        var options = new CosmosRelationQueryBindingAuthoringOptions(
            "tests/invalid-field-convention/v1",
            containerName: "loads",
            fieldPaths: new Dictionary<FieldPath, FieldPath>
            {
                [IdPath] = IdPath,
                [StatusPath] = StatusPath
            },
            fieldMappingConvention: (CosmosRelationQueryFieldMappingConvention)99);

        var result = CosmosRelationQueryBinding.For(fixture.Placed, options).Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "fieldMappingConvention");
    }

    [Fact]
    public void Build_NonCosmosPlacedSourceReturnsProfileMismatchDiagnostic()
    {
        var fixture = CreateRowFixture(ElasticRelationQueryTargetProfile.Default);

        var result = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .Identity(load => load.Id)
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch);
    }

    [Fact]
    public void Build_ArtifactRoundTripPreservesGeneratedConfigurationDecisions()
    {
        var fixture = CreateRowFixture();
        var binding = CosmosRelationQueryBinding.For(fixture.Placed)
            .Container("loads")
            .Identity(load => load.Id)
            .ExactOrdering(load => load.Id)
            .Build()
            .RequireValue();

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<CosmosRelationQueryStorageBinding>(json, JsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(binding.ConfigurationDecisions.ToArray(), rehydrated.ConfigurationDecisions.ToArray());
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, JsonOptions));
    }

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    static RowFixture CreateRowFixture(
        RelationQueryTargetCapabilityProfile? targetProfile = null,
        string? identitySourceSelector = null,
        bool partitionByStatus = false,
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
        var query = author.BuildQuery(new("cosmos-binding-authoring"), new("CosmosBindingAuthoring"), rows);
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
            sourceKey: "tests/cosmos/loads",
            targetProfile: targetProfile ?? CosmosRelationQueryTargetProfile.Default);
        var placedSource = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id, identitySourceSelector)
            .FieldsBySemanticPath();
        if (partitionByStatus)
        {
            placedSource.Partition(load => load.Status);
        }

        var authoredPlacement = placementBuilder.Build().RequireValue();
        var placed = authoredPlacement.GetInput(placedSource);
        return new(plan, authoredPlacement, placed);
    }

    static RowFixture CreateAggregationFixture()
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<LoadDocument>();
        var loads = author.Source(loadShape);
        var aggregate = author.Aggregate<SourceQueryNode, StatusCount>(
            loads.Node,
            builder => builder
                .Group(result => result.Status, (LoadDocument load) => load.Status, loads.Binding)
                .Count(result => result.Count));
        var aggregations = author.Aggregation(aggregate, id: "status-counts");
        var query = author.BuildQuery(
            new("cosmos-binding-authoring-aggregation"),
            new("CosmosBindingAuthoringAggregation"),
            aggregations);
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));

        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var placementBuilder = RelationQueryPlacement.For(plan);
        var source = placementBuilder.Source(
            sourceKey: "tests/cosmos/loads",
            targetProfile: CosmosRelationQueryTargetProfile.Default);
        var placedSource = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var authoredPlacement = placementBuilder.Build().RequireValue();
        var placed = authoredPlacement.GetInput(placedSource);
        return new(plan, authoredPlacement, placed);
    }

    static void AssertDecision(
        CosmosRelationQueryStorageBinding binding,
        string setting,
        RelationQueryConfigurationValueOrigin origin,
        string authority)
    {
        var decision = Assert.Single(binding.ConfigurationDecisions, candidate => candidate.Setting == setting);
        Assert.Equal(origin, decision.Origin);
        Assert.Equal(authority, decision.Authority);
    }

    static string Format<T>(IEnumerable<T> diagnostics) => string.Join(Environment.NewLine, diagnostics);

    sealed class LoadDocument
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("unused")]
        public string? Unused { get; init; }
    }

    sealed class LoadRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    sealed class StatusCount
    {
        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("count")]
        public long Count { get; init; }
    }

    sealed record RowFixture(
        CompiledRelationQueryPlan Plan,
        RelationQueryAuthoredPlacement AuthoredPlacement,
        RelationQueryPlacedInput<LoadDocument> Placed);
}
