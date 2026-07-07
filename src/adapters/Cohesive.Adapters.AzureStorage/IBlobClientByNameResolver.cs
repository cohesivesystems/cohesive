using Azure.Storage.Blobs;

namespace Cohesive.Adapters.AzureStorage;

/// <summary>
/// Resolves blob clients by name, with a fixed account and container name.
/// </summary>
public interface IBlobClientByNameResolver
{
    /// <summary>
    /// Gets a blob client for the given blob name.
    /// </summary>
    Task<BlobClient> GetBlobClient(string blobName, CancellationToken ct = default);
}