using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Computes stable semantic content fingerprints for canonical relationship catalogs.
/// </summary>
/// <remarks>
/// The v2 canonicalization profile writes UTF-8 JSON with ordinal object-key ordering,
/// ordinal relationship-id ordering for the set-like relationship collection, preserved
/// order for semantic sequences such as field-path segments, unescaped Unicode scalar text,
/// and canonical round-trip JSON numbers. Exact decimal values and double values that round-trip
/// through <see cref="decimal"/> share one normalized decimal spelling. Document metadata is not
/// part of the input.
/// </remarks>
public static class RelationshipCatalogFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile identifier.</summary>
    public const string Canonicalization = "relationship-catalog/v1-c14n/v2";

    /// <summary>Computes a semantic content fingerprint for a relationship catalog.</summary>
    /// <param name="catalog">Canonical semantic relationship catalog to fingerprint.</param>
    /// <returns>Versioned canonicalization profile and SHA-256 digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The catalog contains a value that has no canonical relationship catalog JSON encoding.
    /// </exception>
    /// <exception cref="JsonException">
    /// The catalog contains a value that cannot be written using the strict canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The catalog contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static RelationshipCatalogFingerprint Compute(RelationshipCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var canonicalCatalog = GetCanonicalCatalogBytes(catalog);
        var version = Encoding.UTF8.GetBytes(RelationshipCatalogDocument.CurrentSchemaVersion);
        var content = new byte[version.Length + 1 + canonicalCatalog.Length];
        version.CopyTo(content, 0);
        content[version.Length] = 0;
        canonicalCatalog.CopyTo(content, version.Length + 1);

        var hash = SHA256.HashData(content);
        return new(
            algorithm: Algorithm,
            canonicalization: Canonicalization,
            value: Convert.ToHexString(hash).ToLowerInvariant());
    }

    static byte[] GetCanonicalCatalogBytes(RelationshipCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var options = RelationshipCatalogJsonSerializer.CreateOptions();
        var node = JsonSerializer.SerializeToNode(catalog, options)
                   ?? throw new InvalidOperationException(
                       "Failed to materialize canonical relationship catalog JSON.");

        return CanonicalJsonWriter.GetCanonicalBytes(
            node,
            options,
            RelationCanonicalJsonArrayOrderings.RelationshipCatalog);
    }
}
