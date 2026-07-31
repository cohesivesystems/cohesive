using System.Text;
using Cohesive.Adapters.Elastic;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticMaterializationWireJsonTests
{
    [Fact]
    public void Parse_PreservesOpaqueBytesAndDefensivelyOwnsThem()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            """ { "escaped" : "\/", "escapedLineFeed" : "\n", "number" : 1.00 } """);
        byte[] expected = [.. source];

        var value = ElasticJsonObject.Parse(source, nameof(source));
        source[0] = (byte)'x';
        var returned = value.ToArray();
        returned[1] = (byte)'x';

        Assert.Equal(expected.Length, value.Length);
        Assert.Equal(expected, value.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{} {}")]
    public void Parse_RejectsEmptyNonObjectAndMultipleRootContent(string source)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ElasticJsonObject.Parse(Encoding.UTF8.GetBytes(source), "payload"));

        Assert.Equal("payload", exception.ParamName);
    }

    [Theory]
    [InlineData("{\"value\":1}\r")]
    [InlineData("{\n\"value\":1}")]
    public void Parse_RejectsRawCarriageReturnAndLineFeed(string source)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ElasticJsonObject.Parse(Encoding.UTF8.GetBytes(source), "payload"));

        Assert.Equal("payload", exception.ParamName);
        Assert.Contains("one wire line", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateControlIndexBody_ProducesExactSchema()
    {
        var body = ElasticMaterializationWireJson.CreateControlIndexBody(
            ".cohesive-marker-initial",
            maximumIndexedIdentityCharacters: 321);

        const string expected = """{"settings":{"index.hidden":true,"number_of_shards":1},"mappings":{"dynamic":false,"properties":{"documentKind":{"type":"keyword"},"generationId":{"type":"keyword","ignore_above":321},"retained":{"type":"boolean"}}},"aliases":{".cohesive-marker-initial":{"is_hidden":true}}}""";
        AssertExact(expected, body);
    }

    [Fact]
    public void CreateGenerationIndexBody_ProducesExactSchema()
    {
        var body = ElasticMaterializationWireJson.CreateGenerationIndexBody(
            "binding-fingerprint",
            "template-fingerprint",
            "generation/001",
            ".cohesive-owner-generation",
            maximumIndexedIdentityCharacters: 321);

        const string expected = """{"settings":{"index.hidden":true,"index.meta.cohesive_binding":"binding-fingerprint","index.meta.cohesive_template":"template-fingerprint","index.meta.cohesive_generation":"generation/001"},"mappings":{"properties":{"_cohesive":{"type":"object","dynamic":false,"properties":{"generationId":{"type":"keyword","index":false,"doc_values":false},"itemId":{"type":"keyword","ignore_above":321},"mutationId":{"type":"keyword","index":false,"doc_values":false},"mutationFingerprint":{"type":"keyword","index":false,"doc_values":false},"version":{"type":"long"},"deleted":{"type":"boolean"}}}}},"aliases":{".cohesive-owner-generation":{"is_hidden":true}}}""";
        AssertExact(expected, body);
    }

    [Fact]
    public void CreateAliasBody_ProducesExactOrderedCasAndPreservesOpaqueFilter()
    {
        var filter = ElasticJsonObject.Parse(
            Encoding.UTF8.GetBytes("""{"term" : {"tenant" : "north\/west"}}"""),
            "filter");
        ElasticAliasCasRequest request = new(
            markerIndex: ".cohesive-control",
            expectedMarkerAlias: ".cohesive-marker-4",
            nextMarkerAlias: ".cohesive-marker-5",
            readAlias: "loads-read",
            expectedReadIndex: "generation-old",
            nextReadIndex: "generation-new",
            maximumResponseBytes: 4_096,
            readAliasFilter: filter,
            routing: "route-all",
            searchRouting: "route-search",
            indexRouting: "route-index",
            isWriteIndex: false,
            expectedNextOwnerAlias: ".cohesive-owner-next");

        var body = ElasticMaterializationWireJson.CreateAliasBody(request);

        const string expected = """{"actions":[{"remove":{"index":".cohesive-control","alias":".cohesive-marker-4","must_exist":true}},{"remove":{"index":"generation-new","alias":".cohesive-owner-next","must_exist":true}},{"add":{"index":"generation-new","alias":".cohesive-owner-next","is_hidden":true}},{"remove":{"index":"generation-old","alias":"loads-read","must_exist":true}},{"add":{"index":"generation-new","alias":"loads-read","filter":{"term" : {"tenant" : "north\/west"}},"routing":"route-all","search_routing":"route-search","index_routing":"route-index","is_write_index":false}},{"add":{"index":".cohesive-control","alias":".cohesive-marker-5","is_hidden":true}}]}""";
        AssertExact(expected, body);
    }

    [Fact]
    public void TermAndFilteredQueries_ProduceExactSchemas()
    {
        var documentKind = ElasticMaterializationWireJson.StringTermQuery(
            "documentKind",
            "generation");
        var retained = ElasticMaterializationWireJson.BooleanTermQuery("retained", value: true);

        AssertExact("""{"term":{"documentKind":"generation"}}""", documentKind);
        AssertExact("""{"term":{"retained":true}}""", retained);
        AssertExact(
            """{"bool":{"filter":[{"term":{"documentKind":"generation"}},{"term":{"retained":true}}]}}""",
            ElasticMaterializationWireJson.FilteredQuery(documentKind, retained));
    }

    [Fact]
    public void CreateMultiGetBody_FullProjectionOmitsSourceSelection()
    {
        var body = ElasticMaterializationWireJson.CreateMultiGetBody(
            ["item-a"],
            ElasticMultiGetSourceProjection.Full);

        AssertExact("""{"ids":["item-a"]}""", body);
    }

    [Fact]
    public void CreateScanBody_FirstPageOmitsSearchAfter()
    {
        ElasticScanRequest request = new(
            "generation-a",
            ElasticMaterializationWireJson.MatchAllQuery,
            "_cohesive.itemId",
            afterSortValue: null,
            maximumItems: 2,
            maximumResponseBytes: 4_096);

        var body = ElasticMaterializationWireJson.CreateScanBody(request);

        AssertExact(
            """{"size":3,"track_total_hits":false,"_source":true,"query":{"match_all":{}},"sort":[{"_cohesive.itemId":"asc"}]}""",
            body);
    }

    [Fact]
    public void CreateAliasBody_MarkerOnlyOmitsReadPublicationActions()
    {
        ElasticAliasCasRequest request = new(
            markerIndex: ".cohesive-control",
            expectedMarkerAlias: ".cohesive-marker-4",
            nextMarkerAlias: ".cohesive-marker-5",
            readAlias: null,
            expectedReadIndex: null,
            nextReadIndex: null,
            maximumResponseBytes: 4_096);

        var body = ElasticMaterializationWireJson.CreateAliasBody(request);

        AssertExact(
            """{"actions":[{"remove":{"index":".cohesive-control","alias":".cohesive-marker-4","must_exist":true}},{"add":{"index":".cohesive-control","alias":".cohesive-marker-5","is_hidden":true}}]}""",
            body);
    }

    static void AssertExact(string expected, ElasticJsonObject actual) =>
        Assert.Equal(Encoding.UTF8.GetBytes(expected), actual.ToArray());
}
