using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Elastic;

namespace Cohesive.Tests.Elastic;

public sealed class FakeElasticMaterializationTransportTests
{
    [Fact]
    public async Task AliasExchange_MissingExpectedPriorConflictsBeforeRemovingStrayOwner()
    {
        FakeElasticMaterializationTransport transport = new();
        await CreateIndexAsync(
            transport,
            ".cohesive-control",
            "{\"aliases\":{\".cohesive-marker-4\":{\"is_hidden\":true}}}"u8.ToArray());
        await CreateIndexAsync(transport, "generation-expected", "{}"u8.ToArray());
        await CreateIndexAsync(transport, "generation-next", "{}"u8.ToArray());
        await CreateIndexAsync(
            transport,
            ".stray-hidden",
            "{\"settings\":{\"index.hidden\":true},\"aliases\":{\"loads-read\":{}}}"u8.ToArray());
        ElasticAliasCasRequest request = new(
            markerIndex: ".cohesive-control",
            expectedMarkerAlias: ".cohesive-marker-4",
            nextMarkerAlias: ".cohesive-marker-5",
            readAlias: "loads-read",
            expectedReadIndex: "generation-expected",
            nextReadIndex: "generation-next",
            maximumResponseBytes: 4_096,
            readAliasFilter: JsonObject("{\"term\":{\"_cohesive.deleted\":false}}"u8.ToArray()),
            isWriteIndex: false);

        var result = await transport.CompareExchangeAliasAsync(request, CancellationToken.None);

        Assert.Equal(ElasticAliasCasDisposition.Conflict, result.Disposition);
        var readOwner = Assert.Single((await transport.InspectAliasesAsync(
            ["loads-read"],
            maximumResponseBytes: 4_096,
            CancellationToken.None)).Bindings);
        Assert.Equal(".stray-hidden", readOwner.Index);
        var marker = Assert.Single((await transport.InspectAliasesAsync(
            [".cohesive-marker-4", ".cohesive-marker-5"],
            maximumResponseBytes: 4_096,
            CancellationToken.None)).Bindings);
        Assert.Equal(".cohesive-marker-4", marker.Alias);
    }

    [Fact]
    public async Task OwnedIndexDeletion_IsAtomicAndRefusesAReplacementWithoutTheOwnerAlias()
    {
        FakeElasticMaterializationTransport transport = new();
        await CreateIndexAsync(
            transport,
            "generation-owned",
            "{\"aliases\":{\".cohesive-owner\":{\"is_hidden\":true}}}"u8.ToArray());

        var deleted = await transport.DeleteOwnedIndexAsync(
            "generation-owned",
            ".cohesive-owner",
            maximumResponseBytes: 4_096,
            CancellationToken.None);
        await CreateIndexAsync(transport, "generation-owned", "{}"u8.ToArray());
        var refused = await transport.DeleteOwnedIndexAsync(
            "generation-owned",
            ".cohesive-owner",
            maximumResponseBytes: 4_096,
            CancellationToken.None);

        Assert.Equal(ElasticOwnedIndexDeleteDisposition.Applied, deleted.Disposition);
        Assert.True(deleted.Acknowledged);
        Assert.Equal(ElasticOwnedIndexDeleteDisposition.OwnershipConflict, refused.Disposition);
        Assert.True(await transport.IndexExistsAsync(
            "generation-owned",
            maximumResponseBytes: 4_096,
            CancellationToken.None));
    }

    [Fact]
    public async Task MetadataMultiGet_BoundsRecoveryIndependentlyOfHistoricalValueSize()
    {
        FakeElasticMaterializationTransport transport = new();
        await CreateIndexAsync(transport, "generation-items", "{}"u8.ToArray());
        var source = JsonSerializer.SerializeToUtf8Bytes(new
        {
            _cohesive = new
            {
                generationId = "generation/1",
                itemId = "item/1",
                mutationId = "mutation/1",
                mutationFingerprint = new string('a', 64),
                version = 1,
                deleted = false
            },
            value = new string('x', 10_000)
        });
        _ = await transport.CreateDocumentAsync(
            "generation-items",
            "item-1",
            JsonObject(source),
            maximumResponseBytes: 4_096,
            CancellationToken.None);

        await Assert.ThrowsAsync<ElasticMaterializationTransportException>(async () =>
            await transport.MultiGetAsync(
                "generation-items",
                ["item-1"],
                ElasticMultiGetSourceProjection.Full,
                maximumResponseBytes: 512,
                CancellationToken.None));
        var projected = Assert.Single((await transport.MultiGetAsync(
            "generation-items",
            ["item-1"],
            ElasticMultiGetSourceProjection.MaterializationMetadata,
            maximumResponseBytes: 512,
            CancellationToken.None)).Documents);

        using var projectedSource = JsonDocument.Parse(projected.Source);
        Assert.True(projectedSource.RootElement.TryGetProperty("_cohesive", out _));
        Assert.False(projectedSource.RootElement.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task BulkAsync_PreservesOwnedSourceBytesWhenCapturingRequests()
    {
        FakeElasticMaterializationTransport transport = new();
        await CreateIndexAsync(transport, "generation-items", "{}"u8.ToArray());
        var source = JsonObject(Encoding.UTF8.GetBytes("""{"escaped" : "\/", "number" : 1.00}"""));

        var result = await transport.BulkAsync(
            [new(ElasticBulkOperationKind.Index, "generation-items", "item-1", externalVersion: 1, source)],
            maximumWireBytes: 4_096,
            maximumResponseBytes: 4_096,
            CancellationToken.None);

        var captured = Assert.Single(Assert.Single(transport.BulkRequests));
        Assert.Equal(source.ToArray(), captured.Source!.ToArray());
        const string expectedAction =
            "{\"index\":{\"_index\":\"generation-items\",\"_id\":\"item-1\",\"version\":1,\"version_type\":\"external\"}}\n";
        Assert.Equal(
            Encoding.UTF8.GetByteCount(expectedAction) + source.Length + 1L,
            result.WireBytes);
    }

    static async Task CreateIndexAsync(
        FakeElasticMaterializationTransport transport,
        string index,
        ReadOnlyMemory<byte> body)
    {
        var result = await transport.CreateIndexAsync(
            index,
            JsonObject(body),
            maximumResponseBytes: 4_096,
            CancellationToken.None);
        Assert.Equal(ElasticIndexCreateDisposition.Created, result.Disposition);
        Assert.True(result.Acknowledged);
    }

    static ElasticJsonObject JsonObject(ReadOnlyMemory<byte> value) =>
        ElasticJsonObject.Parse(value, nameof(value));
}
