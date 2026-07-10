using Cohesive.AI.Training;

namespace Cohesive.Adapters.AzureStorage;

/// <summary>
/// Writes dataset artifacts directly to Azure Blob Storage.
/// </summary>
public sealed class AzureBlobDatasetOutputStreamProvider(IBlobClientByNameResolver clientResolver) : IDatasetOutputStreamProvider
{
    /// <summary>Opens a writable dataset output target.</summary>
    public async ValueTask<DatasetOutputWriteTarget> OpenWriteAsync(string fileName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ct.ThrowIfCancellationRequested();
        
        var blobClient = await clientResolver.GetBlobClient(blobName: fileName, ct: ct);
        var stream = await blobClient.OpenWriteAsync(overwrite: true, cancellationToken: ct).ConfigureAwait(false);
        return new(stream, location: blobClient.Uri);
    }
}
