using Azure.Storage.Blobs;

namespace Cohesive.Adapters.AzureStorage;

/// <summary>
/// Extension methods for <see cref="BlobServiceClient"/>.
/// </summary>
public static class BlobServiceClientExtensions
{
    /// <summary>
    /// Gets a blob client resolver for the given container name.
    /// </summary>
    /// <param name="client">The blob service client.</param>
    /// <param name="containerName">The container name fixed in the blob client resolver.</param>
    /// <param name="blobPrefix">Optional blob name prefix applied to every resolved blob name.</param>
    /// <param name="createIfNotExists">Creates the container if it does not exist upon resolution.</param>
    /// <returns></returns>
    public static IBlobClientByNameResolver GetBlobClientByNameResolver(this BlobServiceClient client, string containerName, string? blobPrefix = null, bool createIfNotExists = true) =>
        new BlobClientByNameResolver(client, containerName: containerName, blobPrefix: blobPrefix, createIfNotExists: createIfNotExists);

    class BlobClientByNameResolver(BlobServiceClient client, string containerName, string? blobPrefix, bool createIfNotExists) : IBlobClientByNameResolver
    {
        public async Task<BlobClient> GetBlobClient(string blobName, CancellationToken ct = default)
        {
            var containerClient = client.GetBlobContainerClient(blobContainerName: containerName);
            if (createIfNotExists)
                await containerClient.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            return containerClient.GetBlobClient(blobName: Uri.CombineSegments(blobPrefix, blobName));
        }
    }
    
    /// <summary>
    /// Gets a blob client for the given container and blob name, creating the container if it does not exist.
    /// </summary>
    /// <param name="client">The client to a storage account.</param>
    /// <param name="containerName">The blob container name.</param>
    /// <param name="blobName">The blob name.</param>
    /// <param name="createIfNotExists">Indicates whether to create the blob if it does not exist.</param>
    /// <param name="ct"></param>
    /// <returns>The blob client for the specified container and blob.</returns>
    public static async Task<BlobClient> GetBlobClient(this BlobServiceClient client, string containerName, string blobName, bool createIfNotExists = true, CancellationToken ct = default)
    {
        var containerClient = client.GetBlobContainerClient(blobContainerName: containerName);
        if (createIfNotExists)
            await containerClient.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        return containerClient.GetBlobClient(blobName: blobName);
    }
}
