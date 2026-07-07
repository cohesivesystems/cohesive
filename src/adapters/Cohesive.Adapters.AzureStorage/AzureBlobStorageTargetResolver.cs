namespace Cohesive.Adapters.AzureStorage;

static class AzureBlobStorageTargetResolver
{
    /// <summary>
    /// Builds a URI for the given account and container name.
    /// </summary>
    /// <param name="accountName"></param>
    /// <param name="containerName"></param>
    /// <returns></returns>
    public static string BuildContainerUri(string accountName, string containerName) =>
        $"https://{AccountHost(accountName)}/{containerName}";
    
    /// <summary>
    /// Parses ({AccountHost},{ContainerName},{BlobName}) from the given storage root URI and file name.
    /// For HTTPs schemes, the account host is extracted from the URI.
    /// Otherwise, the account name is required to be provided at the call-site.
    /// </summary>
    /// <param name="storageRoot">An absolute storage root URI that contains the container name and possibly the account name.</param>
    /// <param name="fileName">The file name turned into the blob name.</param>
    /// <param name="accountName">
    /// The name of the storage account to use for the account host Uri for non HTTPs schemes from <see cref="AzureBlobStorageSchemes"/>.
    /// If null, the account host is extracted from the URI.
    /// </param>
    /// <returns></returns>
    /// <example>
    /// <c>https://{account}.blob.core.windows.net/{container}</c><br />
    /// <c>abfss://{container}@{account}.dfs.core.windows.net</c><br />
    /// <c>azblob://{container}@{account}.blob.core.windows.net</c>
    /// </example>
    /// <exception cref="NotSupportedException">Storage uri must be absolute</exception>
    /// <exception cref="InvalidOperationException"><paramref name="accountName"/> was required but not provided.</exception>
    public static AzureBlobStorageTarget Parse(string storageRoot, string fileName, string? accountName)
    {
        if (!Uri.TryCreate(storageRoot, UriKind.Absolute, out var uri))
            throw new NotSupportedException($"Azure blob output requires an absolute storage root URI.");

        return uri.Scheme.ToLowerInvariant() switch
        {
            AzureBlobStorageSchemes.Abfs   or AzureBlobStorageSchemes.Abfss   => ParseAbfs(uri, fileName: fileName, accountName),
            AzureBlobStorageSchemes.Azblob or AzureBlobStorageSchemes.Azblobs => ParseAzBlob(uri, fileName: fileName, accountName),
            AzureBlobStorageSchemes.Https                                     => ParseHttps(uri, fileName: fileName),
            _ => throw new NotSupportedException($"Storage scheme '{uri.Scheme}' is not supported by the Azure blob storage target resolver.")
        };
    }

    /// <summary>
    /// Parses ({AccountHost},{ContainerName},{BlobName}) from <c>abfss://{container}@{account}.dfs.core.windows.net</c>, or <c>abfss://{containerName}</c> with <paramref name="accountName"/> required.
    /// </summary>
    /// <param name="storageRootUri"></param>
    /// <param name="fileName"></param>
    /// <param name="accountName"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"><paramref name="accountName"/> was required but not provided.</exception>
    static AzureBlobStorageTarget ParseAbfs(Uri storageRootUri, string fileName, string? accountName)
    {
        string containerName;
        string accountHost;
        if (!string.IsNullOrWhiteSpace(storageRootUri.UserInfo))
        {
            containerName = storageRootUri.UserInfo;
            accountHost = NormalizeBlobHost(storageRootUri.Host);
        }
        else
        {
            containerName = storageRootUri.Host;
            if (string.IsNullOrWhiteSpace(accountName))
                throw new InvalidOperationException("Azure blob output requires a configured storage account name when the abfs root omits an account host.");
            accountHost = AccountHost(accountName);
        }
        return CreateTarget(accountHost: accountHost, containerName: containerName, prefixOrAbsolutePath: storageRootUri.AbsolutePath, fileName: fileName);
    }

    /// <summary>
    /// Parses ({AccountHost},{ContainerName},{BlobName}) from <c>https://{account}.blob.core.windows.net/{container}</c>
    /// </summary>
    /// <param name="storageRootUri"></param>
    /// <param name="fileName"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    static AzureBlobStorageTarget ParseHttps(Uri storageRootUri, string fileName)
    {
        var segments = storageRootUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidOperationException($"Azure blob output root '{storageRootUri}' does not include a container name.");
        var containerName = segments[0];
        var prefix = string.Join('/', segments.Skip(1));
        var accountHost = NormalizeBlobHost(storageRootUri.Host);
        return CreateTarget(accountHost: accountHost, containerName: containerName, prefixOrAbsolutePath: prefix, fileName: fileName);
    }
    
    /// <summary>
    /// Parses <c>azblob://{container}@{account}.blob.core.windows.net</c> or <c>azblob://{containerName}.blob.core.windows.net</c> with <paramref name="accountName"/> required.
    /// </summary>
    /// <param name="uri"></param>
    /// <param name="fileName"></param>
    /// <param name="accountName"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"><paramref name="accountName"/> was required but not provided.</exception>
    static AzureBlobStorageTarget ParseAzBlob(Uri uri, string fileName, string? accountName)
    {
        string containerName;
        string accountHost;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            containerName = uri.UserInfo;
            accountHost = NormalizeBlobHost(uri.Host);
        }
        else
        {
            containerName = uri.Host;
            if (string.IsNullOrWhiteSpace(accountName))
                throw new InvalidOperationException("Azure blob output requires a configured storage account name when the azblob root omits an account host.");
            accountHost = AccountHost(accountName);
        }
        return CreateTarget(accountHost: accountHost, containerName: containerName, prefixOrAbsolutePath: uri.AbsolutePath, fileName: fileName);
    }

    static AzureBlobStorageTarget CreateTarget(string accountHost, string containerName, string prefixOrAbsolutePath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            throw new InvalidOperationException("Azure blob output requires a non-empty container name.");

        return new(
            AccountHost: accountHost,
            ContainerName: containerName,
            BlobName: Uri.CombineSegments(prefixOrAbsolutePath, fileName)
            );
    }

    internal static string AccountHost(string accountName) => 
        $"{accountName}.blob.core.windows.net";
    
    internal static string NormalizeBlobHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Azure blob output requires a non-empty storage account host.");
        return host.Replace(".dfs.core.", ".blob.core.", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A component-wise reference to an Azure blob.
/// </summary>
/// <param name="AccountHost">The account host (e.g., {account}.blob.core.windows.net</param>
/// <param name="ContainerName">The name of the blob container.</param>
/// <param name="BlobName">The name of the blob within the container.</param>
readonly record struct AzureBlobStorageTarget(
    string AccountHost,
    string ContainerName,
    string BlobName
    );
