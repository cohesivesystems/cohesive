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

    [Fact]
    public void JsonRoundTrip_PreservesCollectionMembershipEvidenceAndFingerprint()
    {
        var binding = CreateBinding();

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<ElasticRelationQueryStorageBinding>(json, JsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(binding.SchemaVersion, rehydrated.SchemaVersion);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        var field = Assert.Single(rehydrated.Fields);
        Assert.Equal(
            ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership,
            field.SemanticCapabilities);
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, JsonOptions));
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

    static ElasticRelationQueryStorageBinding CreateBinding() => new(
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
        origin: ElasticRelationQueryBindingOrigin.Convention,
        conventionSetVersion: ElasticRelationQueryStorageBinding.SemanticPathConventionSet);

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
            origin: ElasticRelationQueryBindingOrigin.Convention,
            conventionSetVersion: ElasticRelationQueryStorageBinding.SemanticPathConventionSet);
    }
}
