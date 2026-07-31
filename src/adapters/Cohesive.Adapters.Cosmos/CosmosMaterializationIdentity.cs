using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Shared canonical identity operations used by Cosmos materialization realizations.</summary>
internal static class CosmosMaterializationIdentity
{
    static readonly System.Text.Json.JsonSerializerOptions CanonicalJsonOptions =
        MaterializationJsonSerializer.CreateOptions();

    /// <summary>Computes the canonical full-content fingerprint of one source placement.</summary>
    /// <param name="placement">Exact source placement whose semantic content is fingerprinted.</param>
    /// <returns>Lowercase SHA-256 over the strict canonical placement document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    internal static string ComputePlacementFingerprint(RelationQuerySourcePlacementBinding placement) =>
        Convert.ToHexStringLower(SHA256.HashData(
            StrictDocumentJson.GetCanonicalBytes(
                value: Guard.RequireNotNull(placement),
                options: CanonicalJsonOptions)));

    /// <summary>Computes a deterministic fingerprint over ordered attributable configuration references.</summary>
    /// <param name="references">Ordered non-null configuration references.</param>
    /// <returns>Lowercase length-framed SHA-256 over the supplied references.</returns>
    /// <exception cref="ArgumentException"><paramref name="references"/> contains a null value.</exception>
    internal static string ComputeReferenceFingerprint(ImmutableArray<string> references) =>
        ComputeOrderedFingerprint(values: references.AsSpan());

    /// <summary>Computes a deterministic fingerprint over ordered, length-framed text values.</summary>
    /// <param name="values">Ordered non-null values.</param>
    /// <returns>Lowercase length-framed SHA-256 over <paramref name="values"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="values"/> contains a null value.</exception>
    internal static string ComputeOrderedFingerprint(ReadOnlySpan<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            AppendFingerprintPart(hash: hash, value: value, parameterName: nameof(values));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Appends one non-null length-framed text value to an incremental fingerprint.</summary>
    /// <param name="hash">Incremental SHA-256 computation receiving the value.</param>
    /// <param name="value">Non-null text value.</param>
    /// <param name="parameterName">Caller-facing parameter name used when <paramref name="value"/> is null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hash"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is <see langword="null"/>.</exception>
    internal static void AppendFingerprintPart(
        IncrementalHash hash,
        string value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (value is null)
        {
            throw new ArgumentException("Fingerprint values cannot contain null values.", parameterName);
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
