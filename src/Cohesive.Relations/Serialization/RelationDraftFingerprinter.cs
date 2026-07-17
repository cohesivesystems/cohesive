using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Relations.Drafts;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Computes stable semantic content fingerprints for portable relation drafts.
/// </summary>
/// <remarks>
/// <para>
/// The v2 canonicalization profile writes UTF-8 JSON with ordinal object-key ordering,
/// stable-id ordering for set-like draft and logical-query collections, preserved order for
/// semantic sequences, ordinal ordering for ambiguous candidate-id and unresolved-reason sets,
/// unescaped Unicode scalar text, and canonical round-trip JSON numbers. Exact decimal values
/// and double values that round-trip through <see cref="decimal"/> share one normalized decimal
/// spelling. Numerically equivalent positive and negative zero values are normalized to zero.
/// </para>
/// <para>
/// A draft's lifecycle identifier and document metadata are not semantic inputs and are excluded.
/// Intended relation identity, logical input, projection assignments and candidates, resolutions,
/// output semantics, and invariants are included.
/// </para>
/// </remarks>
public static class RelationDraftFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile identifier.</summary>
    public const string Canonicalization = "relation-draft/v1-c14n/v2";

    /// <summary>Computes a semantic content fingerprint for a portable relation draft.</summary>
    /// <param name="draft">Portable relation draft to fingerprint.</param>
    /// <returns>Versioned canonicalization profile and SHA-256 digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The draft contains a value that has no canonical relation draft JSON encoding.
    /// </exception>
    /// <exception cref="JsonException">
    /// The draft contains a value that cannot be written using the strict canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The draft contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static RelationDraftFingerprint Compute(RelationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var canonicalDraft = GetCanonicalSemanticBytes(draft);
        var version = Encoding.UTF8.GetBytes(RelationDraftDocument.CurrentSchemaVersion);
        var content = new byte[version.Length + 1 + canonicalDraft.Length];
        version.CopyTo(content, 0);
        content[version.Length] = 0;
        canonicalDraft.CopyTo(content, version.Length + 1);

        var hash = SHA256.HashData(content);
        return new(
            algorithm: Algorithm,
            canonicalization: Canonicalization,
            value: Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>Gets the canonical JSON bytes used as semantic fingerprint input.</summary>
    /// <param name="draft">Portable relation draft to canonicalize.</param>
    /// <returns>Canonical UTF-8 JSON excluding the draft lifecycle identifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The serialized draft is not an object, does not contain its lifecycle identifier, or
    /// contains a value with no canonical relation draft JSON encoding.
    /// </exception>
    /// <exception cref="JsonException">
    /// The draft contains a value that cannot be written using the strict canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The draft contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    internal static byte[] GetCanonicalSemanticBytes(RelationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var options = RelationDraftJsonSerializer.CreateOptions();
        var node = JsonSerializer.SerializeToNode(draft, options) as JsonObject
                   ?? throw new InvalidOperationException(
                       "Failed to materialize canonical relation draft JSON as an object.");

        var lifecycleIdProperty = options.PropertyNamingPolicy?.ConvertName(nameof(RelationDraft.Id))
                                  ?? nameof(RelationDraft.Id);
        if (!node.Remove(lifecycleIdProperty))
        {
            throw new InvalidOperationException(
                $"Canonical relation draft JSON does not contain lifecycle property '{lifecycleIdProperty}'.");
        }

        return CanonicalJsonWriter.GetCanonicalBytes(
            node,
            options,
            static propertyName => propertyName switch
            {
                "nodes" or "parameters" or "assignments" or "candidates"
                    or "groupings" or "aggregates" => "id",
                "invariants" => "name",
                _ => null
            },
            static propertyName => propertyName is "candidateIds" or "reasons");
    }
}
