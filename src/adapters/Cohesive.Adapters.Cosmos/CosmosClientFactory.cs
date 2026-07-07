using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Options for <see cref="CosmosClientFactory"/>.
/// </summary>
public record CosmosClientFactoryOptions
{
    public string? Endpoint { get; init; }
    
    public string? AccountKey { get; init; }
    
    public bool? AllowInsecureServerCertificate { get; init; }

    public bool UseDefaultCredential { get; init; } = true;

    [MemberNotNullWhen(true, nameof(Endpoint))]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && (!string.IsNullOrEmpty(AccountKey) || UseDefaultCredential);
    
    public TokenCredential? GetCredential() => 
        UseDefaultCredential ? new DefaultAzureCredential() : null;
}

/// <summary>
/// A factory for creating Cosmos DB clients.
/// </summary>
public class CosmosClientFactory
{
    readonly ConcurrentDictionary<CosmosAccountClientCacheKey, Lazy<CosmosClient>> clientsByAccountKey = new();

    /// <summary>
    /// The default factory instance.
    /// </summary>
    public static readonly CosmosClientFactory Shared = new();
    
    /// <summary>
    /// Creates a Cosmos DB client.
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">The Cosmos client was not properly configured.</exception>
    public CosmosClient CreateCosmosClient(CosmosClientFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        
        if (!options.IsConfigured)
            throw new InvalidOperationException("Training runtime Cosmos endpoint is not configured.");
        
        var cacheKey = new CosmosAccountClientCacheKey(
            Endpoint: options.Endpoint,
            AccountKey: options.AccountKey,
            AllowInsecureServerCertificate: options.AllowInsecureServerCertificate is true,
            UseDefaultCredential: options.UseDefaultCredential
        );

        return clientsByAccountKey.GetOrAdd(cacheKey, static key => new(() => CreateAccountKeyClient(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    static CosmosClient CreateAccountKeyClient(CosmosAccountClientCacheKey key)
    {
        if (!string.IsNullOrWhiteSpace(key.AccountKey))
            return new(
                accountEndpoint: key.Endpoint,
                authKeyOrResourceToken: key.AccountKey,
                clientOptions: CreateClientOptions(key.AllowInsecureServerCertificate)
            );

        if (key.UseDefaultCredential)
            return new(accountEndpoint: key.Endpoint, tokenCredential: new DefaultAzureCredential(), CreateClientOptions(key.AllowInsecureServerCertificate is true));
        
        throw new InvalidOperationException("Cosmos account key or token credential is not configured.");
    }

    static CosmosClientOptions CreateClientOptions(bool allowInsecureServerCertificate)
    {
        var clientOptions = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer()
        };

        if (!allowInsecureServerCertificate)
            return clientOptions;

        clientOptions.ConnectionMode = ConnectionMode.Gateway;
        clientOptions.HttpClientFactory = static () =>
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            return new(handler, disposeHandler: true);
        };

        return clientOptions;
    }

    readonly record struct CosmosAccountClientCacheKey(
        string Endpoint,
        string? AccountKey,
        bool AllowInsecureServerCertificate,
        bool UseDefaultCredential
        );
}
