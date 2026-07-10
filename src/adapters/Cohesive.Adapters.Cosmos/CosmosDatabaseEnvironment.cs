using System.Net;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Options for provisioning a Cosmos database environment.
/// </summary>
public sealed record CosmosDatabaseEnvironmentOptions(
    CosmosClientFactoryOptions ClientOptions,
    string DatabaseName,
    bool DeleteDatabaseOnDispose = true
    );

/// <summary>
/// Owns a provisioned Cosmos database plus the containers created within it.
/// </summary>
public sealed class CosmosDatabaseEnvironment(
    Database database,
    IReadOnlyDictionary<string, (ContainerProperties, Container)> containersByName,
    bool deleteDatabaseOnDispose
    ) : IAsyncDisposable
{
    /// <summary>Gets the database.</summary>
    public Database Database { get; } = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Gets the database name.</summary>
    public string DatabaseName => Database.Id;

    /// <summary>Gets the containers by name.</summary>
    public IReadOnlyDictionary<string, (ContainerProperties, Container)> ContainersByName { get; } = Guard.RequireNotNull(containersByName);
    
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!deleteDatabaseOnDispose)
            return;

        try
        {
            await Database.DeleteAsync().ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }

        // CosmosClientFactory may return a shared cached client, so this environment
        // owns the database lifecycle but intentionally does not dispose the client.
    }
}

/// <summary>Provides operations for cosmos client factory extensions.</summary>
public static class CosmosClientFactoryExtensions
{
    /// <summary>
    /// Creates a Cosmos database if needed and ensures the requested containers exist.
    /// </summary>
    /// <exception cref="InvalidOperationException">Cosmos database name is not configured; duplicate container names.</exception>
    public static async Task<CosmosDatabaseEnvironment> CreateDatabaseEnvironment(
        this CosmosClientFactory factory,
        CosmosDatabaseEnvironmentOptions options,
        IEnumerable<ContainerProperties> containers,
        CancellationToken ct = default
        )
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ClientOptions);
        ArgumentNullException.ThrowIfNull(containers);

        if (string.IsNullOrWhiteSpace(options.DatabaseName))
            throw new InvalidOperationException("Cosmos database name is not configured.");

        var client = factory.CreateCosmosClient(options.ClientOptions);

        Database database = await client
            .CreateDatabaseIfNotExistsAsync(id: options.DatabaseName, cancellationToken: ct)
            .ConfigureAwait(false);

        var containersByName = new Dictionary<string, (ContainerProperties, Container)>(StringComparer.Ordinal);
        foreach (var containerProperties in containers)
        {
            ArgumentNullException.ThrowIfNull(containerProperties);
            ArgumentException.ThrowIfNullOrWhiteSpace(containerProperties.Id);

            if (containersByName.ContainsKey(containerProperties.Id))
                throw new InvalidOperationException($"Duplicate Cosmos container definition '{containerProperties.Id}' was provided for database '{options.DatabaseName}'.");

            var response = await database.CreateContainerIfNotExistsAsync(containerProperties, cancellationToken: ct);
            var actualProperties = response.Resource;
            if (!string.Equals(actualProperties.PartitionKeyPath, containerProperties.PartitionKeyPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cosmos container '{options.DatabaseName}/{containerProperties.Id}' has partition key path '{actualProperties.PartitionKeyPath}', but the runtime expects '{containerProperties.PartitionKeyPath}'. Cosmos partition key paths are immutable; recreate the container or use a different database/container name.");
            }

            Container container = response;
            containersByName.Add(containerProperties.Id, (actualProperties, container));
        }

        return new(
            database,
            containersByName: containersByName,
            deleteDatabaseOnDispose: options.DeleteDatabaseOnDispose
            );
    }
}
