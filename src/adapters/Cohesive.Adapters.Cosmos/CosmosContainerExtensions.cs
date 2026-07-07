using System.Net;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Extensions for <see cref="Container"/>.
/// </summary>
public static class CosmosContainerExtensions
{
    extension(Container container)
    {
        /// <summary>
        /// Reads an item from the container and handles a HttpStatusCode.NotFound exception as a null response.
        /// </summary>
        /// <param name="id">The Cosmos item id</param>
        /// <param name="partitionKey">The partition key for the item.</param>
        /// <param name="requestOptions">(Optional) The options for the item request.</param>
        /// <param name="cancellationToken">(Optional) CancellationToken representing request cancellation.</param>
        /// <typeparam name="T">The resource type.</typeparam>
        /// <returns>The response or null if not found.</returns>
        public async Task<ItemResponse<T>?> TryReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await container.ReadItemAsync<T>(id, partitionKey, requestOptions: requestOptions, cancellationToken: cancellationToken);
                return response;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
            {
                return null;
            }
        }
        
        /// <summary>
        /// Reads an item from the container and handles a HttpStatusCode.NotFound exception as a null response.
        /// </summary>
        /// <param name="id">The Cosmos item id</param>
        /// <param name="partitionKey">The partition key for the item.</param>
        /// <param name="requestOptions">(Optional) The options for the item request.</param>
        /// <param name="cancellationToken">(Optional) CancellationToken representing request cancellation.</param>
        /// <typeparam name="T">The resource type.</typeparam>
        /// <returns>The response resource or null if not found.</returns>
        public Task<T?> TryReadItemResourceAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            container.TryReadItemAsync<T>(id: id, partitionKey, requestOptions, cancellationToken).Then(x => x is null ? default : x.Resource);
    }
}