using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Computes stable content fingerprints for canonical relation/query definitions.
/// </summary>
/// <remarks>
/// The v4 canonicalization profile writes UTF-8 JSON with ordinal object-key ordering,
/// stable-id ordering for set-like definition collections, preserved order for semantic
/// sequences, unescaped Unicode scalar text, and canonical round-trip JSON numbers. Exact
/// decimal values and double values that round-trip through <see cref="decimal"/> share one
/// normalized decimal spelling.
/// Numerically equivalent positive and negative zero values are normalized to zero.
/// Query parameter defaults include an explicit discriminator when a fallback is declared,
/// preserving the semantic difference between no fallback and an explicit null fallback.
/// </remarks>
public static class RelationQueryDefinitionFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile identifier.</summary>
    public const string Canonicalization = "relation-query/v1-c14n/v4";

    /// <summary>Computes a content fingerprint that excludes document metadata and physical plans.</summary>
    /// <param name="definition">Canonical semantic definition to fingerprint.</param>
    /// <returns>Versioned canonicalization profile and SHA-256 digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="JsonException">
    /// The definition contains a value that cannot be written using the strict canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The definition contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static RelationQueryDefinitionFingerprint Compute(RelationQueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var canonicalDefinition = GetCanonicalDefinitionBytes(definition);
        var version = Encoding.UTF8.GetBytes(RelationQueryDocument.CurrentSchemaVersion);
        var content = new byte[version.Length + 1 + canonicalDefinition.Length];
        version.CopyTo(content, 0);
        content[version.Length] = 0;
        canonicalDefinition.CopyTo(content, version.Length + 1);

        var hash = SHA256.HashData(content);
        return new(
            algorithm: Algorithm,
            canonicalization: Canonicalization,
            value: Convert.ToHexString(hash).ToLowerInvariant());
    }

    internal static byte[] GetCanonicalDefinitionBytes(RelationQueryDefinition definition)
    {
        var options = RelationQueryJsonSerializer.CreateOptions();
        var node = JsonSerializer.SerializeToNode(definition, options)
                   ?? throw new InvalidOperationException("Failed to materialize canonical relation/query definition JSON.");

        return CanonicalJsonWriter.GetCanonicalBytes(
            node,
            options,
            RelationCanonicalJsonArrayOrderings.Definition);
    }
}
