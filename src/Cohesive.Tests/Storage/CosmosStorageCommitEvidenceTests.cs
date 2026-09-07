using System.Net;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Storage.Commits;
using Cohesive.Tests.Storage.Conformance;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Storage;

public sealed class CosmosStorageCommitEvidenceTests
{
    [Theory]
    [InlineData(ConsistencyLevel.Eventual, HttpStatusCode.Conflict)]
    [InlineData(ConsistencyLevel.Eventual, HttpStatusCode.PreconditionFailed)]
    [InlineData(ConsistencyLevel.Session, HttpStatusCode.Conflict)]
    [InlineData(ConsistencyLevel.Session, HttpStatusCode.PreconditionFailed)]
    [InlineData(ConsistencyLevel.Strong, HttpStatusCode.Conflict)]
    [InlineData(ConsistencyLevel.Strong, HttpStatusCode.PreconditionFailed)]
    public async Task ItemFailureRemainsUnknownUntilReceiptIsVisible(ConsistencyLevel consistency, HttpStatusCode failure)
    {
        var intent = StorageCommitConformance.Intent("lost-ack",
            new StorageCommitWrite(StorageCommitConformance.Address("item"), StorageCommitConformance.Value("committed")));
        var retained = new StorageCommitReceipt(intent.Reference, intent.Result);
        var reads = new ScriptedReads(retained);
        using var client = CreateClient(reads);
        var executor = CreateExecutor(client, consistency);
        using var response = new FailedBatch(failure, receiptStatus: HttpStatusCode.FailedDependency);
        var unresolved = await executor.ReconcileBatchFailureAsync(StorageCommitConformance.Context, intent, response);
        Assert.Equal(consistency == ConsistencyLevel.Strong ? StorageCommitDisposition.PreconditionFailed : StorageCommitDisposition.Unknown,
            unresolved.Disposition);
        reads.ReceiptVisible = true;
        var replay = await executor.ReconcileBatchFailureAsync(StorageCommitConformance.Context, intent, response);
        Assert.Equal(StorageCommitDisposition.Replayed, replay.Disposition);
        Assert.Equal(retained, replay.Receipt);
        var different = new StorageCommitIntent(intent.ReceiptAddress, intent.Writes, StorageCommitConformance.Value("different"));
        Assert.Equal(StorageCommitDisposition.IdentityConflict,
            (await executor.ReconcileBatchFailureAsync(StorageCommitConformance.Context, different, response)).Disposition);
        Assert.All(reads.ConsistencyLevels, level => Assert.Equal(consistency == ConsistencyLevel.Strong ? "Strong" : null, level));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleOrMissingItemReadDoesNotTurnInvisibleReceiptIntoPreconditionFailure(bool itemVisible)
    {
        var intent = StorageCommitConformance.Intent("lost-replacement",
            new StorageCommitWrite(StorageCommitConformance.Address("item"), StorageCommitConformance.Value("committed"), new("original-token")));
        var retained = new StorageCommitReceipt(intent.Reference, intent.Result);
        var reads = new ScriptedReads(retained) { ItemVisible = itemVisible };
        using var client = CreateClient(reads);
        var executor = CreateExecutor(client, ConsistencyLevel.Eventual);
        Assert.Equal(StorageCommitDisposition.Unknown, (await executor.CommitAsync(StorageCommitConformance.Context, intent)).Disposition);
        Assert.Equal(3, reads.ConsistencyLevels.Count); // receipt, item, receipt
        reads.ReceiptVisible = true;
        var replay = await executor.CommitAsync(StorageCommitConformance.Context, intent);
        Assert.Equal(StorageCommitDisposition.Replayed, replay.Disposition);
        Assert.Equal(retained, replay.Receipt);
    }

    [Theory]
    [InlineData(ConsistencyLevel.Eventual)]
    [InlineData(ConsistencyLevel.Strong)]
    public async Task ReceiptCollisionWithoutVisibleEvidenceIsAlwaysUnknown(ConsistencyLevel consistency)
    {
        var intent = StorageCommitConformance.Intent("receipt-race",
            new StorageCommitWrite(StorageCommitConformance.Address("item"), StorageCommitConformance.Value("v")));
        using var client = CreateClient(new ScriptedReads(new(intent.Reference, intent.Result)));
        var executor = CreateExecutor(client, consistency);
        using var response = new FailedBatch(HttpStatusCode.Conflict, receiptStatus: HttpStatusCode.Conflict);
        Assert.Equal(StorageCommitDisposition.Unknown,
            (await executor.ReconcileBatchFailureAsync(StorageCommitConformance.Context, intent, response)).Disposition);
    }

    [Fact]
    public async Task FullPreflightIncludesNativeEnvelopesAndReceiptWithoutDatabaseReads()
    {
        var intent = StorageCommitConformance.Intent("sized",
            new StorageCommitWrite(StorageCommitConformance.Address("item"), StorageCommitConformance.Value("v")));
        var reads = new ScriptedReads(new(intent.Reference, intent.Result));
        using var client = CreateClient(reads);
        // A one-byte budget must reject both preflight and execution using the same capability authority.
        var executor = CreateExecutor(client, ConsistencyLevel.Eventual, maxBytes: 1);
        Assert.Equal("storage.commit.payload-limit", executor.Validate(intent)!.Diagnostics[0].Code);
        Assert.Equal("storage.commit.payload-limit", (await executor.CommitAsync(StorageCommitConformance.Context, intent)).Diagnostics[0].Code);
        Assert.Empty(reads.ConsistencyLevels);
        var canonicalBytes = Encoding.UTF8.GetByteCount(StorageCommitJson.Serialize(intent));
        Assert.Equal("storage.commit.payload-limit",
            CreateExecutor(client, ConsistencyLevel.Eventual, maxBytes: canonicalBytes).Validate(intent)!.Diagnostics[0].Code);
        var largeReceipt = new StorageCommitIntent(intent.ReceiptAddress, intent.Writes, StorageCommitConformance.Value(new string('x', 4096)));
        Assert.Equal("storage.commit.payload-limit",
            CreateExecutor(client, ConsistencyLevel.Eventual, maxBytes: 2048).Validate(largeReceipt)!.Diagnostics[0].Code);
        Assert.Null(CreateExecutor(client, ConsistencyLevel.Eventual).Validate(intent));
        Assert.Empty(reads.ConsistencyLevels);
    }

    static CosmosStorageCommitExecutor CreateExecutor(CosmosClient client, ConsistencyLevel consistency,
        long maxBytes = CosmosStorageCommitExecutor.MaxSerializedDocumentBytes) =>
        new(client.GetContainer("db", "items"), new(StorageCommitConformance.Target, SupportsMultiplePartitions: false,
            SupportsQueryGuards: consistency == ConsistencyLevel.Strong, MaxAtomicItems: 100,
            MaxSerializedPayloadBytes: maxBytes), consistency);

    static CosmosClient CreateClient(RequestHandler reads) => new("https://localhost:8081/", Convert.ToBase64String(new byte[64]),
        new CosmosClientOptions
        {
            CustomHandlers = { reads }, ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = static () => new HttpClient(new AccountMetadata())
        });

    sealed class AccountMetadata : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"test","writableLocations":[{"name":"local","databaseAccountEndpoint":"https://localhost:8081/"}],
                    "readableLocations":[{"name":"local","databaseAccountEndpoint":"https://localhost:8081/"}],
                    "userConsistencyPolicy":{"defaultConsistencyLevel":"Strong"},"enableMultipleWriteLocations":false}
                    """, Encoding.UTF8, "application/json")
            });
        }
    }

    sealed class ScriptedReads(StorageCommitReceipt receipt) : RequestHandler
    {
        public bool ReceiptVisible { get; set; }
        public bool ItemVisible { get; set; }
        public List<string?> ConsistencyLevels { get; } = [];

        public override Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            // Intercept the SDK pipeline before network I/O. Unexpected mutation requests fail the test.
            Assert.Equal(HttpMethod.Get, request.Method);
            ConsistencyLevels.Add(request.Headers["x-ms-consistency-level"]);
            var isReceipt = request.RequestUri.ToString().Contains("cohesive-commit-v1-receipt-", StringComparison.Ordinal);
            if (!(isReceipt ? ReceiptVisible : ItemVisible)) return Task.FromResult(new ResponseMessage(HttpStatusCode.NotFound));
            var payload = isReceipt
                ? JsonSerializer.SerializeToElement(receipt, StorageCommitJson.CreateOptions())
                : JsonSerializer.SerializeToElement(StorageCommitConformance.Value("later-state"), StorageCommitJson.CreateOptions());
            return Task.FromResult(new ResponseMessage(HttpStatusCode.OK)
            {
                Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { payload, token = "later-token" }))
            });
        }
    }

    sealed class FailedBatch(HttpStatusCode failure, HttpStatusCode receiptStatus) : TransactionalBatchResponse
    {
        public override HttpStatusCode StatusCode => failure;
        public override int Count => 2;
        public override TransactionalBatchOperationResult this[int index] => new FailedOperation(index == 1 ? receiptStatus : failure);
    }

    sealed class FailedOperation(HttpStatusCode status) : TransactionalBatchOperationResult
    {
        public override HttpStatusCode StatusCode => status;
    }
}
