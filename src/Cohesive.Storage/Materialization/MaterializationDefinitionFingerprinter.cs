using System.Security.Cryptography;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Computes an exact portable-content fence for a canonical materialization definition.</summary>
public static class MaterializationDefinitionFingerprinter
{
    /// <summary>Cryptographic hash algorithm used by the current profile.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by the v2 materialization definition fence.</summary>
    public const string Canonicalization = "cohesive-materialization-definition/v2-c14n/v1";

    /// <summary>Computes the fingerprint of every canonical semantic, policy, provenance, and Control field.</summary>
    /// <param name="definition">Canonical materialization definition to fingerprint.</param>
    /// <returns>Versioned SHA-256 metadata fencing the complete definition content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Definition content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    public static ExecutionDefinitionFingerprint Compute(MaterializationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var options = RelationQueryJsonSerializer.CreateOptions();
        var canonical = StrictDocumentJson.GetCanonicalBytes(definition, options);
        var digest = SHA256.HashData(canonical);
        return new(Algorithm, Canonicalization, Convert.ToHexStringLower(digest));
    }
}
