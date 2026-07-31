using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Deterministic fingerprint of one exact canonical Relations physical source-read intent.</summary>
public sealed record MaterializationSourceReadFingerprint
{
    /// <summary>Creates a source-read fingerprint.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization algorithm identity.</param>
    /// <param name="value">Lower-case hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A component is empty or contains ill-formed Unicode, or <paramref name="value"/> is not lower-case hexadecimal.</exception>
    [JsonConstructor]
    public MaterializationSourceReadFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = MaterializationContract.RequireUnicodeIdentity(algorithm, nameof(algorithm));
        Canonicalization = MaterializationContract.RequireUnicodeIdentity(canonicalization, nameof(canonicalization));
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));
        if (value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A source-read fingerprint value must be lower-case hexadecimal.", nameof(value));
        }
    }

    /// <summary>Stable digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Stable canonicalization algorithm identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lower-case hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Computes deterministic fingerprints for exact canonical Relations source-read requests.</summary>
public static class MaterializationSourceReadFingerprinter
{
    /// <summary>Digest algorithm used by source-read fingerprints.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization identity used by source-read fingerprints.</summary>
    public const string Canonicalization = "cohesive-relations-source-read/v1-c14n/v1";

    /// <summary>Computes a fingerprint covering every semantic field of one source-read request.</summary>
    /// <param name="request">Exact canonical Relations source-read request.</param>
    /// <returns>The deterministic source-read fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The request has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">A request value has no configured JSON representation.</exception>
    public static MaterializationSourceReadFingerprint Compute(RelationQuerySourceReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = StrictDocumentJson.GetCanonicalBytes(request, CreateOptions());
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    static JsonSerializerOptions CreateOptions() => RelationQueryJsonSerializer.CreateOptions();
}
