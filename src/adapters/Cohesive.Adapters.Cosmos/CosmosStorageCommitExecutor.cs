using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Storage.Commits;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Realizes portable conditional commits as one container/partition transactional batch.</summary>
/// <remarks>
/// Owns a dedicated document namespace, not arbitrary existing repository documents. Configuration is verified
/// during creation; re-create after topology/consistency changes and prohibit TTL or out-of-band receipt deletion.
/// Uses stream operations and its own tagged portable wire profile, independently of the client's serializer.
/// Each expected logical token is translated to a freshly read native ETag and fenced inside the batch.
/// </remarks>
public sealed class CosmosStorageCommitExecutor : IStorageCommitExecutor
{
    /// <summary>Conservative serialized document budget, leaving room below the service's batch wire limit.</summary>
    public const int MaxSerializedDocumentBytes = 1024 * 1024;
    const string ItemKind = "item";
    const string ReceiptKind = "receipt";
    static readonly JsonSerializerOptions Json = StorageCommitJson.CreateOptions();
    readonly Container container;

    CosmosStorageCommitExecutor(Container container, StorageCommitCapabilities capabilities)
    {
        this.container = container;
        Capabilities = capabilities;
    }

    /// <summary>Verifies the native placement, retention and account write topology before returning an executor.</summary>
    /// <param name="client">Borrowed live Cosmos client; ownership remains with the caller.</param>
    /// <param name="databaseId">Existing database identity.</param>
    /// <param name="containerId">Existing dedicated container with /partitionKey partitioning, TTL disabled and no additional unique keys.</param>
    /// <param name="target">Logical target bound to this container.</param>
    /// <param name="cancellationToken">Cancellation checked during profile acquisition.</param>
    /// <returns>An executor with observed capability evidence. Query guards require a Strong account profile.</returns>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    /// <exception cref="ArgumentException">An identity is empty.</exception>
    /// <exception cref="NotSupportedException">Placement, retention or write topology cannot preserve this profile.</exception>
    /// <exception cref="CosmosException">Account or container metadata cannot be acquired.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public static async Task<CosmosStorageCommitExecutor> CreateAsync(CosmosClient client, string databaseId,
        string containerId, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        cancellationToken.ThrowIfCancellationRequested();
        var account = await client.ReadAccountAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (account.WritableRegions.Count() != 1)
            throw new NotSupportedException("Storage commits require exactly one Cosmos writable region.");
        var container = client.GetContainer(databaseId, containerId);
        var properties = (await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Resource;
        if (properties.PartitionKeyPath != "/partitionKey" || properties.PartitionKeyPaths.Count != 1
            || properties.DefaultTimeToLive is not null || properties.UniqueKeyPolicy.UniqueKeys.Count != 0)
            throw new NotSupportedException("Storage commits require /partitionKey partitioning, disabled TTL and no additional unique keys.");
        return new(container, new(Target: target, SupportsMultiplePartitions: false,
            SupportsQueryGuards: account.Consistency.DefaultConsistencyLevel == ConsistencyLevel.Strong, MaxAtomicItems: 100, MaxSerializedPayloadBytes: MaxSerializedDocumentBytes,
            MaxPartitionKeyUtf8Bytes: properties.PartitionKeyDefinitionVersion == PartitionKeyDefinitionVersion.V2 ? 2048 : 101));
    }

    /// <inheritdoc />
    public StorageCommitCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async ValueTask<StorageCommitResult> CommitAsync(OperationContext context, StorageCommitIntent intent)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Capabilities.Validate(intent) is { } unsupported) return unsupported;
        var cancellation = context.CancellationToken;
        cancellation.ThrowIfCancellationRequested();
        var receipt = new StorageCommitReceipt(intent.Reference, intent.Result);
        // Bound all serialized payloads before reads or mutation submission. The service also enforces its wire limit.
        List<MemoryStream> payloads = new(intent.Writes.Length + 1);
        try
        {
            var totalBytes = 0L;
            foreach (var write in intent.Writes)
            {
                var payload = Encode(write.Address, ItemKind, write.Value, intent.Fingerprint);
                payloads.Add(payload);
                totalBytes += payload.Length;
                if (totalBytes > MaxSerializedDocumentBytes) return PayloadLimit();
            }
            var receiptPayload = Encode(intent.ReceiptAddress, ReceiptKind, receipt, intent.Fingerprint);
            payloads.Add(receiptPayload);
            if (totalBytes + receiptPayload.Length > MaxSerializedDocumentBytes) return PayloadLimit();

            var replay = await ReconcileAsync(context, intent.Reference).ConfigureAwait(false);
            if (replay.Disposition != StorageCommitDisposition.Unknown) return replay;
            var batch = container.CreateTransactionalBatch(new PartitionKey(intent.ReceiptAddress.Partition));
            for (var index = 0; index < intent.Writes.Length; index++)
            {
                var write = intent.Writes[index];
                if (write.ExpectedToken is not { } expected)
                {
                    batch.CreateItemStream(payloads[index]);
                    continue;
                }
                var current = await ReadDocument(write.Address, ItemKind, cancellation).ConfigureAwait(false);
                if (current is null || current.Token != expected.Value)
                    return await ReconcileOrPrecondition(context, intent.Reference, $"/writes/{index}").ConfigureAwait(false);
                batch.ReplaceItemStream(DocumentId(write.Address, ItemKind), payloads[index],
                    new TransactionalBatchItemRequestOptions { IfMatchEtag = current.ETag });
            }
            batch.CreateItemStream(receiptPayload);
            cancellation.ThrowIfCancellationRequested();
            using var response = await batch.ExecuteAsync(cancellation).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return StorageCommitResult.Success(receipt, StorageCommitDisposition.Committed);
            if ((response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                && (response.Count != intent.Writes.Length + 1 || response[intent.Writes.Length].StatusCode == HttpStatusCode.Conflict))
                // A receipt collision or missing per-item evidence does not establish an item failure. An eventually
                // consistent receipt miss remains Unknown until its retained content can be compared.
                return await ReconcileAsync(context, intent.Reference).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                return await ReconcileOrPrecondition(context, intent.Reference, "/writes").ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge) return PayloadLimit();
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                return StorageCommitResult.Unknown();
            throw new CosmosException("Storage commit batch failed.", response.StatusCode, subStatusCode: 0,
                response.ActivityId, response.RequestCharge);
        }
        finally
        {
            foreach (var payload in payloads) payload.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask<StorageCommitResult> ReconcileAsync(OperationContext context, StorageCommitReference commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        if (Capabilities.ValidateAddress(commit.Address) is { } unsupported) return unsupported;
        var document = await ReadDocument(commit.Address, ReceiptKind, context.CancellationToken).ConfigureAwait(false);
        if (document is null) return StorageCommitResult.Unknown();
        var receipt = document.Payload.Deserialize<StorageCommitReceipt>(Json)
            ?? throw new JsonException("A durable receipt cannot be null.");
        return receipt.Reconcile(commit);
    }

    /// <summary>Reads committed portable item state and its logical conditional-write token.</summary>
    /// <param name="context">Cancellation and operation context.</param>
    /// <param name="address">Item in the configured logical target.</param>
    /// <returns>A detached immutable item, or null if no item is visible.</returns>
    /// <exception cref="ArgumentException">The target is not bound or the partition exceeds its byte limit.</exception>
    /// <exception cref="CosmosException">The native read fails.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed during the read.</exception>
    public async ValueTask<StorageCommitItem?> ReadAsync(OperationContext context, StorageCommitAddress address)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(address);
        if (Capabilities.ValidateAddress(address) is { } unsupported)
            throw new ArgumentException(unsupported.Diagnostics[0].Message, nameof(address));
        var document = await ReadDocument(address, ItemKind, context.CancellationToken).ConfigureAwait(false);
        return document is null ? null : new(document.Payload.Deserialize<PortableValue>(Json)
            ?? throw new JsonException("A durable item cannot be null."), new(document.Token));
    }

    async ValueTask<StorageCommitResult> ReconcileOrPrecondition(OperationContext context, StorageCommitReference reference, string location)
    {
        var reconciliation = await ReconcileAsync(context, reference).ConfigureAwait(false);
        return reconciliation.Disposition != StorageCommitDisposition.Unknown ? reconciliation
            : StorageCommitResult.PreconditionFailed(location);
    }

    async Task<ReadDocumentResult?> ReadDocument(StorageCommitAddress address, string kind, CancellationToken cancellation)
    {
        using var response = await container.ReadItemStreamAsync(DocumentId(address, kind), new PartitionKey(address.Partition),
            requestOptions: Capabilities.SupportsQueryGuards ? new ItemRequestOptions { ConsistencyLevel = ConsistencyLevel.Strong } : null,
            cancellationToken: cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellation).ConfigureAwait(false);
        var root = document.RootElement;
        return new(root.GetProperty("payload").Clone(), root.GetProperty("token").GetString()!, response.Headers.ETag);
    }

    static MemoryStream Encode<T>(StorageCommitAddress address, string kind, T payload, string token) =>
        new(JsonSerializer.SerializeToUtf8Bytes(new CommitDocument<T>(DocumentId(address, kind), address.Partition, address, payload, token), Json), writable: false);

    static string DocumentId(StorageCommitAddress address, string kind) => "cohesive-commit-v1-" + kind + "-"
        + Convert.ToHexStringLower(SHA256.HashData(StrictDocumentJson.GetCanonicalBytes(address, Json)));

    static StorageCommitResult PayloadLimit() => StorageCommitResult.Rejected(StorageCommitDisposition.Unsupported,
        "storage.commit.payload-limit", $"The bounded Cosmos profile supports at most {MaxSerializedDocumentBytes} serialized document bytes including the receipt.");

    sealed record CommitDocument<T>(string Id, string PartitionKey, StorageCommitAddress Address, T Payload, string Token);
    sealed record ReadDocumentResult(JsonElement Payload, string Token, string ETag);
}
