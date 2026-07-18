using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Adapters.Elastic;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Model;

public sealed class ElasticRelationQueryStorageBindingPersistenceTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly RelationQueryPlanComponentFingerprint CompiledPlanFingerprint = new(
        "sha256",
        "tests/compiled-plan/v1",
        "compiled-plan");
    static readonly RelationQuerySourcePlacementFingerprint PlacementFingerprint = new(
        "sha256",
        "tests/source-placement/v1",
        "source-placement");

    [Fact]
    public void JsonRoundTrip_PreservesCollectionMembershipEvidenceAndFingerprint()
    {
        var binding = CreateBinding(includeAffinity: true);

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<ElasticRelationQueryStorageBinding>(json, JsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(ElasticRelationQueryStorageBinding.CurrentSchemaVersion, rehydrated.SchemaVersion);
        Assert.Equal(binding.SchemaVersion, rehydrated.SchemaVersion);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(binding.CompiledPlanFingerprint, rehydrated.CompiledPlanFingerprint);
        Assert.Equal(binding.PlacementFingerprint, rehydrated.PlacementFingerprint);
        var field = Assert.Single(rehydrated.Fields);
        Assert.Equal(
            ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership,
            field.SemanticCapabilities);
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, JsonOptions));
    }

    [Fact]
    public void ConfigurationDecisions_NormalizeRoundTripAndParticipateInFingerprint()
    {
        ImmutableArray<RelationQueryConfigurationDecision> decisions =
        [
            new(
                "sourceMode",
                RelationQueryConfigurationValueOrigin.AdapterConvention,
                "tests/elastic-conventions/v1"),
            new(
                "indexName",
                RelationQueryConfigurationValueOrigin.Explicit,
                "tests/deployment/v3")
        ];
        var first = CreateBinding(decisions);
        var reversed = CreateBinding([.. decisions.Reverse()]);
        var changedAuthority = CreateBinding(
        [
            decisions[0],
            new(
                decisions[1].Setting,
                decisions[1].Origin,
                "tests/deployment/v4")
        ]);

        var json = JsonSerializer.Serialize(first, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<ElasticRelationQueryStorageBinding>(json, JsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(first.ConfigurationDecisions.ToArray(), rehydrated.ConfigurationDecisions.ToArray());
        Assert.Equal(first.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(first.Fingerprint, reversed.Fingerprint);
        Assert.Equal(json, JsonSerializer.Serialize(reversed, JsonOptions));
        Assert.NotEqual(first.Fingerprint, changedAuthority.Fingerprint);
        Assert.Throws<ArgumentException>(() => CreateBinding([decisions[0], decisions[0]]));
    }

    [Fact]
    public void BindingAffinity_IsPairedFingerprintContentWhileOmissionRemainsAnUnverifiedEscapeHatch()
    {
        var verified = CreateBinding(includeAffinity: true);
        var unverified = CreateBinding();
        var missingPlacement = SerializeToObject(verified);
        missingPlacement.Remove("placementFingerprint");

        Assert.Equal(CompiledPlanFingerprint, verified.CompiledPlanFingerprint);
        Assert.Equal(PlacementFingerprint, verified.PlacementFingerprint);
        Assert.Null(unverified.CompiledPlanFingerprint);
        Assert.Null(unverified.PlacementFingerprint);
        Assert.NotEqual(verified.Fingerprint, unverified.Fingerprint);
        var exception = Assert.Throws<ArgumentException>(() => Deserialize(missingPlacement));
        Assert.Contains("must be supplied together", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRehydration_RejectsForeignSettingsAndConventionOriginWithConsumerProvenance()
    {
        ImmutableArray<RelationQueryConfigurationDecision> decisions =
        [
            new("indexName", RelationQueryConfigurationValueOrigin.Explicit, "tests/deployment/v1")
        ];
        var foreign = SerializeToObject(CreateBinding(decisions));
        foreign["configurationDecisions"]!.AsArray()[0]!["setting"] = "cosmosOnlySetting";
        var foreignException = Assert.Throws<ArgumentException>(() => Deserialize(foreign));

        var convention = SerializeToObject(CreateBinding(decisions));
        convention["origin"] = (int)ElasticRelationQueryBindingOrigin.Convention;
        var originException = Assert.Throws<ArgumentException>(() => Deserialize(convention));

        Assert.Contains("does not belong", foreignException.ToString(), StringComparison.Ordinal);
        Assert.Contains("cannot retain explicit", originException.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRoundTrip_PreservesNestedCorrelationChildMappingsAndFingerprint()
    {
        var binding = CreateNestedBinding();

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<ElasticRelationQueryStorageBinding>(json, JsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(binding.Fields.ToArray(), rehydrated.Fields.ToArray());
        var field = Assert.Single(rehydrated.Fields);
        var nested = field.NestedScope;
        Assert.NotNull(nested);
        Assert.Equal(FieldPath.Parse("stops"), nested.NestedPath);
        Assert.Equal(ElasticRelationQueryNestedCorrelationGuarantee.SameNestedDocument, nested.CorrelationGuarantee);
        Assert.Equal(ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion, nested.NullElementBehavior);
        Assert.Equal(ElasticRelationQueryMissingValueBehavior.ProhibitedByIngestion, field.MissingValueBehavior);
        Assert.Equal(ElasticRelationQueryNullValueBehavior.ProhibitedByIngestion, field.NullValueBehavior);
        Assert.Equal(ElasticRelationQueryEmptyCollectionBehavior.NoNestedDocuments, nested.EmptyCollectionBehavior);
        Assert.Equal(
            FieldPath.Parse("stops.location.keyword"),
            nested.ResolveChild(FieldPath.Parse("Location")).QueryField);
        Assert.Equal(
            FieldPath.Parse("stops.type.keyword"),
            nested.ResolveChild(FieldPath.Parse("Type")).QueryField);
        Assert.Equal(
            CreateNestedPathDecisions().OrderBy(static decision => decision.Setting).ToArray(),
            rehydrated.ConfigurationDecisions.ToArray());
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, JsonOptions));
    }

    [Fact]
    public void NestedBindingFingerprint_NormalizesChildOrderAndChangesWithCorrelationEvidence()
    {
        var first = CreateNestedBinding();
        var reversed = CreateNestedBinding(reverseChildren: true);
        var unproven = CreateNestedBinding(
            correlation: ElasticRelationQueryNestedCorrelationGuarantee.Unproven);
        var weakAbsence = CreateNestedBinding(
            missingValueBehavior: ElasticRelationQueryMissingValueBehavior.NotIndexed);
        var droppedNullElement = CreateNestedBinding(
            nullElementBehavior: ElasticRelationQueryNestedAbsenceBehavior.NotIndexed);

        Assert.Equal(first.Fingerprint, reversed.Fingerprint);
        Assert.Equal(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonSerializer.Serialize(reversed, JsonOptions));
        Assert.NotEqual(first.Fingerprint, unproven.Fingerprint);
        Assert.NotEqual(first.Fingerprint, weakAbsence.Fingerprint);
        Assert.NotEqual(first.Fingerprint, droppedNullElement.Fingerprint);
    }

    [Fact]
    public void JsonRehydration_RejectsNestedEvidenceTamperedAfterFingerprinting()
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(CreateNestedBinding(), JsonOptions))!.AsObject();
        document["fields"]!.AsArray()[0]!["nestedScope"]!["correlationGuarantee"] =
            (int)ElasticRelationQueryNestedCorrelationGuarantee.Unproven;

        var exception = Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<ElasticRelationQueryStorageBinding>(document.ToJsonString(), JsonOptions));

        Assert.Contains("fingerprint does not match normalized content", exception.ToString(), StringComparison.Ordinal);
    }

    static ElasticRelationQueryStorageBinding CreateBinding(
        ImmutableArray<RelationQueryConfigurationDecision> configurationDecisions = default,
        bool includeAffinity = false) => new(
        id: new("load-search/v1"),
        source: new RelationQuerySourceInstanceId("loads-source"),
        placementBinding: new RelationQuerySourcePlacementBindingId("loads-placement"),
        target: new RelationQueryTargetId("elastic"),
        targetProfile: new RelationQueryTargetProfileId("elastic-query/v1"),
        indexName: "loads-read",
        fields:
        [
            new(
                new RelationQueryInputId("field:stop-locations"),
                sourceField: null,
                queryField: FieldPath.Parse("stopLocations.keyword"),
                mappingKind: ElasticRelationQueryFieldMappingKind.Keyword,
                retrievalKind: ElasticRelationQueryFieldRetrievalKind.Unavailable,
                retrievalEncoding: null,
                documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
                semanticCapabilities: ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership,
                semanticProfile: "tests/ordinal-keyword-array-v1")
        ],
        origin: !configurationDecisions.IsDefaultOrEmpty
        && configurationDecisions.Any(static decision => decision.Origin is
                RelationQueryConfigurationValueOrigin.Explicit
                or RelationQueryConfigurationValueOrigin.ScopedProfile)
            ? ElasticRelationQueryBindingOrigin.Explicit
            : ElasticRelationQueryBindingOrigin.Convention,
        conventionSetVersion: ElasticRelationQueryStorageBinding.SemanticPathConventionSet,
        configurationDecisions: configurationDecisions,
        compiledPlanFingerprint: includeAffinity ? CompiledPlanFingerprint : null,
        placementFingerprint: includeAffinity ? PlacementFingerprint : null);

    static ElasticRelationQueryStorageBinding CreateNestedBinding(
        bool reverseChildren = false,
        ElasticRelationQueryNestedCorrelationGuarantee correlation =
            ElasticRelationQueryNestedCorrelationGuarantee.SameNestedDocument,
        ElasticRelationQueryNestedAbsenceBehavior nullElementBehavior =
            ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion,
        ElasticRelationQueryMissingValueBehavior missingValueBehavior =
            ElasticRelationQueryMissingValueBehavior.ProhibitedByIngestion)
    {
        ImmutableArray<ElasticRelationQueryNestedChildFieldBinding> children =
        [
            new(
                FieldPath.Parse("Location"),
                FieldPath.Parse("stops.location.keyword"),
                ElasticRelationQueryFieldMappingKind.Keyword,
                ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                "tests/ordinal-keyword-v1",
                ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion,
                ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion),
            new(
                FieldPath.Parse("Type"),
                FieldPath.Parse("stops.type.keyword"),
                ElasticRelationQueryFieldMappingKind.Keyword,
                ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                "tests/ordinal-keyword-v1",
                ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion,
                ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion)
        ];
        if (reverseChildren)
        {
            children = [.. children.Reverse()];
        }

        return new(
            id: new("load-search-nested/v2"),
            source: new RelationQuerySourceInstanceId("loads-source"),
            placementBinding: new RelationQuerySourcePlacementBindingId("loads-placement"),
            target: ElasticRelationQueryTargetProfile.Target,
            targetProfile: ElasticRelationQueryTargetProfile.ProfileId,
            indexName: "loads-read",
            fields:
            [
                new(
                    new RelationQueryInputId("field:stops"),
                    sourceField: null,
                    queryField: FieldPath.Parse("stops"),
                    mappingKind: ElasticRelationQueryFieldMappingKind.Nested,
                    retrievalKind: ElasticRelationQueryFieldRetrievalKind.Unavailable,
                    retrievalEncoding: null,
                    documentScope: ElasticRelationQueryFieldDocumentScope.NestedDocument,
                    missingValueBehavior: missingValueBehavior,
                    nullValueBehavior: ElasticRelationQueryNullValueBehavior.ProhibitedByIngestion,
                    nestedScope: new(
                        FieldPath.Parse("stops"),
                        correlation,
                        nullElementBehavior,
                        ElasticRelationQueryEmptyCollectionBehavior.NoNestedDocuments,
                        children))
            ],
            origin: ElasticRelationQueryBindingOrigin.Explicit,
            conventionSetVersion: ElasticRelationQueryStorageBinding.SemanticPathConventionSet,
            configurationDecisions: CreateNestedPathDecisions());
    }

    static ImmutableArray<RelationQueryConfigurationDecision> CreateNestedPathDecisions()
    {
        const string prefix = "field/field:stops/nested/";
        const string authority = "tests/nested-mapping/v2";
        return
        [
            new(
                prefix + "nestedPath",
                RelationQueryConfigurationValueOrigin.Explicit,
                authority),
            new(
                prefix + "child/" + DirectFieldSettingKey("Location") + "/elementPath",
                RelationQueryConfigurationValueOrigin.Explicit,
                authority),
            new(
                prefix + "child/" + DirectFieldSettingKey("Type") + "/elementPath",
                RelationQueryConfigurationValueOrigin.Explicit,
                authority)
        ];
    }

    static string DirectFieldSettingKey(string field) => $"0:{field.Length}:{field}";

    static JsonObject SerializeToObject(ElasticRelationQueryStorageBinding binding) =>
        JsonNode.Parse(JsonSerializer.Serialize(binding, JsonOptions))!.AsObject();

    static ElasticRelationQueryStorageBinding? Deserialize(JsonObject document) =>
        JsonSerializer.Deserialize<ElasticRelationQueryStorageBinding>(document.ToJsonString(), JsonOptions);
}
