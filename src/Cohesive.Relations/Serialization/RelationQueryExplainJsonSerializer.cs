using System.Text.Json;
using Cohesive.Relations.Explain;

namespace Cohesive.Relations.Serialization;

/// <summary>Strict JSON serialization for canonical relation/query explain artifacts.</summary>
public static class RelationQueryExplainJsonSerializer
{
    /// <summary>Serializes one verified canonical explain artifact.</summary>
    /// <param name="artifact">Explain artifact to serialize.</param>
    /// <param name="indented">Whether persisted JSON is indented.</param>
    /// <returns>Deterministic portable explain JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The artifact cannot be serialized as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">The artifact contains an unsupported serialization type.</exception>
    public static string Serialize(RelationQueryExplainArtifact artifact, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return JsonSerializer.Serialize(artifact, CreateOptions(indented));
    }

    /// <summary>Deserializes, normalizes, and verifies a current-version canonical explain artifact.</summary>
    /// <param name="json">Persisted canonical explain JSON.</param>
    /// <returns>A normalized explain artifact with verified stage affinity and fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="json"/> is empty or white space.</exception>
    /// <exception cref="JsonException">
    /// JSON is malformed, duplicated, incomplete, uses an unsupported schema, violates stage affinity, or has a
    /// stale fingerprint.
    /// </exception>
    /// <exception cref="NotSupportedException">The artifact contains an unsupported serialization type.</exception>
    public static RelationQueryExplainArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("A relation/query explain artifact must be a JSON object.");
        if (StrictDocumentJson.TryFindDuplicateProperty(parsed.RootElement, string.Empty, out var duplicate))
            throw new JsonException($"Canonical explain JSON contains duplicate property '{duplicate}'.");
        if (!parsed.RootElement.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(version.GetString(), RelationQueryExplainArtifact.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException("A relation/query explain artifact must declare the supported schemaVersion.");
        }
        if (!parsed.RootElement.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
            throw new JsonException("A relation/query explain artifact must contain its stages array.");
        if (!parsed.RootElement.TryGetProperty("diagnostics", out var diagnostics)
            || diagnostics.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("A relation/query explain artifact must contain its diagnostics array.");
        }
        if (!parsed.RootElement.TryGetProperty("capabilitySummary", out var capabilitySummary)
            || capabilitySummary.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new JsonException("A relation/query explain artifact must contain its derived capabilitySummary.");
        }
        if (!parsed.RootElement.TryGetProperty("fingerprint", out var fingerprint)
            || fingerprint.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A relation/query explain artifact must contain its canonical fingerprint.");
        }

        try
        {
            return JsonSerializer.Deserialize<RelationQueryExplainArtifact>(json, CreateOptions())
                ?? throw new JsonException("Explain JSON deserialized to null.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new JsonException(exception.Message, exception);
        }
    }

    internal static JsonSerializerOptions CreateOptions(bool indented = false) =>
        RelationQueryJsonSerializer.CreateOptions(indented);
}
