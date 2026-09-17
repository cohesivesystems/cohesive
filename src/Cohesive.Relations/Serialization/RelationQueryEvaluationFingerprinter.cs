using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Serialization;

/// <summary>Versioned cryptographic fingerprint of one complete canonical evaluation.</summary>
public sealed record RelationQueryEvaluationFingerprint
{
    /// <summary>Creates an evaluation fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A required string is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryEvaluationFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>Computes stable fingerprints for portable canonical relation/query evaluations.</summary>
/// <remarks>
/// The exact immutable compilation snapshot is weakly memoized by object identity because it commonly dominates an
/// evaluation document and is shared by many runtime evaluations. Per-evaluation evidence is always serialized and
/// hashed anew. Segmented hashing is byte-for-byte equivalent to the declared v3 canonicalization profile and does
/// not introduce a second semantic identity for compilation requests.
/// </remarks>
public static class RelationQueryEvaluationFingerprinter
{
    static readonly JsonSerializerOptions CanonicalOptions = RelationQueryEvaluationJsonSerializer.CreateOptions();
    static readonly ConditionalWeakTable<RelationQueryCompilationRequest, Lazy<byte[]>> CompilationCanonicalBytes = new();

    static ReadOnlySpan<byte> ObjectStartAndCompilationProperty => "{\"compilation\":"u8;
    static ReadOnlySpan<byte> EvaluationProperty => ",\"evaluation\":"u8;
    static ReadOnlySpan<byte> ParametersProperty => ",\"parameters\":"u8;
    static ReadOnlySpan<byte> PlanReferenceProperty => ",\"planReference\":"u8;
    static ReadOnlySpan<byte> SchemaVersionProperty => ",\"schemaVersion\":"u8;
    static ReadOnlySpan<byte> SuppliedRootsProperty => ",\"suppliedRoots\":"u8;
    static ReadOnlySpan<byte> Null => "null"u8;
    static ReadOnlySpan<byte> ObjectEnd => "}"u8;

    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonical evaluation profile identifier.</summary>
    public const string Canonicalization = "relation-query-evaluation/v3-c14n/v1";

    /// <summary>
    /// Computes a fingerprint over compilation snapshots, demand origin, evaluation identity, runtime input evidence,
    /// supplied roots, provenance, and optional compiled-plan attribution.
    /// </summary>
    /// <param name="evaluation">Normalized canonical evaluation to fingerprint.</param>
    /// <returns>A versioned SHA-256 fingerprint excluding only the persisted fingerprint property itself.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="evaluation"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Canonical evaluation JSON cannot be materialized.</exception>
    /// <exception cref="JsonException">Canonical evaluation content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical evaluation content contains an unsupported type.</exception>
    public static RelationQueryEvaluationFingerprint Compute(RelationQueryEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonicalSegments(evaluation, hash.AppendData);
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    internal static byte[] GetCanonicalBytes(RelationQueryEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArrayBufferWriter<byte> buffer = new();
        AppendCanonicalSegments(evaluation, buffer.Write);
        return buffer.WrittenSpan.ToArray();
    }

    static void AppendCanonicalSegments(RelationQueryEvaluation evaluation, CanonicalSegmentAppender append)
    {
        append(ObjectStartAndCompilationProperty);
        append(CompilationCanonicalBytes.GetValue(
            evaluation.Compilation,
            static compilation => new(
                () => GetCanonicalBytes(
                    compilation,
                    RelationCanonicalJsonArrayOrderings.Compilation),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value);
        append(EvaluationProperty);
        append(GetCanonicalBytes(evaluation.Evaluation));
        append(ParametersProperty);
        append(GetCanonicalBytes(evaluation.Parameters));
        append(PlanReferenceProperty);
        append(evaluation.PlanReference is null
            ? Null
            : GetCanonicalBytes(evaluation.PlanReference));
        append(SchemaVersionProperty);
        append(GetCanonicalBytes(evaluation.SchemaVersion));
        append(SuppliedRootsProperty);
        append(evaluation.SuppliedRoots is null
            ? Null
            : GetCanonicalBytes(
                evaluation.SuppliedRoots,
                RelationCanonicalJsonArrayOrderings.SuppliedRoots));
        append(ObjectEnd);
    }

    static byte[] GetCanonicalBytes<T>(
        T value,
        Func<CanonicalJsonArrayPath, CanonicalJsonArrayOrdering>? getArrayOrdering = null)
    {
        var node = JsonSerializer.SerializeToNode(value, CanonicalOptions)
            ?? throw new InvalidOperationException(
                $"Failed to materialize canonical JSON for '{typeof(T).Name}'.");
        return getArrayOrdering is null
            ? CanonicalJsonWriter.GetCanonicalSequenceBytes(node, CanonicalOptions)
            : CanonicalJsonWriter.GetCanonicalBytes(node, CanonicalOptions, getArrayOrdering);
    }

    delegate void CanonicalSegmentAppender(ReadOnlySpan<byte> value);
}
