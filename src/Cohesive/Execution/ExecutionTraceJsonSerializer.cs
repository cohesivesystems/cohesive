using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Versioned cryptographic identity of one normalized semantic execution trace.</summary>
public sealed record ExecutionTraceFingerprint
{
    /// <summary>Creates an execution-trace fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonical trace profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public ExecutionTraceFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonical trace profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>Computes deterministic semantic identities for normalized execution traces.</summary>
public static class ExecutionTraceFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonical normalized-trace profile identifier.</summary>
    public const string Canonicalization = "cohesive-execution-trace/v2-c14n/v1";

    /// <summary>Computes the semantic fingerprint of one normalized execution trace.</summary>
    /// <remarks>
    /// The fingerprint excludes <see cref="NormalizedExecutionTrace.DurableCommitSequence"/> so the same
    /// semantic activation compares exactly between a reference interpreter and a durable interpreter. The
    /// complete persisted JSON retains that physical commit observation.
    /// </remarks>
    /// <param name="trace">Normalized trace to fingerprint.</param>
    /// <returns>A SHA-256 fingerprint over deterministic semantic trace evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The trace cannot be materialized as JSON.</exception>
    /// <exception cref="JsonException">The trace cannot be serialized as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">The trace contains an unsupported serialization type.</exception>
    public static ExecutionTraceFingerprint ComputeSemantic(NormalizedExecutionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var root = ExecutionTraceJsonSerializer.ToJsonObject(trace);
        root.Remove("durableCommitSequence");
        var canonical = CanonicalJsonWriter.GetCanonicalBytes(
            root,
            ExecutionTraceJsonSerializer.CreateOptions(),
            static _ => CanonicalJsonArrayOrdering.Sequence,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }
}

/// <summary>Strict deterministic JSON boundary for normalized execution traces.</summary>
public static class ExecutionTraceJsonSerializer
{
    /// <summary>Creates strict serializer options for the normalized execution-trace wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict, case-sensitive serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one normalized execution trace.</summary>
    /// <param name="trace">Trace to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Persisted normalized-trace JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">The trace has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">The trace violates its strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The trace contains an unsupported serialization type.</exception>
    public static string Serialize(
        NormalizedExecutionTrace trace,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(trace))
            : JsonSerializer.Serialize(trace, CreateOptions(formatting));
    }

    /// <summary>Gets canonical UTF-8 JSON for one complete normalized execution trace.</summary>
    /// <param name="trace">Trace to serialize.</param>
    /// <returns>Canonical UTF-8 JSON retaining semantic and optional durable-commit evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The trace has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">The trace violates its strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The trace contains an unsupported serialization type.</exception>
    public static byte[] GetCanonicalBytes(NormalizedExecutionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return CanonicalJsonWriter.GetCanonicalBytes(
            ToJsonObject(trace),
            CreateOptions(),
            static _ => CanonicalJsonArrayOrdering.Sequence,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);
    }

    /// <summary>Deserializes and validates one current-version normalized execution trace.</summary>
    /// <param name="json">Persisted trace JSON.</param>
    /// <returns>A structurally valid current-version normalized trace.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">JSON is invalid, noncanonical, structurally inconsistent, or unsupported.</exception>
    public static NormalizedExecutionTrace Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var trace = JsonSerializer.Deserialize<NormalizedExecutionTrace>(json, CreateOptions())
            ?? throw new JsonException("Normalized execution-trace JSON produced no document.");
        if (trace.SchemaVersion != NormalizedExecutionTrace.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported normalized execution-trace schema '{trace.SchemaVersion.Value}'.");
        }

        var canonical = Serialize(trace);
        using var suppliedDocument = JsonDocument.Parse(json);
        using var canonicalDocument = JsonDocument.Parse(canonical);
        if (!JsonElement.DeepEquals(suppliedDocument.RootElement, canonicalDocument.RootElement))
            throw new JsonException("Normalized execution-trace JSON is not in canonical wire form.");
        return trace;
    }

    internal static JsonObject ToJsonObject(NormalizedExecutionTrace trace) =>
        JsonSerializer.SerializeToNode(trace, CreateOptions()) as JsonObject
        ?? throw new InvalidOperationException("Failed to materialize normalized execution-trace JSON.");
}
