using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Azure.Qualification;

/// <summary>Explicit, create-only native write/read/delete probes. No resource provisioning, background work or retries.</summary>
public static class StorageRuntimeQualification
{
    /// <summary>Creates a tiny block blob with If-None-Match, verifies its original ETag/content, then deletes with If-Match.</summary>
    /// <remarks>The caller owns the native client, target review and SDK retry policy. Use a reviewed unused prefix;
    /// versioning/soft delete can retain deleted data. No existing blob or snapshot is deleted. A lost create response
    /// leaves cleanup unresolved; the caller must inspect the exact generated name rather than retry the run ID.</remarks>
    public static Task<RuntimeQualificationResult> BlobAsync(BlobContainerClient container, RuntimeQualificationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(options);
        var blob = container.GetBlobClient(options.ObjectName);
        var bytes = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"));
        return StorageQualification.RunAsync(options, async ct =>
        {
            try
            {
                var created = await blob.UploadAsync(new BinaryData(bytes), new BlobUploadOptions
                { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }, HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" } }, ct).ConfigureAwait(false);
                var receipt = created.Value.ETag.ToString();
                return !string.IsNullOrWhiteSpace(receipt) ? receipt : throw new InvalidOperationException();
            }
            catch (RequestFailedException e) when (e.Status == 412 || (e.Status == 409 && e.ErrorCode == "BlobAlreadyExists")) { return null; }
        }, async (etag, ct) =>
        {
            var read = await blob.DownloadContentAsync(new BlobDownloadOptions { Conditions = new BlobRequestConditions { IfMatch = new ETag(etag) } }, ct).ConfigureAwait(false);
            return read.Value.Details.ETag.ToString() == etag && read.Value.Content.ToMemory().Span.SequenceEqual(bytes);
        }, async (etag, ct) =>
        {
            await blob.DeleteAsync(DeleteSnapshotsOption.None, new BlobRequestConditions { IfMatch = new ETag(etag) }, ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Creates one synthetic item in an explicitly selected container using its own partition, reads it, and conditionally deletes it.</summary>
    /// <remarks>Requires the native partition path /partitionKey. The caller must review change-feed/index/consumer effects;
    /// a synthetic document must never be sent to a product inbox. No upsert, TTL mutation, query or container creation occurs.</remarks>
    public static async Task<RuntimeQualificationResult> CosmosAsync(Container container, RuntimeQualificationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(options);
        if (cancellationToken.IsCancellationRequested)
            return new(options.RunId, options.ObjectName, QualificationOutcome.NotStarted, QualificationCleanup.NotRequired, "qualification.canceled");
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        admission.CancelAfter(options.OperationTimeout);
        try
        {
            var properties = await container.ReadContainerAsync(cancellationToken: admission.Token).ConfigureAwait(false);
            if (properties.Resource.PartitionKeyPath != "/partitionKey") throw new InvalidOperationException();
        }
        catch (Exception)
        {
            return new(options.RunId, options.ObjectName, QualificationOutcome.NotStarted, QualificationCleanup.NotRequired, "qualification.partitionAdmissionFailed");
        }
        var challenge = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id = options.ObjectName, partitionKey = options.ObjectName, qualification = challenge });
        var key = new PartitionKey(options.ObjectName);
        return await StorageQualification.RunAsync(options, async ct =>
        {
            using var content = new MemoryStream(payload, writable: false);
            using var created = await container.CreateItemStreamAsync(content, key, new ItemRequestOptions { EnableContentResponseOnWrite = false }, ct).ConfigureAwait(false);
            if (created.StatusCode == HttpStatusCode.Conflict) return null;
            created.EnsureSuccessStatusCode();
            return created.Headers.ETag ?? throw new InvalidOperationException();
        }, async (etag, ct) =>
        {
            using var read = await container.ReadItemStreamAsync(options.ObjectName, key, cancellationToken: ct).ConfigureAwait(false);
            read.EnsureSuccessStatusCode();
            if (read.Headers.ETag != etag) return false;
            using var content = await JsonDocument.ParseAsync(read.Content, cancellationToken: ct).ConfigureAwait(false);
            return content.RootElement.GetProperty("id").GetString() == options.ObjectName
                && content.RootElement.GetProperty("partitionKey").GetString() == options.ObjectName
                && content.RootElement.GetProperty("qualification").GetString() == challenge;
        }, async (etag, ct) =>
        {
            using var deleted = await container.DeleteItemStreamAsync(options.ObjectName, key, new ItemRequestOptions { IfMatchEtag = etag }, ct).ConfigureAwait(false);
            deleted.EnsureSuccessStatusCode();
        }, admission.Token).ConfigureAwait(false);
    }
}
