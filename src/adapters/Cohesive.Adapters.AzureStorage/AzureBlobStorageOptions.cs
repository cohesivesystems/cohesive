using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace Cohesive.Adapters.AzureStorage;

/// <summary>
/// Azure Blob Storage account connection settings.
/// </summary>
public sealed record AzureBlobStorageOptions
{
    /// <summary>
    /// The default storage account name used when only one storage account is configured.
    /// </summary>
    public const string DefaultName = "Default";
    
    /// <summary>
    /// The azure blob storage connection string.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// The azure blob storage account name.
    /// </summary>
    public string? AccountName { get; init; }
    
    /// <summary>
    /// Indicates whether the storage options are configured.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ConnectionString)
        || !string.IsNullOrWhiteSpace(AccountName);

    public string? GetAccountName() => AccountName;
    
    string? GetAccountHost() =>
        string.IsNullOrWhiteSpace(AccountName)
            ? null
            : AzureBlobStorageTargetResolver.AccountHost(AccountName);
    
    public Uri GetServiceUri()
    {
        if (!string.IsNullOrEmpty(ConnectionString))
            throw new InvalidOperationException("Connection-string-backed Azure Blob storage settings do not expose a service URI.");

        var accountHost = GetAccountHost();
        if (!string.IsNullOrWhiteSpace(accountHost))
            return new($"https://{accountHost}", UriKind.Absolute);

        throw new InvalidOperationException("Neither AccountName nor ConnectionString is provided for AzureBlobStorageOptions.");
    }

    public TokenCredential? TryCreateTokenCredential()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
            return null;

        if (GetAccountHost() is not null)
            return new DefaultAzureCredential();
        
        return null;
    }
}

/// <summary>
/// Azure Blob Storage container settings backed by a configured storage account.
/// </summary>
public sealed record AzureBlobContainerOptions
{
    /// <summary>
    /// The default container profile name used when one container profile is configured.
    /// </summary>
    public const string DefaultName = AzureBlobStorageOptions.DefaultName;

    /// <summary>
    /// The storage account profile used to access this container.
    /// </summary>
    public string? AzureBlobStorageName { get; init; } = AzureBlobStorageOptions.DefaultName;

    /// <summary>
    /// The blob container name.
    /// </summary>
    public string? ContainerName { get; init; }

    /// <summary>
    /// Optional storage root URI for this container and optional prefix, e.g. <c>https://{account}.blob.core.windows.net/{container}/{prefix}</c>.
    /// This is a container location, not an account connection.
    /// </summary>
    public string? StorageRoot { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ContainerName)
        || !string.IsNullOrWhiteSpace(StorageRoot);

    public string GetRequiredContainerName()
    {
        if (!string.IsNullOrWhiteSpace(ContainerName))
            return ContainerName;

        if (!string.IsNullOrWhiteSpace(StorageRoot))
            return AzureBlobStorageTargetResolver.Parse(storageRoot: StorageRoot, fileName: "", accountName: AzureBlobStorageOptions.DefaultName).ContainerName;

        throw new InvalidOperationException("Azure blob container profile requires a configured container name or storage root.");
    }

    public string? TryGetBlobPrefix()
    {
        if (string.IsNullOrWhiteSpace(StorageRoot))
            return null;

        var target = AzureBlobStorageTargetResolver.Parse(storageRoot: StorageRoot, fileName: "", accountName: AzureBlobStorageOptions.DefaultName);
        return string.IsNullOrWhiteSpace(target.BlobName) ? null : target.BlobName;
    }

    public string GetStorageRoot(AzureBlobStorageOptions account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!string.IsNullOrWhiteSpace(StorageRoot))
            return StorageRoot;

        var accountName = account.GetAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
            throw new InvalidOperationException("Azure blob container storage root requires an account name when StorageRoot is not configured.");

        return AzureBlobStorageTargetResolver.BuildContainerUri(accountName: accountName, containerName: GetRequiredContainerName());
    }
}

public static class AzureBlobStorageOptionsExtensions
{
    static string? NormalizeAzureBlobStorageName(string? name) =>
        IsDefaultAzureBlobStorageName(name) ? AzureBlobStorageOptions.DefaultName : name;

    static bool IsDefaultAzureBlobStorageName(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || string.Equals(name, AzureBlobStorageOptions.DefaultName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, Options.DefaultName, StringComparison.Ordinal);
    
    /// <summary>
    /// Gets the Azure Blob storage options with the given name.
    /// </summary>
    /// <param name="optionsByName">The keyed collection of storage options to scan.</param>
    /// <param name="name">The name/label/key of the storage options to return.</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">Unable to find options with the given name</exception>
    public static KeyValuePair<string, AzureBlobStorageOptions> GetRequiredOptions(this IReadOnlyDictionary<string, AzureBlobStorageOptions>? optionsByName, string? name)
    {
        var normalizedName = NormalizeAzureBlobStorageName(name)
            ?? throw new InvalidOperationException("Azure blob storage name must be configured.");
        
        if (optionsByName is not null && optionsByName.TryGetValue(normalizedName, out var options) && options.IsConfigured)
        {
            return new(normalizedName, options);
        }

        throw new InvalidOperationException($"Azure blob storage '{normalizedName}' is not configured.");
    }

    static string? NormalizeAzureBlobContainerName(string? name) =>
        IsDefaultAzureBlobStorageName(name) ? AzureBlobContainerOptions.DefaultName : name;

    /// <summary>
    /// Gets the Azure Blob container options with the given name.
    /// </summary>
    public static KeyValuePair<string, AzureBlobContainerOptions> GetRequiredOptions(this IReadOnlyDictionary<string, AzureBlobContainerOptions>? optionsByName, string? name)
    {
        var normalizedName = NormalizeAzureBlobContainerName(name) ?? throw new InvalidOperationException("Azure blob container name must be configured.");
        if (optionsByName is not null && optionsByName.TryGetValue(normalizedName, out var options) && options.IsConfigured)
        {
            return new(normalizedName, options);
        }

        throw new InvalidOperationException($"Azure blob container '{normalizedName}' is not configured.");
    }
}
