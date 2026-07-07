namespace Cohesive.Adapters.AzureStorage;

/// <summary>
/// Supported Azure Blob Storage schemes.
/// </summary>
public static class AzureBlobStorageSchemes
{
    /// <summary>
    /// <c>abfs://{container}@{account}.dfs.core.windows.net</c>
    /// </summary>
    public const string Abfs = "abfs";
    
    /// <summary>
    /// <c>abfss://{container}@{account}.dfs.core.windows.net</c>
    /// </summary>
    public const string Abfss = "abfss";
    
    /// <summary>
    /// <c>azblob://{container}@{account}.blob.core.windows.net</c> 
    /// </summary>
    public const string Azblob = "azblob";
    
    /// <summary>
    /// <c>azblobs://{container}@{account}.blob.core.windows.net</c>    
    /// </summary>
    public const string Azblobs = "azblobs";
    
    /// <summary>
    /// <c>https://{account}.blob.core.windows.net/{container}</c>
    /// <br />
    /// <c>https://{account}.dfs.core.windows.net/{container}</c>
    /// </summary>
    public const string Https = "https";
}