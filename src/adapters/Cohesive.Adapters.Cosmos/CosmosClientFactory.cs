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
    /// <summary>Gets the endpoint.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets the account key.</summary>
    public string? AccountKey { get; init; }

    /// <summary>Gets or sets whether insecure server certificates are allowed.</summary>
    public bool? AllowInsecureServerCertificate { get; init; }

    /// <summary>Gets or sets whether the default Azure credential is used.</summary>
    public bool UseDefaultCredential { get; init; } = true;

    /// <summary>
    /// Gets the native Cosmos SDK telemetry configuration used by clients created from these options.
    /// </summary>
    /// <remarks>
    /// The default enables the SDK's <c>Azure.Cosmos.Operation</c> activities while retaining the SDK's
    /// query-text suppression and diagnostic thresholds. The factory snapshots the public configuration before
    /// resolving its client cache, so later mutations do not change an existing client.
    /// </remarks>
    public CosmosClientTelemetryOptions TelemetryOptions { get; init; } = CreateDefaultTelemetryOptions();

    /// <summary>Gets whether a usable Cosmos endpoint and credential are configured.</summary>
    [MemberNotNullWhen(true, nameof(Endpoint))]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && (!string.IsNullOrEmpty(AccountKey) || UseDefaultCredential);

    /// <summary>Gets credential.</summary>
    public TokenCredential? GetCredential() =>
        UseDefaultCredential ? new DefaultAzureCredential() : null;

    static CosmosClientTelemetryOptions CreateDefaultTelemetryOptions() => new()
    {
        DisableDistributedTracing = false,
        DisableSendingMetricsToService = true,
        QueryTextMode = QueryTextMode.None
    };
}

/// <summary>
/// A factory for creating Cosmos DB clients.
/// </summary>
public class CosmosClientFactory
{
    /// <summary>
    /// The native Cosmos SDK activity source that emits database operation activities.
    /// </summary>
    public const string OperationActivitySourceName = "Azure.Cosmos.Operation";

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
            UseDefaultCredential: options.UseDefaultCredential,
            Telemetry: CosmosClientTelemetryConfiguration.Capture(options.TelemetryOptions)
        );

        return clientsByAccountKey.GetOrAdd(cacheKey, static key => new(() => CreateAccountKeyClient(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    static CosmosClient CreateAccountKeyClient(CosmosAccountClientCacheKey key)
    {
        if (!string.IsNullOrWhiteSpace(key.AccountKey))
            return new(
                accountEndpoint: key.Endpoint,
                authKeyOrResourceToken: key.AccountKey,
                clientOptions: CreateClientOptions(key.AllowInsecureServerCertificate, key.Telemetry)
            );

        if (key.UseDefaultCredential)
            return new(accountEndpoint: key.Endpoint, tokenCredential: new DefaultAzureCredential(), CreateClientOptions(key.AllowInsecureServerCertificate is true, key.Telemetry));

        throw new InvalidOperationException("Cosmos account key or token credential is not configured.");
    }

    static CosmosClientOptions CreateClientOptions(
        bool allowInsecureServerCertificate,
        CosmosClientTelemetryConfiguration telemetry)
    {
        var clientOptions = new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer(),
            CosmosClientTelemetryOptions = telemetry.ToSdkOptions()
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
        bool UseDefaultCredential,
        CosmosClientTelemetryConfiguration Telemetry
        );

    readonly record struct CosmosClientTelemetryConfiguration(
        bool DisableSendingMetricsToService,
        bool DisableDistributedTracing,
        TimeSpan NonPointOperationLatencyThreshold,
        TimeSpan PointOperationLatencyThreshold,
        double? RequestChargeThreshold,
        int? PayloadSizeThresholdInBytes,
        QueryTextMode QueryTextMode)
    {
        public static CosmosClientTelemetryConfiguration Capture(CosmosClientTelemetryOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            CosmosThresholdOptions thresholds = options.CosmosThresholdOptions;

            return new(
                options.DisableSendingMetricsToService,
                options.DisableDistributedTracing,
                thresholds.NonPointOperationLatencyThreshold,
                thresholds.PointOperationLatencyThreshold,
                thresholds.RequestChargeThreshold,
                thresholds.PayloadSizeThresholdInBytes,
                options.QueryTextMode);
        }

        public CosmosClientTelemetryOptions ToSdkOptions() => new()
        {
            DisableSendingMetricsToService = DisableSendingMetricsToService,
            DisableDistributedTracing = DisableDistributedTracing,
            CosmosThresholdOptions = new()
            {
                NonPointOperationLatencyThreshold = NonPointOperationLatencyThreshold,
                PointOperationLatencyThreshold = PointOperationLatencyThreshold,
                RequestChargeThreshold = RequestChargeThreshold,
                PayloadSizeThresholdInBytes = PayloadSizeThresholdInBytes
            },
            QueryTextMode = QueryTextMode
        };
    }
}
