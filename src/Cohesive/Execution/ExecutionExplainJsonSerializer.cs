using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Computes deterministic identities for execution explain artifacts.</summary>
public static class ExecutionExplainFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonical execution-explain profile identifier.</summary>
    public const string Canonicalization = "cohesive-execution-explain/v1-c14n/v1";

    /// <summary>Computes deterministic explain identity without runtime observations or human prose.</summary>
    /// <param name="artifact">Normalized explain artifact.</param>
    /// <returns>A SHA-256 fingerprint over deterministic authored and interpretation evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Explain content cannot be materialized as JSON.</exception>
    /// <exception cref="JsonException">Explain content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static ExecutionExplainFingerprint Compute(ExecutionExplainArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var root = ExecutionExplainJsonSerializer.ToJsonObject(artifact);
        root.Remove("fingerprint");
        root.Remove("runtimeStatus");
        RemoveMeasuredEvidence(root);
        RemoveHumanProse(root);
        var canonical = CanonicalJsonWriter.GetCanonicalBytes(
            root,
            ExecutionExplainJsonSerializer.CreateOptions(),
            static _ => CanonicalJsonArrayOrdering.Sequence,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    static void RemoveMeasuredEvidence(JsonObject root)
    {
        if (root["evidence"] is not JsonArray evidence)
            return;
        for (var index = evidence.Count - 1; index >= 0; index--)
        {
            if (evidence[index] is JsonObject item
                && string.Equals(
                    item["authority"]?.GetValue<string>(),
                    ExecutionExplainEvidenceAuthority.Measured.ToString(),
                    StringComparison.Ordinal))
            {
                evidence.RemoveAt(index);
            }
        }
    }

    static void RemoveHumanProse(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                value.Remove("message");
                value.Remove("description");
                value.Remove("resolutionOptions");
                foreach (var property in value.ToArray())
                    RemoveHumanProse(property.Value);
                break;
            case JsonArray array:
                foreach (var item in array)
                    RemoveHumanProse(item);
                break;
        }
    }
}

/// <summary>Strict deterministic JSON boundary for execution explain artifacts.</summary>
public static class ExecutionExplainJsonSerializer
{
    /// <summary>Creates strict serializer options for the execution-explain wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict, case-sensitive portable-document options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one verified explain artifact.</summary>
    /// <param name="artifact">Artifact to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable execution-explain JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Explain content cannot be materialized as JSON.</exception>
    /// <exception cref="JsonException">Explain content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static string Serialize(
        ExecutionExplainArtifact artifact,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(artifact))
            : JsonSerializer.Serialize(artifact, CreateOptions(formatting));
    }

    /// <summary>Gets canonical UTF-8 JSON for one complete explain artifact.</summary>
    /// <param name="artifact">Artifact to serialize.</param>
    /// <returns>Canonical UTF-8 JSON including deterministic and runtime-observation fields.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Explain content cannot be materialized as JSON.</exception>
    /// <exception cref="JsonException">Explain content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static byte[] GetCanonicalBytes(ExecutionExplainArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return CanonicalJsonWriter.GetCanonicalBytes(
            ToJsonObject(artifact),
            CreateOptions(),
            static _ => CanonicalJsonArrayOrdering.Sequence,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);
    }

    /// <summary>Deserializes, normalizes, and verifies one current-version execution explain artifact.</summary>
    /// <param name="json">Persisted explain JSON.</param>
    /// <returns>A normalized artifact with verified affinity and fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="json"/> is empty or white space.</exception>
    /// <exception cref="JsonException">JSON is malformed, duplicated, inconsistent, or uses an unsupported schema.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static ExecutionExplainArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("An execution explain artifact must be a JSON object.");
        if (StrictDocumentJson.TryFindDuplicateProperty(parsed.RootElement, string.Empty, out var duplicate))
            throw new JsonException($"Execution explain JSON contains duplicate property '{duplicate}'.");

        try
        {
            var artifact = JsonSerializer.Deserialize<ExecutionExplainArtifact>(json, CreateOptions())
                ?? throw new JsonException("Execution explain JSON produced no artifact.");
            var canonical = Serialize(artifact);
            using var canonicalDocument = JsonDocument.Parse(canonical);
            if (!JsonElement.DeepEquals(parsed.RootElement, canonicalDocument.RootElement))
                throw new JsonException("Execution explain JSON is not in normalized wire form.");
            return artifact;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new JsonException(exception.Message, exception);
        }
    }

    internal static JsonObject ToJsonObject(ExecutionExplainArtifact artifact) =>
        JsonSerializer.SerializeToNode(artifact, CreateOptions()) as JsonObject
        ?? throw new InvalidOperationException("Failed to materialize execution explain JSON.");
}
