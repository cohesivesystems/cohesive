using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Cohesive.Model;
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
}
