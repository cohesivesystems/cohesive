using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;

namespace Cohesive.Storage.Processes;

/// <summary>Stable diagnostics emitted while reading durable Process-checkpoint JSON.</summary>
public static class ProcessCheckpointJsonDiagnosticCodes
{
    /// <summary>The supplied checkpoint JSON is empty.</summary>
    public const string JsonEmpty = "storage.processes.checkpoint.json.empty";

    /// <summary>The supplied checkpoint text is not valid JSON.</summary>
    public const string JsonInvalid = "storage.processes.checkpoint.json.invalid";

    /// <summary>The checkpoint root is not a JSON object.</summary>
    public const string RootInvalid = "storage.processes.checkpoint.root.invalid";

    /// <summary>The checkpoint contains a duplicate JSON object property.</summary>
    public const string DuplicateProperty = "storage.processes.checkpoint.json.duplicateProperty";

    /// <summary>The checkpoint cannot be deserialized under its closed portable contract.</summary>
    public const string DeserializationInvalid = "storage.processes.checkpoint.deserialize.invalid";

    /// <summary>JSON deserialization unexpectedly produced a null checkpoint.</summary>
    public const string DeserializationNull = "storage.processes.checkpoint.deserialize.null";

    /// <summary>The JSON differs from the unique canonical typed checkpoint representation.</summary>
    public const string WireNonCanonical = "storage.processes.checkpoint.wire.nonCanonical";
}

/// <summary>Strict canonical JSON serialization and recovery admission for durable Process checkpoints.</summary>
public static class ProcessDurableCheckpointJsonSerializer
{
    /// <summary>Creates strict serializer options for the durable Process-checkpoint wire.</summary>
    /// <param name="formatting">Compact or human-readable JSON formatting.</param>
    /// <returns>Case-sensitive strict portable-document serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes a complete durable Process checkpoint.</summary>
    /// <param name="checkpoint">Complete validated physical checkpoint.</param>
    /// <param name="formatting">Compact canonical or human-readable JSON formatting.</param>
    /// <returns>Strict versioned checkpoint JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The checkpoint violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The checkpoint contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The checkpoint has no canonical JSON encoding.</exception>
    public static string Serialize(
        ProcessDurableCheckpoint checkpoint,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(checkpoint))
            : JsonSerializer.Serialize(checkpoint, CreateOptions(formatting));
    }

    /// <summary>Gets deterministic canonical UTF-8 JSON for one durable Process checkpoint.</summary>
    /// <param name="checkpoint">Complete validated physical checkpoint.</param>
    /// <returns>Canonical checkpoint bytes suitable for content addressing and exact persistence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The checkpoint violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The checkpoint contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The checkpoint has no canonical JSON encoding.</exception>
    public static byte[] GetCanonicalBytes(ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return StrictDocumentJson.GetCanonicalBytes(checkpoint, CreateOptions());
    }

    /// <summary>Deserializes and admits a checkpoint against one exact compiled Process definition.</summary>
    /// <param name="json">Persisted canonical checkpoint JSON.</param>
    /// <param name="plan">Exact compiled Process plan selected for recovery.</param>
    /// <returns>A compatible complete checkpoint that is safe to present to the Process interpreter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is malformed, noncanonical, or incompatible with the plan.</exception>
    public static ProcessDurableCheckpoint Deserialize(string json, CompiledProcessPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validation = TryDeserialize(json, plan, out var checkpoint);
        if (validation.IsValid && checkpoint is not null)
        {
            return checkpoint;
        }

        throw ValidationException(validation);
    }

    /// <summary>Attempts strict checkpoint deserialization and exact pre-execution compatibility validation.</summary>
    /// <param name="json">Persisted canonical checkpoint JSON.</param>
    /// <param name="plan">Exact compiled Process plan selected for recovery.</param>
    /// <param name="checkpoint">
    /// Parsed checkpoint when strict deserialization succeeds, even when compatibility validation rejects it.
    /// </param>
    /// <returns>Deterministically ordered wire, definition, and restored-state diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        CompiledProcessPlan plan,
        out ProcessDurableCheckpoint? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                "durable Process checkpoint",
                out checkpoint,
                out var error))
        {
            return Error(DiagnosticCode(error.Failure), error.Message, error.Location);
        }

        return ProcessCheckpointCompatibilityValidator.Validate(plan, checkpoint!);
    }

    static string DiagnosticCode(StrictDocumentJsonReadFailure failure) =>
        failure switch
        {
            StrictDocumentJsonReadFailure.Empty => ProcessCheckpointJsonDiagnosticCodes.JsonEmpty,
            StrictDocumentJsonReadFailure.InvalidJson => ProcessCheckpointJsonDiagnosticCodes.JsonInvalid,
            StrictDocumentJsonReadFailure.RootInvalid => ProcessCheckpointJsonDiagnosticCodes.RootInvalid,
            StrictDocumentJsonReadFailure.DuplicateProperty =>
                ProcessCheckpointJsonDiagnosticCodes.DuplicateProperty,
            StrictDocumentJsonReadFailure.DeserializationInvalid =>
                ProcessCheckpointJsonDiagnosticCodes.DeserializationInvalid,
            StrictDocumentJsonReadFailure.DeserializationNull =>
                ProcessCheckpointJsonDiagnosticCodes.DeserializationNull,
            StrictDocumentJsonReadFailure.WireNonCanonical =>
                ProcessCheckpointJsonDiagnosticCodes.WireNonCanonical,
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown strict JSON read failure.")
        };

    static JsonException ValidationException(DocumentValidationResult validation)
    {
        var diagnostic = validation.Diagnostics.FirstOrDefault();
        return diagnostic is null
            ? new JsonException("Durable Process-checkpoint validation failed.")
            : new JsonException($"{diagnostic.Code} at {diagnostic.Location ?? "$"}: {diagnostic.Message}");
    }

    static DocumentValidationResult Error(string code, string message, string location) =>
        DocumentValidationResult.FromDiagnostics([
            new(code, DiagnosticSeverity.Error, message, location)
        ]);
}
