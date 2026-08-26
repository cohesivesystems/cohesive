using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;

namespace Cohesive.Relations.Serialization;

/// <summary>Strict JSON serialization for portable canonical relation/query evaluations.</summary>
public static class RelationQueryEvaluationJsonSerializer
{
    /// <summary>Serializes one verified canonical relation/query evaluation.</summary>
    /// <param name="evaluation">Evaluation to serialize.</param>
    /// <param name="indented">Whether the persisted JSON should be indented.</param>
    /// <returns>Deterministic portable evaluation JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="evaluation"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The evaluation cannot be written as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">The evaluation contains an unsupported serialization type.</exception>
    public static string Serialize(RelationQueryEvaluation evaluation, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return JsonSerializer.Serialize(evaluation, CreateOptions(indented));
    }

    /// <summary>
    /// Deserializes, normalizes, and verifies one current-version canonical relation/query evaluation.
    /// </summary>
    /// <param name="json">Persisted canonical evaluation JSON.</param>
    /// <returns>A verified current-version evaluation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="json"/> is empty or white space.</exception>
    /// <exception cref="JsonException">
    /// JSON is malformed, duplicated, incomplete, from an unsupported schema version, semantically invalid, or has
    /// a stale fingerprint.
    /// </exception>
    /// <exception cref="NotSupportedException">The evaluation contains an unsupported serialization type.</exception>
    public static RelationQueryEvaluation Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("A relation/query evaluation must be a JSON object.");
        if (StrictDocumentJson.TryFindDuplicateProperty(parsed.RootElement, string.Empty, out var duplicate))
            throw new JsonException($"Canonical evaluation JSON contains duplicate property '{duplicate}'.");
        if (!parsed.RootElement.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(
                version.GetString(),
                RelationQueryEvaluation.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new JsonException("A relation/query evaluation must declare the supported schemaVersion.");
        }
        if (!parsed.RootElement.TryGetProperty("fingerprint", out _))
            throw new JsonException("A relation/query evaluation must contain its canonical fingerprint.");

        try
        {
            return JsonSerializer.Deserialize<RelationQueryEvaluation>(json, CreateOptions())
                ?? throw new JsonException("Evaluation JSON deserialized to null.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new JsonException(exception.Message, exception);
        }
    }

    internal static JsonSerializerOptions CreateOptions(bool indented = false)
    {
        var options = RelationQueryJsonSerializer.CreateOptions(indented);
        return options;
    }
}
