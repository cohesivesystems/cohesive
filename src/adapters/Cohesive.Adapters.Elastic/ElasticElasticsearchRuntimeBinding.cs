using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using global::Elastic.Clients.Elasticsearch;

namespace Cohesive.Adapters.Elastic;

/// <summary>Stable non-secret identity of one Elasticsearch cluster.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ElasticClusterId
{
    /// <summary>Creates an Elasticsearch cluster identity.</summary>
    /// <param name="value">
    /// Stable deployment identity, preferably the Elasticsearch cluster UUID obtained from the cluster information
    /// API rather than an endpoint or credential.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty, exceeds 256 characters, or contains a control character.
    /// </exception>
    [JsonConstructor]
    public ElasticClusterId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
        if (Value.Length > 256 || Value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An Elasticsearch cluster identity must be bounded non-secret text without control characters.",
                nameof(value));
        }
    }

    /// <summary>Gets the stable non-secret cluster identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable cluster identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Sanitized deterministic fingerprint of one Elasticsearch runtime attestation.</summary>
/// <remarks>
/// The fingerprint covers the declared cluster identity, adapter authority, and client assembly version. It contains
/// no endpoint, API key, certificate, or other connection configuration and therefore does not make independently
/// constructed clients interchangeable. Exact runtime affinity is additionally enforced by client object identity.
/// </remarks>
public sealed record ElasticElasticsearchRuntimeFingerprint
{
    internal ElasticElasticsearchRuntimeFingerprint(
        string algorithm,
        string canonicalization,
        string value)
    {
        Algorithm = algorithm;
        Canonicalization = canonicalization;
        Value = value;
    }

    /// <summary>Gets the hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the sanitized runtime-attestation canonicalization profile.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>
/// Runtime attestation binding one persisted Elasticsearch cluster identity to one exact
/// <see cref="ElasticsearchClient"/> instance.
/// </summary>
/// <remarks>
/// The client is borrowed and must outlive every materialization target constructed with this binding. The binding
/// does not own or dispose the client. The runtime owner is responsible for verifying that the client reaches
/// <see cref="Cluster"/>, normally by comparing the cluster information API's UUID before registering the target.
/// <see cref="Authority"/> is provenance and must never contain a connection string, URI, API key, or credential.
/// </remarks>
public sealed class ElasticElasticsearchRuntimeBinding
{
    readonly ElasticsearchClient client;

    /// <summary>Creates an exact borrowed-client runtime binding.</summary>
    /// <param name="cluster">Persisted cluster identity attested by the runtime owner.</param>
    /// <param name="client">Caller-owned Elasticsearch client covered by the attestation.</param>
    /// <param name="authority">
    /// Stable non-secret identity of the deployment or configuration authority making the attestation.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="client"/> or <paramref name="authority"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="cluster"/> is default, or <paramref name="authority"/> is empty, exceeds 256 characters, or is
    /// not an ASCII provenance identity composed of letters, digits, <c>/</c>, <c>.</c>, <c>_</c>, <c>-</c>,
    /// <c>:</c>, or <c>@</c>.
    /// </exception>
    public ElasticElasticsearchRuntimeBinding(
        ElasticClusterId cluster,
        ElasticsearchClient client,
        string authority)
    {
        if (string.IsNullOrWhiteSpace(cluster.Value))
        {
            throw new ArgumentException(
                "An Elasticsearch runtime binding requires a persisted cluster identity.",
                nameof(cluster));
        }

        this.client = client ?? throw new ArgumentNullException(nameof(client));
        Cluster = cluster;
        Authority = ElasticMaterializationBindingContract.RequireAuthority(authority, nameof(authority));
        ClientVersion = client.GetType().Assembly.GetName().Version?.ToString() ?? "unknown";
        Fingerprint = ComputeFingerprint(cluster, Authority, ClientVersion);
    }

    /// <summary>Gets the persisted cluster identity attested by this binding.</summary>
    public ElasticClusterId Cluster { get; }

    /// <summary>Gets the stable non-secret authority that supplied the runtime attestation.</summary>
    public string Authority { get; }

    /// <summary>Gets the Elasticsearch client assembly version included in capability provenance.</summary>
    public string ClientVersion { get; }

    /// <summary>Gets the sanitized runtime-attestation fingerprint.</summary>
    public ElasticElasticsearchRuntimeFingerprint Fingerprint { get; }

    internal ElasticsearchClient Client => client;

    static ElasticElasticsearchRuntimeFingerprint ComputeFingerprint(
        ElasticClusterId cluster,
        string authority,
        string clientVersion)
    {
        const string algorithm = "sha256";
        const string canonicalization = "cohesive.adapters.elastic/runtime-attestation/v1";
        StringBuilder canonical = new(256);
        Append(canonical, canonicalization);
        Append(canonical, cluster.Value);
        Append(canonical, authority);
        Append(canonical, clientVersion);
        return new(
            algorithm,
            canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));
    }

    static void Append(StringBuilder canonical, string value)
    {
        canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(value);
        canonical.Append(';');
    }
}
