using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Runtime;

/// <summary>Stable semantic coordinates owned by portable Process execution-trace artifacts.</summary>
public static class ProcessExecutionTraceWireNames
{
    /// <summary>Authority that owns the Process trace artifact contract.</summary>
    public const string SemanticAuthority = "cohesive.processes.execution-traces";

    /// <summary>Canonical read action used by transport-neutral observation surfaces.</summary>
    public const string Read = "traces";

    /// <summary>Semantic path of the canonical retained-trace query.</summary>
    public static ExecutionSemanticPath QueryPath { get; } = new(["queries", Read]);
}

/// <summary>Portable retained normalized Process traces with explicit pre-retention coverage evidence.</summary>
/// <remarks>
/// This artifact contains only canonical Process identity and trace evidence. Physical repository or execution-engine
/// keys belong to acquisition adapters and must not be projected into this contract.
/// </remarks>
public sealed record ProcessExecutionTraceArtifact
{
    /// <summary>Current portable Process trace-artifact schema.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-process-execution-traces/v1");

    /// <summary>Creates one portable Process trace artifact.</summary>
    /// <param name="schemaVersion">Exact Process trace-artifact schema.</param>
    /// <param name="definition">Exact canonical Process definition.</param>
    /// <param name="processInstanceId">Canonical logical Process instance identity.</param>
    /// <param name="missingTracePrefixCount">
    /// Number of earliest activation-evidence entries that predate normalized trace retention.
    /// </param>
    /// <param name="traces">Retained normalized traces in canonical activation order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Schema, identity, trace schema, definition, continuation, or activation evidence is invalid or contradictory.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="missingTracePrefixCount"/> is negative.</exception>
    /// <exception cref="OverflowException">Missing and retained activation-evidence counts exceed the supported range.</exception>
    [JsonConstructor]
    public ProcessExecutionTraceArtifact(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionReference definition,
        ProcessInstanceId processInstanceId,
        int missingTracePrefixCount,
        ImmutableArray<NormalizedExecutionTrace> traces = default)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported Process execution-trace artifact schema.", nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
        {
            throw new ArgumentException("A trace artifact requires a logical Process instance identity.", nameof(processInstanceId));
        }

        if (missingTracePrefixCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(missingTracePrefixCount),
                missingTracePrefixCount,
                "A missing trace-prefix count cannot be negative.");
        }

        var normalized = traces.IsDefault ? ImmutableArray<NormalizedExecutionTrace>.Empty : traces;
        _ = checked(missingTracePrefixCount + normalized.Length);
        HashSet<(ProcessAttemptId Attempt, ActivationId Activation)> identities = [];
        foreach (var trace in normalized)
        {
            if (trace is null
                || trace.SchemaVersion != NormalizedExecutionTrace.CurrentSchemaVersion
                || trace.Kind != ProcessDefinitionDocuments.Kind
                || trace.Definition != definition
                || trace.Continuation is not { } continuation
                || continuation.ProcessInstanceId != processInstanceId)
            {
                throw new ArgumentException(
                    "Every retained trace must use the current Process trace schema and identify the artifact's exact definition and logical instance.",
                    nameof(traces));
            }
            if (!identities.Add((continuation.ProcessAttemptId, trace.Activation)))
            {
                throw new ArgumentException(
                    "A Process trace artifact cannot repeat an activation within one attempt.",
                    nameof(traces));
            }
        }

        SchemaVersion = schemaVersion;
        Definition = definition;
        ProcessInstanceId = processInstanceId;
        MissingTracePrefixCount = missingTracePrefixCount;
        Traces = normalized;
    }

    /// <summary>Exact portable Process trace-artifact schema.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Exact canonical Process definition shared by every retained trace.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Canonical logical Process instance identity shared by every retained trace.</summary>
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Number of earliest activation-evidence entries without a retained normalized trace.</summary>
    public int MissingTracePrefixCount { get; }

    /// <summary>Retained payload-safe normalized traces in canonical activation order.</summary>
    public ImmutableArray<NormalizedExecutionTrace> Traces { get; }

    /// <summary>Whether every retained activation-evidence entry has a normalized trace.</summary>
    [JsonIgnore]
    public bool IsComplete => MissingTracePrefixCount == 0;

    /// <summary>Total activation-evidence inventory represented by missing-prefix evidence plus retained traces.</summary>
    [JsonIgnore]
    public int ActivationEvidenceCount => checked(MissingTracePrefixCount + Traces.Length);
}

/// <summary>Strict deterministic JSON boundary for portable Process execution-trace artifacts.</summary>
public static class ProcessExecutionTraceJsonSerializer
{
    /// <summary>Creates strict serializer options for the Process trace-artifact wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict, case-sensitive portable-document options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one verified Process trace artifact.</summary>
    /// <param name="artifact">Artifact to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable Process trace-artifact JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Trace content cannot be materialized as JSON.</exception>
    /// <exception cref="JsonException">Trace content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Trace content contains an unsupported serialization type.</exception>
    public static string Serialize(
        ProcessExecutionTraceArtifact artifact,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(artifact))
            : JsonSerializer.Serialize(artifact, CreateOptions(formatting));
    }

    /// <summary>Gets canonical UTF-8 JSON for one complete Process trace artifact.</summary>
    /// <param name="artifact">Artifact to serialize.</param>
    /// <returns>Canonical UTF-8 JSON containing only portable canonical trace evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Trace content cannot be materialized as JSON.</exception>
    /// <exception cref="JsonException">Trace content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Trace content contains an unsupported serialization type.</exception>
    public static byte[] GetCanonicalBytes(ProcessExecutionTraceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return CanonicalJsonWriter.GetCanonicalBytes(
            ToJsonObject(artifact),
            CreateOptions(),
            static _ => CanonicalJsonArrayOrdering.Sequence,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);
    }

    /// <summary>Deserializes and verifies one current-version Process trace artifact.</summary>
    /// <param name="json">Persisted Process trace-artifact JSON.</param>
    /// <returns>The validated portable artifact.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="json"/> is empty or white space.</exception>
    /// <exception cref="JsonException">JSON is malformed, duplicated, inconsistent, or uses an unsupported schema.</exception>
    /// <exception cref="NotSupportedException">Trace content contains an unsupported serialization type.</exception>
    public static ProcessExecutionTraceArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Process execution-trace artifact must be a JSON object.");
        }

        if (StrictDocumentJson.TryFindDuplicateProperty(parsed.RootElement, string.Empty, out var duplicate))
        {
            throw new JsonException($"Process execution-trace JSON contains duplicate property '{duplicate}'.");
        }

        try
        {
            var artifact = JsonSerializer.Deserialize<ProcessExecutionTraceArtifact>(json, CreateOptions())
                ?? throw new JsonException("Process execution-trace JSON produced no artifact.");
            var canonical = Serialize(artifact);
            using var canonicalDocument = JsonDocument.Parse(canonical);
            if (!JsonElement.DeepEquals(parsed.RootElement, canonicalDocument.RootElement))
            {
                throw new JsonException("Process execution-trace JSON is not in normalized wire form.");
            }

            return artifact;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new JsonException(exception.Message, exception);
        }
    }

    static JsonObject ToJsonObject(ProcessExecutionTraceArtifact artifact) =>
        JsonSerializer.SerializeToNode(artifact, CreateOptions()) as JsonObject
        ?? throw new InvalidOperationException("Failed to materialize Process execution-trace JSON.");
}
