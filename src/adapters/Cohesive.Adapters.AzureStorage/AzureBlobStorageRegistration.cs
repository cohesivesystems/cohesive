using Azure.Storage.Blobs;
using Cohesive.AI.Training;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.AzureStorage;

/// <summary>
/// Registration helpers for the Azure Blob Storage system.
/// </summary>
public static class AzureBlobStorageRegistration
{
    /// <param name="services">The service collection to register into.</param>
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers named <see cref="BlobServiceClient"/> instances and azure storage options.
        /// </summary>
        /// <param name="settings"></param>
        public void RegisterAzureBlobStorageClients(IEnumerable<KeyValuePair<string, AzureBlobStorageOptions>>? settings)
        {
            settings = settings?.ToArray() ?? [];
            
            services.AddSingleton<IReadOnlyDictionary<string, AzureBlobStorageOptions>>(settings.Where(x => x.Value.IsConfigured).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));
            
            services.AddAzureClients(clientBuilder =>
            {
                foreach (var (name, options) in settings)
                {
                    if (options?.IsConfigured is not true)
                        continue;

                    if (!string.IsNullOrWhiteSpace(options.ConnectionString))
                    {
                        clientBuilder.AddBlobServiceClient(connectionString: options.ConnectionString).WithName(name);
                    }
                    else
                    {
                        var serviceUri = options.GetServiceUri();
                        var blobClientBuilder = clientBuilder.AddBlobServiceClient(serviceUri: serviceUri).WithName(name);
                        if (options.TryCreateTokenCredential() is {} credential)
                            blobClientBuilder.WithCredential(credential);
                    }
                }
            });
        }

        /// <summary>
        /// Registers named Azure Blob container profiles.
        /// </summary>
        public void RegisterAzureBlobContainers(IEnumerable<KeyValuePair<string, AzureBlobContainerOptions>>? settings)
        {
            settings = settings?.ToArray() ?? [];
            services.AddSingleton<IReadOnlyDictionary<string, AzureBlobContainerOptions>>(settings.Where(x => x.Value.IsConfigured).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Registers an <see cref="IDatasetOutputStreamProvider"/> backed by Azure Blob Storage, if configured.
        /// </summary>
        /// <param name="name">The name of the output stream provider to register.</param>
        /// <param name="containerProfileName">The name of the Azure Blob container profile to use for the provider.</param>
        public void RegisterAzureBlobStorageOutputStreamProvider(string name, string? containerProfileName)
        {
            if (string.IsNullOrWhiteSpace(containerProfileName))
                return;
            
            services.AddKeyedSingleton<IDatasetOutputStreamProvider>(
                serviceKey: name, 
                (sp, _) => new AzureBlobDatasetOutputStreamProvider(sp.GetBlobClientByContainerProfileResolver(profile: containerProfileName, createIfNotExists: true))
            );
        }
    }

    /// <param name="sp">The service provider where the Azure blob storage system was registered.</param>
    extension(IServiceProvider sp)
    {
        /// <summary>
        /// Gets the Azure blob storage options with the given name, defaulting to the default storage profile when omitted.
        /// </summary>
        /// <param name="profile">The name of the Azure storage profile.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">Unable to find the Azure storage profile with the given name.</exception>
        public AzureBlobStorageOptions GetRequiredAzureStorageOptions(string? profile) =>
            sp.GetRequiredService<IReadOnlyDictionary<string, AzureBlobStorageOptions>>().GetRequiredOptions(profile).Value;

        /// <summary>
        /// Gets the Azure blob container options with the given profile name.
        /// </summary>
        public AzureBlobContainerOptions GetRequiredAzureContainerOptions(string profile) =>
            sp.GetRequiredService<IReadOnlyDictionary<string, AzureBlobContainerOptions>>().GetValueOrDefault(profile) ?? throw new InvalidOperationException($"Azure blob container profile '{profile}' is not configured.");
        
        /// <summary>
        /// Gets a <see cref="IBlobClientByNameResolver"/> for an Azure Blob Storage container associated with the given container profile name.
        /// </summary>
        /// <param name="profile">The name of the Azure blob container profile.</param>
        /// <param name="createIfNotExists"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">The given Azure blob container profile does not exist or does not have a container name configured.</exception>
        public IBlobClientByNameResolver GetBlobClientByContainerProfileResolver(string profile, bool createIfNotExists = true)
        {
            var containerOptions = sp.GetRequiredAzureContainerOptions(profile: profile);
            var storageProfileName = string.IsNullOrWhiteSpace(containerOptions.AzureBlobStorageName) ? AzureBlobStorageOptions.DefaultName : containerOptions.AzureBlobStorageName;
            return sp.GetRequiredService<IAzureClientFactory<BlobServiceClient>>()
                .CreateClient(name: storageProfileName)
                .GetBlobClientByNameResolver(containerName: containerOptions.GetRequiredContainerName(), blobPrefix: containerOptions.TryGetBlobPrefix(), createIfNotExists: createIfNotExists);
        }
    }
}
