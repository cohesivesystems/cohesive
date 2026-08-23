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
    static readonly Uri AccountEndpoint = new("https://localhost:8081/");
    const string DatabaseName = "operations";
    static readonly FieldPath IdPath = FieldPath.FromField("id");
    static readonly FieldPath StatusPath = FieldPath.FromField("status");
    static readonly FieldPath StopsPath = FieldPath.FromField("stops");
    static readonly FieldPath StopLocationPath = FieldPath.FromField("location");
    static readonly FieldPath StopTypePath = FieldPath.FromField("type");
    static readonly FieldPath PhysicalStopsPath = FieldPath.FromField("routeStops");
    static readonly FieldPath PhysicalStopLocationPath = FieldPath.FromField("site");
    static readonly FieldPath PhysicalStopTypePath = FieldPath.FromField("stopKind");
    const CosmosRelationQueryCollectionElementSemanticCapabilities ExactComparisons =
        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality;
    const CosmosRelationQueryStructuredCollectionAbsenceBehavior RequiredStructuredValue =
        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion;

    [Fact]
    public void Build_TypedOverridesDriveEffectiveOrderingEvidenceAndNativeCompilation()
    {
        var fixture = CreateRowFixture();

        var authored = CosmosRelationQueryBinding.For(fixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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
            new RelationQueryBoundRealizationRequest(
                fixture.Plan,
                realization,
                fixture.AuthoredPlacement.Placement),
            binding);

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        Assert.Contains("c[\"documentId\"]", Assert.Single(compilation.Artifacts).Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EntitySourceScopeIsFingerprintBoundPersistableAndConjoinedByNativeCompilation()
    {
        var fixture = CreateRowFixture();
        const string authority = "tests/cosmos-entity-scope/v1";

        var binding = CosmosRelationQueryBinding.For(fixture.Placed, explicitAuthority: authority)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("shared")
            .EntityDocuments("entity-v2")
            .Identity(load => load.Id)
            .StableUnique(load => load.Id)
            .ExactOrdering(load => load.Id)
            .Build()
            .RequireValue();

        var scope = Assert.Single(binding.SourceScopeEqualities);
        Assert.Equal(FieldPath.FromField("documentKind"), scope.DocumentPath);
        Assert.Equal("entity-v2", scope.Value);
        Assert.Contains(binding.ConfigurationDecisions, decision =>
            decision.Setting.StartsWith("sourceScopeEquality/", StringComparison.Ordinal)
            && decision.Origin == EffectiveConfigurationOrigin.Explicit
            && string.Equals(decision.Authority, authority, StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var restored = Assert.IsType<CosmosRelationQueryStorageBinding>(
            JsonSerializer.Deserialize<CosmosRelationQueryStorageBinding>(json, JsonOptions));
        Assert.Equal(binding.Fingerprint, restored.Fingerprint);
        Assert.Equal(scope, Assert.Single(restored.SourceScopeEqualities));

        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        var compilation = new CosmosRelationQueryCompiler().Compile(
            new RelationQueryBoundRealizationRequest(
                fixture.Plan,
                realization,
                fixture.AuthoredPlacement.Placement),
            binding);

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var template = Assert.Single(compilation.Artifacts).Statement;
        Assert.Contains("WHERE (c[\"documentKind\"] = @p0)", template.Text, StringComparison.Ordinal);
        var discriminator = Assert.Single(template.Parameters, static parameter =>
            parameter.Kind == CosmosSqlParameterBindingKind.Constant
            && Equals(parameter.ConstantValue, "entity-v2"));
        Assert.Equal("@p0", discriminator.Name);

        var changed = CosmosRelationQueryBinding.For(fixture.Placed, explicitAuthority: authority)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("shared")
            .EntityDocuments("entity-v3")
            .Identity(load => load.Id)
            .StableUnique(load => load.Id)
            .ExactOrdering(load => load.Id)
            .Build()
            .RequireValue();
        Assert.NotEqual(binding.Id, changed.Id);
        Assert.NotEqual(binding.Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void Build_SourceScopeRejectsMalformedAndDuplicateMembershipFacts()
    {
        var fixture = CreateRowFixture();
        var builder = CosmosRelationQueryBinding.For(fixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("shared")
            .Identity(load => load.Id);

        Assert.Throws<ArgumentException>(() => builder.SourceScopeEquals(FieldPath.Parse("envelope.kind"), "entity"));
        Assert.Throws<ArgumentException>(() => builder.SourceScopeEquals(FieldPath.FromField("documentKind"), " "));

        var duplicate = builder
            .EntityDocuments("entity")
            .EntityDocuments("entity")
            .Build();
        Assert.False(duplicate.IsSuccess);
        Assert.Contains(duplicate.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate
            && diagnostic.Setting is not null
            && diagnostic.Setting.StartsWith("sourceScopeEquality/", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_TypedStructuredCollectionFlowsFromExpressionAuthoringToSdkQueryDefinition()
    {
        var fixture = CreateStructuredCollectionFixture();
        var binding = ConfigureTypedStructuredCollection(fixture)
            .StableUnique(load => load.Id)
            .ExactOrdering(load => load.Id)
            .Build()
            .RequireValue();

        var realization = RelationQueryRealizationCompiler.Compile(
            fixture.Plan,
            CosmosRelationQueryTargetProfile.Default,
            CosmosRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
        var compilation = new CosmosRelationQueryCompiler().Compile(
            new RelationQueryBoundRealizationRequest(
                fixture.Plan,
                realization,
                fixture.AuthoredPlacement.Placement),
            binding);

        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var artifact = Assert.Single(compilation.Artifacts);
        var query = artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new("location")] = ObservationValue.FromString("SEA")
        }).ToQueryDefinition();
        Assert.Contains(
            "EXISTS (SELECT VALUE e0 FROM e0 IN c[\"routeStops\"] WHERE "
            + "((e0[\"site\"] = @p0) AND (e0[\"stopKind\"] = @p1)))",
            query.QueryText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(" JOIN ", query.QueryText, StringComparison.Ordinal);
        Assert.DoesNotContain("ARRAY_CONTAINS", query.QueryText, StringComparison.Ordinal);
        Assert.Equal(2, query.GetQueryParameters().Count());
    }

    [Fact]
    public void Build_TypedAndStructuralStructuredCollectionAuthoringNormalizeToOneArtifact()
    {
        const string authority = "tests/cosmos-collection-local/v1";
        var fixture = CreateStructuredCollectionFixture();
        var typed = ConfigureTypedStructuredCollection(fixture, authority: authority)
            .WithId(new("tests/cosmos-collection-equivalence/v1"))
            .Build()
            .RequireValue();
        var structural = CosmosRelationQueryBinding.For(
                (RelationQueryPlacedInput)fixture.Placed,
                explicitAuthority: authority)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("loads")
            .WithId(new("tests/cosmos-collection-equivalence/v1"))
            .Identity(fixture.Placed.GetField(IdPath))
            .StructuredCollection(
                StopsPath,
                PhysicalStopsPath,
                collection => collection
                    .AttestCanonicalAnyRepresentation("tests/cosmos-json-array/v1")
                    .Child(
                        StopLocationPath,
                        PhysicalStopLocationPath,
                        CosmosRelationQueryCollectionElementValueDomain.String,
                        ExactComparisons,
                        "tests/cosmos-ordinal-string/v1",
                        RequiredStructuredValue,
                        RequiredStructuredValue)
                    .Child(
                        StopTypePath,
                        PhysicalStopTypePath,
                        CosmosRelationQueryCollectionElementValueDomain.String,
                        ExactComparisons,
                        "tests/cosmos-ordinal-string/v1",
                        RequiredStructuredValue,
                        RequiredStructuredValue))
            .Build()
            .RequireValue();

        Assert.Equal(typed.Fingerprint, structural.Fingerprint);
        Assert.Equal(
            JsonSerializer.Serialize(typed, JsonOptions),
            JsonSerializer.Serialize(structural, JsonOptions));
        var collectionInput = fixture.Placed.GetField(load => load.Stops).Input.Id;
        var collectionPrefix = $"field/{collectionInput.Value}/collection";
        var decisions = typed.ConfigurationDecisions
            .Where(decision => decision.Setting.StartsWith(collectionPrefix, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(decisions);
        Assert.All(decisions, decision =>
        {
            Assert.Equal(EffectiveConfigurationOrigin.Explicit, decision.Origin);
            Assert.Equal(authority, decision.Authority);
        });
    }

    [Fact]
    public void Build_StructuredCollectionNormalizationIsOrderIndependentAndEvidenceSensitive()
    {
        var fixture = CreateStructuredCollectionFixture();
        var baseline = ConfigureTypedStructuredCollection(fixture).Build().RequireValue();
        var reordered = ConfigureTypedStructuredCollection(fixture, reverseChildren: true).Build().RequireValue();
        var changed = ConfigureTypedStructuredCollection(
                fixture,
                locationDocumentPath: FieldPath.FromField("changedSite"))
            .Build()
            .RequireValue();

        Assert.Equal(baseline.Id, reordered.Id);
        Assert.Equal(baseline.Fingerprint, reordered.Fingerprint);
        Assert.Equal(
            JsonSerializer.Serialize(baseline, JsonOptions),
            JsonSerializer.Serialize(reordered, JsonOptions));
        Assert.NotEqual(baseline.Id, changed.Id);
        Assert.NotEqual(baseline.Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void Build_StructuredCollectionDuplicatesMissingEvidenceAndInvalidTypedChildrenAreDiagnosed()
    {
        var fixture = CreateStructuredCollectionFixture();
        var result = CosmosRelationQueryBinding.For(fixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("loads")
            .Identity(load => load.Id)
            .StructuredCollection(
                (LoadDocument load) => load.Stops,
                PhysicalStopsPath,
                collection => collection
                    .AttestCanonicalAnyRepresentation("tests/cosmos-json-array/v1")
                    .AttestCanonicalAnyRepresentation("tests/cosmos-json-array/v2")
                    .Child(
                        stop => stop.Location,
                        PhysicalStopLocationPath,
                        CosmosRelationQueryCollectionElementValueDomain.String,
                        ExactComparisons,
                        "tests/cosmos-ordinal-string/v1",
                        RequiredStructuredValue,
                        RequiredStructuredValue)
                    .Child(
                        stop => stop.Location,
                        FieldPath.FromField("otherSite"),
                        CosmosRelationQueryCollectionElementValueDomain.String,
                        ExactComparisons,
                        "tests/cosmos-ordinal-string/v1",
                        RequiredStructuredValue,
                        RequiredStructuredValue)
                    .Child(
                        stop => stop.Location.ToLowerInvariant(),
                        PhysicalStopTypePath,
                        CosmosRelationQueryCollectionElementValueDomain.String,
                        ExactComparisons,
                        "tests/cosmos-ordinal-string/v1",
                        RequiredStructuredValue,
                        RequiredStructuredValue))
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate
            && diagnostic.Setting is not null
            && diagnostic.Setting.Contains("collection/semanticProfile", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate
            && diagnostic.Setting is not null
            && diagnostic.Setting.Contains("collection/child/", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid
            && diagnostic.Setting is not null
            && diagnostic.Setting.EndsWith("collection/child/typed", StringComparison.Ordinal));

        var missing = CosmosRelationQueryBinding.For(fixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("loads")
            .Identity(load => load.Id)
            .StructuredCollection(
                StopsPath,
                PhysicalStopsPath,
                collection => collection.Child(
                    StopLocationPath,
                    PhysicalStopLocationPath,
                    CosmosRelationQueryCollectionElementValueDomain.String,
                    ExactComparisons,
                    "tests/cosmos-ordinal-string/v1",
                    RequiredStructuredValue,
                    RequiredStructuredValue))
            .Build();
        Assert.False(missing.IsSuccess);
        Assert.Contains(missing.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing
            && diagnostic.Setting is not null
            && diagnostic.Setting.EndsWith("collection/semanticProfile", StringComparison.Ordinal));
        Assert.DoesNotContain(missing.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.ArtifactInvalid);

        var missingChild = CosmosRelationQueryBinding.For(fixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("loads")
            .Identity(load => load.Id)
            .StructuredCollection(
                StopsPath,
                PhysicalStopsPath,
                collection => collection.AttestCanonicalAnyRepresentation("tests/cosmos-json-array/v1"))
            .Build();
        Assert.False(missingChild.IsSuccess);
        Assert.Contains(missingChild.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing
            && diagnostic.Setting is not null
            && diagnostic.Setting.EndsWith("collection/child", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ExpressionAuthoredGroupedAggregationFailsWithoutDeterministicOrderingStrategy()
    {
        var fixture = CreateAggregationFixture();
        var binding = CosmosRelationQueryBinding.For(fixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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
            new RelationQueryBoundRealizationRequest(
                fixture.Plan,
                realization,
                fixture.AuthoredPlacement.Placement),
            binding);

        Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, compilation.Status);
        _ = Assert.Single(
            compilation.Diagnostics
                .Where(static diagnostic =>
                    diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable
                    && diagnostic.Message.Contains(
                        "deterministic order",
                        StringComparison.Ordinal))
                .DistinctBy(static diagnostic => diagnostic.Message));
        Assert.Empty(compilation.Artifacts);
        AssertDecision(
            binding,
            "maximumInputRows",
            EffectiveConfigurationOrigin.Explicit,
            CosmosRelationQueryBinding.LocalDeclarationAuthority);
    }

    [Fact]
    public void Build_DefaultConventionsDoNotInventOrderingOrCardinalityProofs()
    {
        var fixture = CreateRowFixture();

        var binding = CosmosRelationQueryBinding.For(fixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("loads")
            .Identity(load => load.Id)
            .Build()
            .RequireValue();

        Assert.Empty(binding.StableUniqueOrderingPaths);
        Assert.Empty(binding.ExactOrderingPaths);
        Assert.Null(binding.MaximumInputRows);
    }

    [Fact]
    public void Build_MissingPhysicalLocationReportsAllRequiredAffinityFacts()
    {
        var fixture = CreateRowFixture();

        var result = CosmosRelationQueryBinding.For(fixture.Placed)
            .Identity(load => load.Id)
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Equal(
            ["accountEndpoint", "containerName", "databaseName"],
            result.Diagnostics
                .Where(static diagnostic =>
                    diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing)
                .Select(static diagnostic => diagnostic.Setting)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Build_PlacementSelectorsAndEffectiveFieldOverridesProducePhysicalIdentityAndPartitionPaths()
    {
        var fixture = CreateRowFixture(
            identitySourceSelector: "physicalId",
            partitionByStatus: true);

        var binding = CosmosRelationQueryBinding.For(fixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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
            accountEndpoint: new Uri("https://profile.documents.azure.com"),
            databaseName: "profile-operations",
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
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("loads")
            .RootAlias("localRoot")
            .Identity(load => load.Id)
            .Field(load => load.Status, FieldPath.FromField("localStatus"))
            .Build();

        Assert.True(result.IsSuccess, Format(result.Diagnostics));
        var binding = result.RequireValue();
        Assert.Equal(CosmosRelationQueryBindingOrigin.Explicit, binding.Origin);
        Assert.Equal(AccountEndpoint, binding.AccountEndpoint);
        Assert.Equal(DatabaseName, binding.DatabaseName);
        Assert.Equal("localRoot", binding.RootAlias);
        Assert.Equal(IdPath, binding.IdentityPath);
        Assert.Equal(
            FieldPath.FromField("localStatus"),
            binding.ResolveField(fixture.Placed.GetField(load => load.Status).Input.Id));
        AssertDecision(binding, "rootAlias", EffectiveConfigurationOrigin.Explicit, "tests/local-overrides/v1");
        AssertDecision(binding, "accountEndpoint", EffectiveConfigurationOrigin.Explicit, "tests/local-overrides/v1");
        AssertDecision(binding, "databaseName", EffectiveConfigurationOrigin.Explicit, "tests/local-overrides/v1");
        AssertDecision(binding, "containerName", EffectiveConfigurationOrigin.Explicit, "tests/local-overrides/v1");
        AssertDecision(binding, "identityPath", EffectiveConfigurationOrigin.Explicit, "tests/local-overrides/v1");
        AssertDecision(binding, "maximumInputRows", EffectiveConfigurationOrigin.ScopedProfile, "tests/cosmos-profile/v1");
        AssertDecision(
            binding,
            "field/" + fixture.Placed.GetField(load => load.Status).Input.Id.Value,
            EffectiveConfigurationOrigin.Explicit,
            "tests/local-overrides/v1");
        AssertDecision(
            binding,
            "field/" + fixture.Placed.GetField(load => load.Id).Input.Id.Value,
            EffectiveConfigurationOrigin.AdapterConvention,
            CosmosRelationQueryStorageBinding.SemanticPathConventionSet);
        AssertDecision(
            binding,
            "target",
            EffectiveConfigurationOrigin.AdapterConvention,
            CosmosRelationQueryTargetProfile.ProfileId.Value);
        AssertDecision(
            binding,
            "targetProfile",
            EffectiveConfigurationOrigin.AdapterConvention,
            CosmosRelationQueryTargetProfile.ProfileId.Value);

        HashSet<string> expectedSettings =
        [
            "target",
            "targetProfile",
            "accountEndpoint",
            "databaseName",
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
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("loads")
            .WithId(id)
            .Identity(load => load.Id)
            .Field(load => load.Status, FieldPath.FromField("state"))
            .ExactOrdering(load => load.Id)
            .Build()
            .RequireValue();
        var structural = CosmosRelationQueryBinding.For((RelationQueryPlacedInput)fixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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
            accountEndpoint: new Uri("https://LOCALHOST:8081"),
            databaseName: DatabaseName,
            fieldPaths: new Dictionary<FieldPath, FieldPath>
            {
                [IdPath] = FieldPath.FromField("documentId"),
                [StatusPath] = FieldPath.FromField("state")
            },
            stableUniqueOrderingPaths: [FieldPath.FromField("documentId"), FieldPath.FromField("state")],
            exactOrderingPaths: [FieldPath.FromField("documentId"), FieldPath.FromField("state")]);
        var reversedOptions = new CosmosRelationQueryBindingAuthoringOptions(
            "tests/order/v1",
            accountEndpoint: AccountEndpoint,
            databaseName: DatabaseName,
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
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("loads")
            .Identity(load => load.Id)
            .Build()
            .RequireValue();
        var second = CosmosRelationQueryBinding.For(secondFixture.Placed)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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
            .Account(AccountEndpoint)
            .Account(new Uri("https://other.documents.azure.com"))
            .Database(DatabaseName)
            .Database("other-operations")
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
            "accountEndpoint",
            "databaseName",
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
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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
            accountEndpoint: AccountEndpoint,
            databaseName: DatabaseName,
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
    public void Build_InvalidScopedPhysicalLocationAndBindingIdReturnSpecificConfigurationDiagnostics()
    {
        var fixture = CreateRowFixture();
        CosmosRelationQueryBindingId defaultId = default;
        var options = new CosmosRelationQueryBindingAuthoringOptions(
            "tests/invalid-scalars/v1",
            bindingId: defaultId,
            accountEndpoint: new Uri("relative-account", UriKind.Relative),
            databaseName: " ",
            containerName: " ");

        var result = CosmosRelationQueryBinding.For(fixture.Placed, options)
            .Identity(load => load.Id)
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "accountEndpoint");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryBindingAuthoringDiagnosticCodes.ConfigurationConflict
            && diagnostic.Setting == "databaseName");
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
            accountEndpoint: AccountEndpoint,
            databaseName: DatabaseName,
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
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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
            .Account(AccountEndpoint)
            .Database(DatabaseName)
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

    static RowFixture CreateStructuredCollectionFixture()
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<LoadDocument>();
        var location = author.Parameter<string>("location");
        var loads = author.Source(loadShape);
        var filtered = author.Filter(
            loads.Node,
            (LoadDocument load) => load.Stops.Any(stop =>
                stop.Location == location.Value
                && stop.Type == "Pickup"),
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
            new("cosmos-binding-authoring-structured-collection"),
            new("CosmosBindingAuthoringStructuredCollection"),
            rows);
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

    static CosmosRelationQueryStorageBindingBuilder<LoadDocument> ConfigureTypedStructuredCollection(
        RowFixture fixture,
        bool reverseChildren = false,
        FieldPath? locationDocumentPath = null,
        string authority = CosmosRelationQueryBinding.LocalDeclarationAuthority)
    {
        return CosmosRelationQueryBinding.For(fixture.Placed, explicitAuthority: authority)
            .Account(AccountEndpoint)
            .Database(DatabaseName)
            .Container("loads")
            .Identity(load => load.Id)
            .StructuredCollection(
                (LoadDocument load) => load.Stops,
                PhysicalStopsPath,
                collection =>
                {
                    collection.AttestCanonicalAnyRepresentation("tests/cosmos-json-array/v1");

                    void AddLocation() => collection.Child(
                        stop => stop.Location,
                        locationDocumentPath ?? PhysicalStopLocationPath,
                        CosmosRelationQueryCollectionElementValueDomain.String,
                        ExactComparisons,
                        "tests/cosmos-ordinal-string/v1",
                        RequiredStructuredValue,
                        RequiredStructuredValue);
                    void AddType() => collection.Child(
                        stop => stop.Type,
                        PhysicalStopTypePath,
                        CosmosRelationQueryCollectionElementValueDomain.String,
                        ExactComparisons,
                        "tests/cosmos-ordinal-string/v1",
                        RequiredStructuredValue,
                        RequiredStructuredValue);

                    if (reverseChildren)
                    {
                        AddType();
                        AddLocation();
                    }
                    else
                    {
                        AddLocation();
                        AddType();
                    }
                });
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
        EffectiveConfigurationOrigin origin,
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

        [JsonPropertyName("stops")]
        public required IReadOnlyList<StopDocument> Stops { get; init; }

        [JsonPropertyName("unused")]
        public string? Unused { get; init; }
    }

    sealed class StopDocument
    {
        [JsonPropertyName("location")]
        public required string Location { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }
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
