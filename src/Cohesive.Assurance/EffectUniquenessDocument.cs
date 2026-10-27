using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Strict retained check input and integrity envelope; verdicts are re-evaluated rather than trusted on restore.</summary>
/// <remarks>This is the first evidence profile, not a generic artifact store or authenticity mechanism.</remarks>
public static class EffectUniquenessDocument
{
    static readonly JsonSerializerOptions Options = CreateOptions();
    sealed record Envelope(string Profile, string Evaluator, string Fingerprint, EffectUniquenessCheck Input);

    /// <summary>Computes content identity including the semantic profile and all check dependencies.</summary>
    /// <param name="input">Exact check input to fingerprint.</param>
    /// <returns>Profile-scoped SHA-256 content identity; it does not authorize evidence reuse.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public static string Fingerprint(EffectUniquenessCheck input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = StrictDocumentJson.GetCanonicalBytes(new Envelope(EffectUniquenessEvaluator.Profile, EffectUniquenessEvaluator.Identity, "", input), Options);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Serializes a check using the existing canonical JSON profile and a computed fingerprint.</summary>
    /// <param name="input">Immutable check input.</param>
    /// <returns>Deterministic compact JSON with exact target and mapping provenance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public static string Serialize(EffectUniquenessCheck input) => Encoding.UTF8.GetString(
        StrictDocumentJson.GetCanonicalBytes(new Envelope(EffectUniquenessEvaluator.Profile, EffectUniquenessEvaluator.Identity, Fingerprint(input), input), Options));

    /// <summary>Restores and verifies a retained check before running the current exact-profile evaluator.</summary>
    /// <param name="json">Retained UTF-16 JSON document.</param>
    /// <returns>Recomputed evidence for the verified exact input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">JSON is malformed, incomplete, duplicated, unknown, or fails integrity checks.</exception>
    /// <exception cref="ArgumentException">A restored identity or enumeration is invalid.</exception>
    public static EffectUniquenessEvidence Restore(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        if (StrictDocumentJson.TryFindDuplicateProperty(document.RootElement, "", out var location))
            throw new JsonException($"Duplicate property at '{location}'.");
        var envelope = document.RootElement.Deserialize<Envelope>(Options)
            ?? throw new JsonException("An evidence object is required.");
        if (envelope.Profile != EffectUniquenessEvaluator.Profile || envelope.Evaluator != EffectUniquenessEvaluator.Identity)
            throw new JsonException("Unsupported assurance profile.");
        if (envelope.Input is null || envelope.Fingerprint != Fingerprint(envelope.Input))
            throw new JsonException("Evidence fingerprint mismatch.");
        return EffectUniquenessEvaluator.Evaluate(envelope.Input);
    }

    static JsonSerializerOptions CreateOptions()
    {
        var options = StrictDocumentJson.CreateOptions();
        options.RespectRequiredConstructorParameters = true;
        return options;
    }
}
