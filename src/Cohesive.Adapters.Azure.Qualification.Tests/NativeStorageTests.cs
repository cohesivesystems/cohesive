using System.Net;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Cohesive.Adapters.Azure.Qualification;
using Microsoft.Azure.Cosmos;
using Moq;

namespace Cohesive.Adapters.Azure.Qualification.Tests;

public class NativeStorageTests
{
    static RuntimeQualificationOptions Options() => new(Guid.NewGuid(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
    [Fact]
    public async Task Blob_wire_conditions_are_create_only_and_receipt_scoped()
    {
        var options = Options(); var etag = new ETag("original"); BinaryData? payload = null;
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        var blob = new Mock<BlobClient>(MockBehavior.Strict);
        container.Setup(c => c.GetBlobClient(options.ObjectName)).Returns(blob.Object);
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((data, request, _) => { payload = data; Assert.Equal(ETag.All, request.Conditions.IfNoneMatch); })
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobContentInfo(etag, DateTimeOffset.UtcNow, [], null, null, null, 0), Mock.Of<Response>()));
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<BlobDownloadOptions, CancellationToken>((request, _) => Assert.Equal(etag, request.Conditions.IfMatch))
            .ReturnsAsync(() => Response.FromValue(BlobsModelFactory.BlobDownloadResult(payload!, BlobsModelFactory.BlobDownloadDetails(eTag: etag)), Mock.Of<Response>()));
        blob.Setup(b => b.DeleteAsync(DeleteSnapshotsOption.None, It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .Callback<DeleteSnapshotsOption, BlobRequestConditions, CancellationToken>((_, request, _) => Assert.Equal(etag, request.IfMatch))
            .ReturnsAsync(Mock.Of<Response>());
        Assert.True((await StorageRuntimeQualification.BlobAsync(container.Object, options)).Succeeded);
        Assert.Equal(3, blob.Invocations.Count);
    }

    [Theory]
    [InlineData(409)]
    [InlineData(412)]
    public async Task Blob_collision_does_not_download_or_delete(int status)
    {
        var options = Options(); var container = new Mock<BlobContainerClient>(MockBehavior.Strict); var blob = new Mock<BlobClient>(MockBehavior.Strict);
        container.Setup(c => c.GetBlobClient(options.ObjectName)).Returns(blob.Object);
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(status, "private provider body", "BlobAlreadyExists", null));
        var result = await StorageRuntimeQualification.BlobAsync(container.Object, options);
        Assert.Equal(QualificationOutcome.Collision, result.Outcome);
        Assert.Single(blob.Invocations);
    }

    [Fact]
    public async Task Cosmos_uses_create_not_upsert_and_conditionally_deletes_original_version()
    {
        var options = Options(); var container = new Mock<Container>(MockBehavior.Strict); byte[]? payload = null;
        var props = new Mock<ContainerResponse>();props.SetupGet(p=>p.Resource).Returns(new ContainerProperties("probe", "/partitionKey"));
        container.Setup(c=>c.ReadContainerAsync(null,It.IsAny<CancellationToken>())).ReturnsAsync(props.Object);
        container.Setup(c=>c.CreateItemStreamAsync(It.IsAny<Stream>(),new PartitionKey(options.ObjectName),It.IsAny<ItemRequestOptions>(),It.IsAny<CancellationToken>()))
            .Callback<Stream,PartitionKey,ItemRequestOptions,CancellationToken>((stream,_,_,_)=>{using var copy=new MemoryStream();stream.CopyTo(copy);payload=copy.ToArray();})
            .ReturnsAsync(CosmosResponse(HttpStatusCode.Created,"etag"));
        container.Setup(c=>c.ReadItemStreamAsync(options.ObjectName,new PartitionKey(options.ObjectName),null,It.IsAny<CancellationToken>()))
            .ReturnsAsync(()=>CosmosResponse(HttpStatusCode.OK,"etag",payload));
        container.Setup(c=>c.DeleteItemStreamAsync(options.ObjectName,new PartitionKey(options.ObjectName),It.IsAny<ItemRequestOptions>(),It.IsAny<CancellationToken>()))
            .Callback<string,PartitionKey,ItemRequestOptions,CancellationToken>((_,_,request,_)=>Assert.Equal("etag",request.IfMatchEtag))
            .ReturnsAsync(CosmosResponse(HttpStatusCode.NoContent,"etag"));
        Assert.True((await StorageRuntimeQualification.CosmosAsync(container.Object,options)).Succeeded);
        using var document=JsonDocument.Parse(payload!);
        Assert.Equal(options.ObjectName,document.RootElement.GetProperty("id").GetString());
        Assert.Equal(options.ObjectName,document.RootElement.GetProperty("partitionKey").GetString());
        Assert.Equal(4,container.Invocations.Count);
    }

    [Fact]
    public async Task Wrong_partition_policy_prevents_any_item_IO()
    {
        var container=new Mock<Container>(MockBehavior.Strict);var props=new Mock<ContainerResponse>();props.SetupGet(p=>p.Resource).Returns(new ContainerProperties("probe","/tenant"));
        container.Setup(c=>c.ReadContainerAsync(null,It.IsAny<CancellationToken>())).ReturnsAsync(props.Object);
        var result=await StorageRuntimeQualification.CosmosAsync(container.Object,Options());
        Assert.Equal(QualificationOutcome.NotStarted,result.Outcome);Assert.Single(container.Invocations);
    }
    static ResponseMessage CosmosResponse(HttpStatusCode status,string etag,byte[]? content=null)
    {
        var response=new ResponseMessage(status);response.Headers.Add("etag",etag);
        if(content is not null)response.Content=new MemoryStream(content);return response;
    }
}
