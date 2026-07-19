using System.Security.Cryptography;
using System.Text;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Shared deterministic normalization and identity helpers for Cosmos physical affinity.</summary>
internal static class CosmosPhysicalAffinity
{
    /// <summary>Normalizes one absolute Cosmos account endpoint.</summary>
    /// <param name="endpoint">Absolute Cosmos account endpoint without credentials, a query, or a fragment.</param>
    /// <returns>A normalized absolute endpoint whose URI representation has one trailing separator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/> is relative, is not HTTP or HTTPS, has no host, or contains credentials, a query,
    /// or a fragment.
    /// </exception>
    internal static Uri NormalizeAccountEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
            throw new ArgumentException("A Cosmos account endpoint must be absolute.", nameof(endpoint));
        if ((endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException(
                "A Cosmos account endpoint must be an HTTP or HTTPS URI with a host.",
                nameof(endpoint));
        }
        if (!string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "A Cosmos account endpoint cannot contain credentials, a query, or a fragment.",
                nameof(endpoint));
        }

        var normalized = endpoint.AbsoluteUri.TrimEnd('/') + "/";
        return new Uri(normalized, UriKind.Absolute);
    }

    /// <summary>Gets canonical endpoint text without a trailing separator for existing source identities.</summary>
    /// <param name="endpoint">Absolute Cosmos account endpoint.</param>
    /// <returns>Normalized absolute endpoint text without a trailing separator.</returns>
    internal static string CanonicalAccountEndpointText(Uri endpoint) =>
        NormalizeAccountEndpoint(endpoint).AbsoluteUri.TrimEnd('/');

    /// <summary>Computes a lowercase SHA-256 identity for non-secret physical affinity text.</summary>
    /// <param name="value">Non-null affinity text.</param>
    /// <returns>The lowercase hexadecimal SHA-256 value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    internal static string Fingerprint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
