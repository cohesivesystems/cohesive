using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;

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
public static class RelationQueryEvaluationFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonical evaluation profile identifier.</summary>
    public const string Canonicalization = "relation-query-evaluation/v1-c14n/v1";

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
        var options = RelationQueryEvaluationJsonSerializer.CreateOptions();
        var node = JsonSerializer.SerializeToNode(evaluation, options) as JsonObject
            ?? throw new InvalidOperationException("Failed to materialize canonical relation/query evaluation JSON.");
        node.Remove("fingerprint");

        var canonical = CanonicalJsonWriter.GetCanonicalBytes(
            node,
            options,
            RelationCanonicalJsonArrayOrderings.Evaluation);
        var hash = SHA256.HashData(canonical);
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(hash));
    }
}
