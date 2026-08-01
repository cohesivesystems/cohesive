using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using global::Elastic.Clients.Elasticsearch;
using global::Elastic.Transport;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticMaterializationTargetBindingTests
{
    [Fact]
    public void Binding_RoundTripsAndDerivesDeterministicGenerationIndexNames()
    {
        var binding = CreateBinding();
        var options = MaterializationJsonSerializer.CreateOptions();
        var json = JsonSerializer.Serialize(binding, options);
        var restored = JsonSerializer.Deserialize<ElasticMaterializationTargetBinding>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(binding.Fingerprint, restored.Fingerprint);
        Assert.Equal(binding.SingleWriter, restored.SingleWriter);
        Assert.Equal(binding.SearchBinding.Fingerprint, restored.SearchBinding.Fingerprint);

        var first = binding.GetGenerationIndexName(new("generation/001"));
        var repeated = binding.GetGenerationIndexName(new("generation/001"));
        var next = binding.GetGenerationIndexName(new("generation/002"));

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, next);
        Assert.StartsWith(binding.GenerationIndexPrefix, first, StringComparison.Ordinal);
        Assert.Equal(binding.GenerationIndexPrefix.Length + 64, first.Length);
        Assert.Matches("^[a-z0-9-]+$", first);
    }

    [Fact]
    public void PersistedConstructor_RejectsTamperedFingerprint()
    {
        var binding = CreateBinding();
        var tampered = new ElasticMaterializationTargetBindingFingerprint(
            binding.Fingerprint.Algorithm,
            binding.Fingerprint.Canonicalization,
            new string('0', 64));

        var exception = Assert.Throws<ArgumentException>(() => new ElasticMaterializationTargetBinding(
            binding.SchemaVersion,
            tampered,
            binding.Id,
            binding.Cluster,
            binding.TargetId,
            binding.MaterializationId,
            binding.ReadAlias,
            binding.GenerationIndexPrefix,
            binding.ControlIndexName,
            binding.IndexTemplate,
            binding.SingleWriter,
            binding.SearchBinding));

        Assert.Equal("fingerprint", exception.ParamName);
    }

    [Fact]
    public void Binding_RejectsSearchBindingThatDoesNotAddressStableReadAlias()
    {
        var search = CreateSearchBinding("other-read");

        var exception = Assert.Throws<ArgumentException>(() => CreateBinding(searchBinding: search));

        Assert.Equal("searchBinding", exception.ParamName);
    }

    [Fact]
    public void Binding_RejectsStableSearchViewForSwappableReadAlias()
    {
        var search = CreateSearchBinding(
            "loads-read",
            paginationConsistency: ElasticRelationQueryPaginationConsistency.StableSearchView);

        var exception = Assert.Throws<ArgumentException>(() => CreateBinding(searchBinding: search));

        Assert.Equal("searchBinding", exception.ParamName);
        Assert.Contains("swappable materialization read alias", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_AcceptsEveryPhysicalPathInsideMaterializedValueEnvelope()
    {
        ElasticRelationQueryFieldBinding scalar = new(
            new("input/customer-name"),
            FieldPath.Parse("value.customerName"),
            FieldPath.Parse("value.customerName.keyword"),
            ElasticRelationQueryFieldMappingKind.Keyword,
            ElasticRelationQueryFieldRetrievalKind.Source,
            ElasticRelationQueryFieldValueEncoding.JsonString,
            ElasticRelationQueryFieldDocumentScope.RootDocument,
            ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix,
            FieldPath.Parse("value.customerName.reversed"),
            "tests/reversed-keyword/v1");
        var nested = CreateNestedField("value.stops", "value.stops.location.keyword");

        var binding = CreateBinding(searchBinding: CreateSearchBinding("loads-read", [scalar, nested]));

        Assert.Equal(2, binding.SearchBinding.Fields.Length);
    }

    [Theory]
    [InlineData("customerId")]
    [InlineData("_cohesive.itemId")]
    public void Binding_RejectsSourcePathOutsideMaterializedValueEnvelope(string path)
    {
        var search = CreateSearchBinding("loads-read", [CreateSourceOnlyField(path)]);

        var exception = Assert.Throws<ArgumentException>(() => CreateBinding(searchBinding: search));

        Assert.Equal("searchBinding", exception.ParamName);
        Assert.Contains($"source path '{path}'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status.keyword")]
    [InlineData("_cohesive.itemId")]
    [InlineData("_id")]
    public void Binding_RejectsQueryPathOutsideMaterializedValueEnvelopeOrTargetingMetadataId(string path)
    {
        var search = CreateSearchBinding("loads-read", [CreateQueryOnlyField(path)]);

        var exception = Assert.Throws<ArgumentException>(() => CreateBinding(searchBinding: search));

        Assert.Equal("searchBinding", exception.ParamName);
        Assert.Contains($"query path '{path}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_RejectsReversedSuffixPathOutsideMaterializedValueEnvelope()
    {
        ElasticRelationQueryFieldBinding field = new(
            new("input/customer-name"),
            FieldPath.Parse("value.customerName"),
            FieldPath.Parse("value.customerName.keyword"),
            ElasticRelationQueryFieldMappingKind.Keyword,
            ElasticRelationQueryFieldRetrievalKind.Source,
            ElasticRelationQueryFieldValueEncoding.JsonString,
            ElasticRelationQueryFieldDocumentScope.RootDocument,
            ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix,
            FieldPath.Parse("customerName.reversed"),
            "tests/reversed-keyword/v1");
        var search = CreateSearchBinding("loads-read", [field]);

        var exception = Assert.Throws<ArgumentException>(() => CreateBinding(searchBinding: search));

        Assert.Equal("searchBinding", exception.ParamName);
        Assert.Contains("reversed-suffix query path 'customerName.reversed'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_RejectsNestedScopeOutsideMaterializedValueEnvelope()
    {
        var search = CreateSearchBinding(
            "loads-read",
            [CreateNestedField("stops", "stops.location.keyword")]);

        var exception = Assert.Throws<ArgumentException>(() => CreateBinding(searchBinding: search));

        Assert.Equal("searchBinding", exception.ParamName);
        Assert.Contains("query path 'stops'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_RejectsNestedChildThatTargetsMetadataId()
    {
        var search = CreateSearchBinding(
            "loads-read",
            [CreateNestedField("value.stops", "value.stops._id")]);

        var exception = Assert.Throws<ArgumentException>(() => CreateBinding(searchBinding: search));

        Assert.Equal("searchBinding", exception.ParamName);
        Assert.Contains("nested-child query path 'value.stops._id'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_CoversSingleWriterAuthorityAndScope()
    {
        var baseline = CreateBinding();
        var changedAuthority = CreateBinding(
            singleWriter: new("tests/process-runtime/v2", "search-index/loads"));
        var changedScope = CreateBinding(
            singleWriter: new("tests/process-runtime/v1", "search-index/shipments"));

        Assert.NotEqual(baseline.Fingerprint, changedAuthority.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, changedScope.Fingerprint);
    }

    [Fact]
    public void Profile_ConstrainsFencedMutationsAndComposesAliasMarkerPromotion()
    {
        var binding = CreateBinding();
        var runtime = CreateRuntime(binding.Cluster);
        var profile = ElasticMaterializationTargetProfile.Create(
            binding,
            ElasticMaterializationTargetPolicy.Default,
            runtime);

        Assert.Equal(MaterializationEndpointRole.Target, profile.Role);
        Assert.Equal(binding.TargetId.Value, profile.Subject);
        Assert.Equal(9, profile.Evidence.Length);

        foreach (var evidence in profile.Evidence.Where(static item =>
                     item.Guarantees.Contains(MaterializationGuaranteeKind.FencedMutation)))
        {
            Assert.Equal(CapabilityRealizationKind.Constrained, evidence.Realization);
            Assert.Contains(
                $"elastic-single-writer-authority:{binding.SingleWriter.Authority}",
                evidence.SourceReferences);
            Assert.Contains(
                $"elastic-single-writer-scope:{binding.SingleWriter.Scope}",
                evidence.SourceReferences);
        }

        var promotion = Assert.Single(profile.Evidence, static item =>
            item.Capability == MaterializationCapabilityKind.TargetFencedPromotion);
        Assert.Equal(CapabilityRealizationKind.Composed, promotion.Realization);
        Assert.Contains("alias-marker compare-and-swap", promotion.Description, StringComparison.Ordinal);
        Assert.Contains(MaterializationGuaranteeKind.AtomicPromotion, promotion.Guarantees);
        Assert.Contains(MaterializationGuaranteeKind.FencedPromotion, promotion.Guarantees);

        var validation = Assert.Single(profile.Evidence, static item =>
            item.Capability == MaterializationCapabilityKind.TargetValidation);
        Assert.Contains("live template drift requires deployment validation", validation.Description, StringComparison.Ordinal);

        foreach (var evidence in profile.Evidence.Where(static item => item.Capability is
                     MaterializationCapabilityKind.TargetGenerationIsolation
                     or MaterializationCapabilityKind.TargetBulkUpsert
                     or MaterializationCapabilityKind.TargetBulkDelete
                     or MaterializationCapabilityKind.TargetPerItemOutcomes))
        {
            Assert.Contains(
                evidence.OperatingLimits,
                limit => limit.Kind == MaterializationLimitKind.IndexedIdentityCharacters
                    && limit.Maximum == ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters);
            Assert.Contains(
                $"elastic-indexed-identity-characters:{ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters}",
                evidence.SourceReferences);
        }
    }

    [Fact]
    public void ProfileIdentity_CoversPolicyRuntimeAndSingleWriterEvidence()
    {
        var binding = CreateBinding();
        var runtime = CreateRuntime(binding.Cluster);
        var baseline = ElasticMaterializationTargetProfile.GetProfileId(
            binding,
            ElasticMaterializationTargetPolicy.Default,
            runtime);
        var changedPolicy = ElasticMaterializationTargetProfile.GetProfileId(
            binding,
            new(500, 2 * 1024 * 1024, 4, 32 * 1024),
            runtime);
        var changedRuntime = ElasticMaterializationTargetProfile.GetProfileId(
            binding,
            ElasticMaterializationTargetPolicy.Default,
            CreateRuntime(binding.Cluster, "tests/elastic-runtime/v2"));
        var changedSingleWriter = ElasticMaterializationTargetProfile.GetProfileId(
            CreateBinding(singleWriter: new("tests/process-runtime/v1", "search-index/shipments")),
            ElasticMaterializationTargetPolicy.Default,
            runtime);

        Assert.NotEqual(baseline, changedPolicy);
        Assert.NotEqual(baseline, changedRuntime);
        Assert.NotEqual(baseline, changedSingleWriter);
    }

    [Fact]
    public void Profile_RejectsRuntimeForDifferentCluster()
    {
        var binding = CreateBinding();

        var exception = Assert.Throws<ArgumentException>(() => ElasticMaterializationTargetProfile.Create(
            binding,
            ElasticMaterializationTargetPolicy.Default,
            CreateRuntime(new("different-cluster"))));

        Assert.Equal("runtimeBinding", exception.ParamName);
    }

    [Fact]
    public void RuntimeBinding_RejectsEndpointOrCredentialShapedAuthority()
    {
        var client = CreateClient();

        var exception = Assert.Throws<ArgumentException>(() => new ElasticElasticsearchRuntimeBinding(
            new("cluster-uuid"),
            client,
            "https://elastic.example.test?api_key=secret"));

        Assert.Equal("authority", exception.ParamName);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1, 0)]
    public void Policy_RejectsNonPositiveBounds(
        int maximumBatchItems,
        long maximumBatchBytes,
        int maximumParallelism,
        int maximumDiagnosticBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElasticMaterializationTargetPolicy(
            maximumBatchItems,
            maximumBatchBytes,
            maximumParallelism,
            maximumDiagnosticBytes));

    static ElasticMaterializationTargetBinding CreateBinding(
        ElasticMaterializationSingleWriterEvidence? singleWriter = null,
        ElasticRelationQueryStorageBinding? searchBinding = null)
    {
        const string readAlias = "loads-read";
        return new(
            new("tests/elastic-materialization-target/v1"),
            new("cluster-uuid"),
            new("target/search"),
            new("materialization/search"),
            readAlias,
            "loads-generation-",
            ".cohesive-materialization-control",
            new(
                "loads-template",
                new("sha256", "elastic-index-template/v1", new string('a', 64)),
                "tests/elastic-template/v1"),
            singleWriter ?? new("tests/process-runtime/v1", "search-index/loads"),
            searchBinding ?? CreateSearchBinding(readAlias));
    }

    static ElasticRelationQueryStorageBinding CreateSearchBinding(
        string indexName,
        ImmutableArray<ElasticRelationQueryFieldBinding> fields = default,
        ElasticRelationQueryPaginationConsistency paginationConsistency =
            ElasticRelationQueryPaginationConsistency.Unproven) => new(
        new("tests/elastic-search-binding/v1"),
        new RelationQuerySourceInstanceId("search/materialized-loads"),
        new RelationQuerySourcePlacementBindingId("search/materialized-loads/placement"),
        ElasticRelationQueryTargetProfile.Target,
        ElasticRelationQueryTargetProfile.ProfileId,
        indexName,
        fields.IsDefault ? [] : fields,
        paginationConsistency: paginationConsistency);

    static ElasticRelationQueryFieldBinding CreateSourceOnlyField(string path) => new(
        new RelationQueryInputId("input/source"),
        FieldPath.Parse(path),
        queryField: null,
        ElasticRelationQueryFieldMappingKind.Unindexed,
        ElasticRelationQueryFieldRetrievalKind.Source,
        ElasticRelationQueryFieldValueEncoding.JsonString);

    static ElasticRelationQueryFieldBinding CreateQueryOnlyField(string path) => new(
        new RelationQueryInputId("input/query"),
        sourceField: null,
        FieldPath.Parse(path),
        ElasticRelationQueryFieldMappingKind.Keyword,
        ElasticRelationQueryFieldRetrievalKind.Unavailable,
        retrievalEncoding: null);

    static ElasticRelationQueryFieldBinding CreateNestedField(string nestedPath, string childPath)
    {
        var physicalNestedPath = FieldPath.Parse(nestedPath);
        return new(
            new("input/stops"),
            sourceField: null,
            physicalNestedPath,
            ElasticRelationQueryFieldMappingKind.Nested,
            ElasticRelationQueryFieldRetrievalKind.Unavailable,
            retrievalEncoding: null,
            ElasticRelationQueryFieldDocumentScope.NestedDocument,
            missingValueBehavior: ElasticRelationQueryMissingValueBehavior.ProhibitedByIngestion,
            nullValueBehavior: ElasticRelationQueryNullValueBehavior.ProhibitedByIngestion,
            nestedScope: new(
                physicalNestedPath,
                ElasticRelationQueryNestedCorrelationGuarantee.SameNestedDocument,
                ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion,
                ElasticRelationQueryEmptyCollectionBehavior.NoNestedDocuments,
                [
                    new(
                        FieldPath.FromField("location"),
                        FieldPath.Parse(childPath),
                        ElasticRelationQueryFieldMappingKind.Keyword,
                        ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                        "tests/nested-keyword/v1",
                        ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion,
                        ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion)
                ]));
    }

    static ElasticElasticsearchRuntimeBinding CreateRuntime(
        ElasticClusterId cluster,
        string authority = "tests/elastic-runtime/v1") =>
        new(cluster, CreateClient(), authority);

    static ElasticsearchClient CreateClient() => new(
        new ElasticsearchClientSettings(new InMemoryRequestInvoker()));
}
