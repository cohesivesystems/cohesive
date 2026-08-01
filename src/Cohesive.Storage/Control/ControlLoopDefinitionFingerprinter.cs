using System.Security.Cryptography;
using Cohesive.Execution;

namespace Cohesive.Control;

/// <summary>Computes the exact portable-content fence for a bounded Control loop definition.</summary>
/// <remarks>
/// The fingerprint covers the complete canonical definition wire, including hard-limit and configuration evidence
/// and provenance. A runtime must begin a new controller state when any of that exact definition content changes.
/// </remarks>
public static class ControlLoopDefinitionFingerprinter
{
    /// <summary>Cryptographic hash algorithm used by the v1 profile.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by the v2 definition-content fence.</summary>
    public const string Canonicalization = "cohesive-control-loop-definition/v2-c14n/v1";

    /// <summary>Computes the exact canonical-content fingerprint of a Control loop definition.</summary>
    /// <param name="definition">Canonical definition to fingerprint.</param>
    /// <returns>Versioned SHA-256 metadata fencing the exact portable definition content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">The definition cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">The definition contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The definition has no canonical JSON representation.</exception>
    public static ExecutionDefinitionFingerprint Compute(ControlLoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var digest = SHA256.HashData(ControlJsonSerializer.GetCanonicalBytes(definition));
        return new(Algorithm, Canonicalization, Convert.ToHexStringLower(digest));
    }
}
