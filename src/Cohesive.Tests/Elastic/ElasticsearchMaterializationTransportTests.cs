using System.Collections.Immutable;
using System.Text;
using Cohesive.Adapters.Elastic;
using global::Elastic.Clients.Elasticsearch;
using global::Elastic.Transport;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticsearchMaterializationTransportTests
{
    [Fact]
    public async Task BulkAsync_ReportsExactStrictExternalVersionWireBytesAndOrderedMixedOutcomes()
    {
        var transport = CreateTransport(
            """
            {
              "took": 3,
              "errors": true,
              "items": [
                {
                  "index": {
                    "_index": "generation-a",
                    "_id": "item-a",
                    "_version": 7,
                    "result": "created",
                    "status": 201,
                    "_seq_no": 4,
                    "_primary_term": 2
                  }
                },
                {
                  "delete": {
                    "_index": "generation-a",
                    "_id": "item-b",
                    "status": 429,
                    "error": {
                      "type": "es_rejected_execution_exception",
                      "reason": "bulk executor is saturated"
                    }
                  }
                }
              ]
            }
            """);
        ImmutableArray<ElasticBulkOperation> operations =
        [
            new(
                ElasticBulkOperationKind.Index,
                "generation-a",
                "item-a",
                externalVersion: 7,
                "{\"value\":1}"u8.ToArray()),
            new(
                ElasticBulkOperationKind.Delete,
                "generation-a",
                "item-b",
                externalVersion: 8)
        ];

        var result = await transport.BulkAsync(
            operations,
            maximumWireBytes: 4_096,
            maximumResponseBytes: 4_096,
            CancellationToken.None);

        const string expectedWireBody =
            "{\"index\":{\"_index\":\"generation-a\",\"_id\":\"item-a\",\"version\":7,\"version_type\":\"external\"}}\n"
            + "{\"value\":1}\n"
            + "{\"delete\":{\"_index\":\"generation-a\",\"_id\":\"item-b\",\"version\":8,\"version_type\":\"external\"}}\n";
        Assert.Equal(Encoding.UTF8.GetByteCount(expectedWireBody), result.WireBytes);
        Assert.True(result.Errors);
        Assert.Collection(
            result.Items,
            item =>
            {
                Assert.Equal(0, item.Ordinal);
                Assert.Equal(ElasticBulkOperationKind.Index, item.Kind);
                Assert.Equal(201, item.StatusCode);
                Assert.Equal(7, item.ExternalVersion);
                Assert.Equal(new ElasticDocumentConcurrencyToken(4, 2), item.ConcurrencyToken);
            },
            item =>
            {
                Assert.Equal(1, item.Ordinal);
                Assert.Equal(ElasticBulkOperationKind.Delete, item.Kind);
                Assert.Equal(429, item.StatusCode);
                Assert.Equal("es_rejected_execution_exception", item.ErrorType);
            });
    }

    [Fact]
    public async Task GetDocumentAsync_DistinguishesMissingDocumentFromMissingIndex()
    {
        var missingDocument = CreateTransport(
            """{"_index":"control","_id":"missing","found":false}""",
            statusCode: 404);

        var result = await missingDocument.GetDocumentAsync(
            "control",
            "missing",
            maximumResponseBytes: 1_024,
            CancellationToken.None);

        Assert.False(result.Found);

        var missingIndex = CreateTransport(
            """{"error":{"type":"index_not_found_exception","reason":"missing index"},"status":404}""",
            statusCode: 404);
        var exception = await Assert.ThrowsAsync<ElasticMaterializationTransportException>(async () =>
            await missingIndex.GetDocumentAsync(
                "control",
                "missing",
                maximumResponseBytes: 1_024,
                CancellationToken.None));
        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("index_not_found_exception", exception.ErrorType);
        Assert.Equal(
            "Elasticsearch get-control-document failed with HTTP 404 (index_not_found_exception).",
            exception.Message);
        Assert.DoesNotContain("missing index", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(404, false)]
    public async Task IndexExistsAsync_UsesBoundedHeadStatusWithoutRequiringABody(
        int statusCode,
        bool expected)
    {
        InMemoryRequestInvoker invoker = CreateInvoker(string.Empty, statusCode);
        List<ApiCallDetails> observed = [];
        var settings = new ElasticsearchClientSettings(invoker)
            .DisableDirectStreaming()
            .OnRequestCompleted(observed.Add);
        ElasticsearchMaterializationTransport transport = new(new ElasticsearchClient(settings));

        var exists = await transport.IndexExistsAsync(
            "generation-a",
            maximumResponseBytes: 1_024,
            CancellationToken.None);

        Assert.Equal(expected, exists);
        var call = Assert.Single(observed);
        Assert.Equal(global::Elastic.Transport.HttpMethod.HEAD, call.HttpMethod);
        Assert.Equal("/generation-a", call.Uri?.AbsolutePath);
    }

    [Fact]
    public async Task MultiGetAsync_ProjectsOnlyMaterializationMetadataForRecoveryReads()
    {
        InMemoryRequestInvoker invoker = CreateInvoker(
            "{\"docs\":[{\"_id\":\"item-a\",\"found\":true,\"_source\":{\"_cohesive\":{}},"
            + "\"_seq_no\":1,\"_primary_term\":1,\"_version\":2}]}",
            statusCode: 200);
        ApiCallDetails? observed = null;
        var settings = new ElasticsearchClientSettings(invoker)
            .DisableDirectStreaming()
            .OnRequestCompleted(details => observed = details);
        ElasticsearchMaterializationTransport transport = new(new ElasticsearchClient(settings));

        var result = await transport.MultiGetAsync(
            "generation-a",
            ["item-a"],
            ElasticMultiGetSourceProjection.MaterializationMetadata,
            maximumResponseBytes: 1_024,
            CancellationToken.None);

        Assert.True(Assert.Single(result.Documents).Found);
        var call = observed ?? throw new Xunit.Sdk.XunitException("The Elasticsearch request was not observed.");
        Assert.Equal(global::Elastic.Transport.HttpMethod.POST, call.HttpMethod);
        Assert.Equal("/generation-a/_mget", call.Uri?.AbsolutePath);
        Assert.Equal(
            "{\"_source\":[\"_cohesive\"],\"ids\":[\"item-a\"]}",
            Encoding.UTF8.GetString(call.RequestBodyInBytes!));
    }

    [Fact]
    public async Task DeleteOwnedIndexAsync_AtomicallyRequiresOwnerAliasBeforeRemovingIndex()
    {
        InMemoryRequestInvoker invoker = CreateInvoker("{\"acknowledged\":true}", statusCode: 200);
        ApiCallDetails? observed = null;
        var settings = new ElasticsearchClientSettings(invoker)
            .DisableDirectStreaming()
            .OnRequestCompleted(details => observed = details);
        ElasticsearchMaterializationTransport transport = new(new ElasticsearchClient(settings));

        var result = await transport.DeleteOwnedIndexAsync(
            "generation-a",
            ".cohesive-owner",
            maximumResponseBytes: 1_024,
            CancellationToken.None);

        Assert.Equal(ElasticOwnedIndexDeleteDisposition.Applied, result.Disposition);
        Assert.True(result.Acknowledged);
        var call = observed ?? throw new Xunit.Sdk.XunitException("The Elasticsearch request was not observed.");
        Assert.Equal(global::Elastic.Transport.HttpMethod.POST, call.HttpMethod);
        Assert.Equal("/_aliases", call.Uri?.AbsolutePath);
        Assert.Equal(
            "{\"actions\":[{\"remove\":{\"index\":\"generation-a\",\"alias\":\".cohesive-owner\",\"must_exist\":true}},{\"remove_index\":{\"index\":\"generation-a\"}}]}",
            Encoding.UTF8.GetString(call.RequestBodyInBytes!));
    }

    [Fact]
    public async Task WholeRequestFailure_RedactsProviderReasonAndRejectsUnsafeErrorType()
    {
        const string secret = "customer-document-secret-9817";
        var transport = CreateTransport(
            $$"""
            {
              "error": {
                "type": "mapper_parsing_exception {{secret}}",
                "reason": "failed to parse source value {{secret}}"
              },
              "status": 400
            }
            """,
            statusCode: 400);

        var exception = await Assert.ThrowsAsync<ElasticMaterializationTransportException>(async () =>
            await transport.GetDocumentAsync(
                "control",
                "document",
                maximumResponseBytes: 1_024,
                CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("elasticsearch.error", exception.ErrorType);
        Assert.Equal(
            "Elasticsearch get-control-document failed with HTTP 400 (elasticsearch.error).",
            exception.Message);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task MalformedResponse_DoesNotRetainParserExceptionOrDocumentContent()
    {
        const string secret = "malformed-document-secret-4312";
        var transport = CreateTransport(
            $$"""{"found":true,"_source":{"secret":"{{secret}}"}""",
            statusCode: 200);

        var exception = await Assert.ThrowsAsync<ElasticMaterializationTransportException>(async () =>
            await transport.GetDocumentAsync(
                "control",
                "document",
                maximumResponseBytes: 1_024,
                CancellationToken.None));

        Assert.Equal("cohesive.elasticsearch.protocol", exception.ErrorType);
        Assert.Equal("get-control-document response was not valid bounded JSON.", exception.Message);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task CompareExchangeAliasAsync_ClassifiesMissingMarkerAsFenceConflict()
    {
        var transport = CreateTransport(
            """{"error":{"type":"aliases_not_found_exception","reason":"marker missing"},"status":404}""",
            statusCode: 404);
        ElasticAliasCasRequest request = new(
            markerIndex: ".cohesive-control",
            expectedMarkerAlias: ".cohesive-marker-4",
            nextMarkerAlias: ".cohesive-marker-5",
            readAlias: null,
            expectedReadIndex: null,
            nextReadIndex: null,
            maximumResponseBytes: 1_024);

        var result = await transport.CompareExchangeAliasAsync(
            request,
            CancellationToken.None);

        Assert.Equal(ElasticAliasCasDisposition.Conflict, result.Disposition);
        Assert.False(result.Acknowledged);
    }

    [Fact]
    public async Task CompareExchangeAliasAsync_RefusesUnexpectedReadAliasOwners()
    {
        const string response =
            "{\".stray-hidden\":{\"aliases\":{\"loads-read\":{\"is_write_index\":true}}},"
            + "\"generation-old\":{\"aliases\":{\"loads-read\":{\"is_write_index\":false,"
            + "\"filter\":{\"term\":{\"_cohesive.deleted\":false}}}}}}";
        InMemoryRequestInvoker invoker = CreateInvoker(response, statusCode: 200);
        List<ApiCallDetails> observed = [];
        var settings = new ElasticsearchClientSettings(invoker)
            .DisableDirectStreaming()
            .OnRequestCompleted(observed.Add);
        ElasticsearchMaterializationTransport transport = new(new ElasticsearchClient(settings));
        ElasticAliasCasRequest request = new(
            markerIndex: ".cohesive-control",
            expectedMarkerAlias: ".cohesive-marker-4",
            nextMarkerAlias: ".cohesive-marker-5",
            readAlias: "loads-read",
            expectedReadIndex: "generation-old",
            nextReadIndex: "generation-new",
            maximumResponseBytes: 4_096,
            readAliasFilter: "{\"term\":{\"_cohesive.deleted\":false}}"u8.ToArray(),
            isWriteIndex: false);

        var result = await transport.CompareExchangeAliasAsync(request, CancellationToken.None);

        Assert.Equal(ElasticAliasCasDisposition.Conflict, result.Disposition);
        Assert.False(result.Acknowledged);
        var inspection = Assert.Single(observed);
        Assert.Equal(global::Elastic.Transport.HttpMethod.GET, inspection.HttpMethod);
        Assert.Equal("/_alias/loads-read", inspection.Uri?.AbsolutePath);
        Assert.Equal(
            "?ignore_unavailable=true&allow_no_indices=true&expand_wildcards=all",
            inspection.Uri?.Query);
    }

    [Fact]
    public async Task InspectAliasesAsync_RetainsFoundAliasesFromPartialNotFoundResponse()
    {
        const string response =
            "{\"error\":\"alias [marker-old] missing\",\"status\":404,"
            + "\".cohesive-control\":{\"aliases\":{\"marker-next\":{\"is_hidden\":true}}},"
            + "\"generation-next\":{\"aliases\":{\"loads-read\":{\"is_write_index\":false,"
            + "\"filter\":{\"term\":{\"_cohesive.deleted\":false}}}}}}";
        var transport = CreateTransport(response, statusCode: 404);

        var result = await transport.InspectAliasesAsync(
            ["marker-old", "marker-next", "loads-read"],
            maximumResponseBytes: 4_096,
            CancellationToken.None);

        Assert.Collection(
            result.Bindings,
            read =>
            {
                Assert.Equal("loads-read", read.Alias);
                Assert.Equal("generation-next", read.Index);
                Assert.False(read.IsWriteIndex);
            },
            marker =>
            {
                Assert.Equal("marker-next", marker.Alias);
                Assert.Equal(".cohesive-control", marker.Index);
                Assert.True(marker.IsHidden);
            });
    }

    [Fact]
    public async Task InspectAliasesAsync_DoesNotConfuseAnIndexNamedErrorWithNotFoundMetadata()
    {
        const string response =
            "{\"error\":\"alias [missing] missing\",\"status\":404,"
            + "\"error\":{\"aliases\":{\"found\":{\"is_hidden\":true}}}}";
        var transport = CreateTransport(response, statusCode: 404);

        var result = await transport.InspectAliasesAsync(
            ["missing", "found"],
            maximumResponseBytes: 4_096,
            CancellationToken.None);

        var found = Assert.Single(result.Bindings);
        Assert.Equal("found", found.Alias);
        Assert.Equal("error", found.Index);
        Assert.True(found.IsHidden);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddWriteBlockAsync_RequiresTheExplicitIndexToReportBlocked(bool blocked)
    {
        var response = blocked
            ? """{"acknowledged":true,"shards_acknowledged":true,"indices":[{"name":"generation-a","blocked":true}]}"""
            : """{"acknowledged":true,"shards_acknowledged":true,"indices":[{"name":"generation-a","blocked":false}]}""";
        var transport = CreateTransport(response);

        var result = await transport.AddWriteBlockAsync(
            "generation-a",
            maximumResponseBytes: 1_024,
            CancellationToken.None);

        Assert.Equal(ElasticAcknowledgedDisposition.Applied, result.Disposition);
        Assert.Equal(blocked, result.Acknowledged);
    }

    [Fact]
    public async Task RemoveWriteBlockAsync_UsesTheEightCompatibleDynamicSettingReset()
    {
        InMemoryRequestInvoker invoker = CreateInvoker("""{"acknowledged":true}""", statusCode: 200);
        ApiCallDetails? observed = null;
        var settings = new ElasticsearchClientSettings(invoker)
            .DisableDirectStreaming()
            .OnRequestCompleted(details => observed = details);
        ElasticsearchClient client = new(settings);
        ElasticsearchMaterializationTransport transport = new(client);

        var result = await transport.RemoveWriteBlockAsync(
            "generation-a",
            maximumResponseBytes: 1_024,
            CancellationToken.None);

        Assert.Equal(ElasticAcknowledgedDisposition.Applied, result.Disposition);
        Assert.True(result.Acknowledged);
        var call = observed ?? throw new Xunit.Sdk.XunitException("The Elasticsearch request was not observed.");
        Assert.Equal(global::Elastic.Transport.HttpMethod.PUT, call.HttpMethod);
        Assert.Equal("/generation-a/_settings", call.Uri?.AbsolutePath);
        Assert.Equal("{\"index.blocks.write\":null}", Encoding.UTF8.GetString(call.RequestBodyInBytes!));
    }

    [Fact]
    public async Task ScanAsync_RejectsAFirstHitThatDoesNotAdvanceThePageToken()
    {
        var transport = CreateTransport(
            """
            {
              "took": 1,
              "timed_out": false,
              "_shards": { "total": 1, "successful": 1, "failed": 0 },
              "hits": {
                "hits": [
                  { "_id": "item-a", "_source": { "value": 1 }, "sort": ["item-a"] }
                ]
              }
            }
            """);
        ElasticScanRequest request = new(
            "generation-a",
            query: default,
            sortField: "_cohesive.itemId",
            afterSortValue: "item-a",
            maximumItems: 10,
            maximumResponseBytes: 4_096);

        var exception = await Assert.ThrowsAsync<ElasticMaterializationTransportException>(async () =>
            await transport.ScanAsync(request, CancellationToken.None));

        Assert.Equal("cohesive.elasticsearch.protocol", exception.ErrorType);
    }

    [Fact]
    public async Task ResponseByteBound_FailsClosedBeforeParsing()
    {
        var transport = CreateTransport(
            """{"_index":"control","_id":"document","found":false}""",
            statusCode: 404);

        var exception = await Assert.ThrowsAsync<ElasticMaterializationTransportException>(async () =>
            await transport.GetDocumentAsync(
                "control",
                "document",
                maximumResponseBytes: 8,
                CancellationToken.None));

        Assert.Equal("cohesive.elasticsearch.response.limitExceeded", exception.ErrorType);
        Assert.Equal(404, exception.StatusCode);
        Assert.Contains("get-control-document", exception.Message, StringComparison.Ordinal);
        Assert.False(exception.Retryable);
        Assert.Null(exception.InnerException);
    }

    static ElasticsearchMaterializationTransport CreateTransport(string response, int statusCode = 200)
    {
        ElasticsearchClient client = new(new ElasticsearchClientSettings(CreateInvoker(response, statusCode)));
        return new(client);
    }

    static InMemoryRequestInvoker CreateInvoker(string response, int statusCode) =>
        new(
            Encoding.UTF8.GetBytes(response),
            statusCode,
            exception: null,
            contentType: "application/json",
            headers: new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Elastic-Product"] = ["Elasticsearch"]
            });
}
