using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted while reading canonical interaction-envelope JSON.</summary>
public static class InteractionEnvelopeJsonDiagnosticCodes
{
    /// <summary>The supplied interaction JSON is empty.</summary>
    public const string JsonEmpty = "interactions.envelope.json.empty";

    /// <summary>The supplied text is not valid JSON.</summary>
    public const string JsonInvalid = "interactions.envelope.json.invalid";

    /// <summary>The interaction root is not a JSON object.</summary>
    public const string RootInvalid = "interactions.envelope.root.invalid";

    /// <summary>The interaction document contains a duplicate JSON object property.</summary>
    public const string DuplicateProperty = "interactions.envelope.json.duplicateProperty";

    /// <summary>The interaction cannot be deserialized under the closed portable contract.</summary>
    public const string DeserializationInvalid = "interactions.envelope.deserialize.invalid";

    /// <summary>JSON deserialization unexpectedly produced a null envelope.</summary>
    public const string DeserializationNull = "interactions.envelope.deserialize.null";

    /// <summary>The supplied JSON differs from the unique canonical typed interaction wire representation.</summary>
    public const string WireNonCanonical = "interactions.envelope.wire.nonCanonical";
}

/// <summary>Strict versioned JSON serialization and admission for canonical interaction envelopes.</summary>
public static class InteractionEnvelopeJsonSerializer
{
    static readonly ExecutionIrSchemaCompatibilityDeclaration CurrentSchemaCompatibility =
        new([InteractionEnvelope.CurrentSchemaVersion]);

    /// <summary>Creates strict serializer options for the closed interaction-envelope wire contract.</summary>
    /// <param name="formatting">Compact or human-readable JSON formatting.</param>
    /// <returns>Case-sensitive strict portable-document serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes a canonical interaction envelope.</summary>
    /// <param name="envelope">Envelope to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output formatting.</param>
    /// <returns>Strict versioned interaction-envelope JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The envelope violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The envelope contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The envelope has no canonical JSON encoding.</exception>
    public static string Serialize(
        InteractionEnvelope envelope,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(envelope))
            : JsonSerializer.Serialize(envelope, CreateOptions(formatting));
    }

    /// <summary>Gets deterministic canonical UTF-8 JSON for one interaction envelope.</summary>
    /// <param name="envelope">Envelope to encode.</param>
    /// <returns>Canonical exact-decimal JSON bytes preserving all semantic runtime evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The envelope violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The envelope contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The envelope has no canonical JSON encoding.</exception>
    public static byte[] GetCanonicalBytes(InteractionEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return StrictDocumentJson.GetCanonicalBytes<InteractionEnvelope>(envelope, CreateOptions());
    }

    /// <summary>Deserializes and contract-links a current-version interaction envelope.</summary>
    /// <param name="json">Persisted interaction-envelope JSON.</param>
    /// <param name="contracts">Exact canonical contract catalog used for payload and Reply validation.</param>
    /// <returns>A portable, contract-linked current-version interaction envelope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contracts"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is malformed or the envelope fails validation.</exception>
    public static InteractionEnvelope Deserialize(string json, InteractionContractCatalog contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        var validation = TryDeserialize(json, contracts, out var envelope);
        if (validation.IsValid && envelope is not null)
        {
            return envelope;
        }

        throw ValidationException(validation);
    }

    /// <summary>Deserializes and contract-links an interaction envelope using contextual named types.</summary>
    /// <param name="json">Persisted interaction-envelope JSON.</param>
    /// <param name="contracts">Exact canonical contract catalog used for payload and Reply validation.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified portable values.</param>
    /// <returns>A portable, contract-linked current-version interaction envelope.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contracts"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">The JSON is malformed or the envelope fails validation.</exception>
    public static InteractionEnvelope Deserialize(
        string json,
        InteractionContractCatalog contracts,
        ShapeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(graph);
        var validation = TryDeserialize(json, contracts, graph, out var envelope);
        if (validation.IsValid && envelope is not null)
        {
            return envelope;
        }

        throw ValidationException(validation);
    }

    /// <summary>Attempts to read and link a current-version interaction envelope.</summary>
    /// <param name="json">Persisted interaction-envelope JSON.</param>
    /// <param name="contracts">Exact canonical contract catalog used for link validation.</param>
    /// <param name="envelope">Parsed envelope when strict deserialization succeeds, even if validation fails.</param>
    /// <returns>Deterministically ordered read, linking, and portability diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contracts"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        InteractionContractCatalog contracts,
        out InteractionEnvelope? envelope)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        return TryDeserializeCore(json, CurrentSchemaCompatibility, contracts, graph: null, out envelope);
    }

    /// <summary>Attempts to read and link an envelope using contextual named portable types.</summary>
    /// <param name="json">Persisted interaction-envelope JSON.</param>
    /// <param name="contracts">Exact canonical contract catalog used for link validation.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified portable values.</param>
    /// <param name="envelope">Parsed envelope when strict deserialization succeeds, even if validation fails.</param>
    /// <returns>Deterministically ordered read, linking, and portability diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contracts"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        InteractionContractCatalog contracts,
        ShapeGraph graph,
        out InteractionEnvelope? envelope)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(graph);
        return TryDeserializeCore(json, CurrentSchemaCompatibility, contracts, graph, out envelope);
    }

    /// <summary>Attempts to read an envelope against explicit exact schema and contract compatibility.</summary>
    /// <param name="json">Persisted interaction-envelope JSON.</param>
    /// <param name="schemaCompatibility">Exact envelope schema versions admitted by the interpreter.</param>
    /// <param name="contracts">Exact canonical contract catalog used for link validation.</param>
    /// <param name="graph">Optional shape graph used to resolve named and qualified portable values.</param>
    /// <param name="envelope">Parsed envelope when strict deserialization succeeds, even if validation fails.</param>
    /// <returns>Deterministically ordered read, compatibility, linking, and portability diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaCompatibility"/> or <paramref name="contracts"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        ExecutionIrSchemaCompatibilityDeclaration schemaCompatibility,
        InteractionContractCatalog contracts,
        ShapeGraph? graph,
        out InteractionEnvelope? envelope)
    {
        ArgumentNullException.ThrowIfNull(schemaCompatibility);
        ArgumentNullException.ThrowIfNull(contracts);
        return TryDeserializeCore(json, schemaCompatibility, contracts, graph, out envelope);
    }

    static DocumentValidationResult TryDeserializeCore(
        string json,
        ExecutionIrSchemaCompatibilityDeclaration schemaCompatibility,
        InteractionContractCatalog contracts,
        ShapeGraph? graph,
        out InteractionEnvelope? envelope)
    {
        var unsupportedSchema = ValidateImplementedSchemaBeforeTypedRead(json);
        if (unsupportedSchema is not null)
        {
            envelope = null;
            return unsupportedSchema;
        }

        if (!StrictDocumentJson.TryReadCanonicalObject<InteractionEnvelope>(
                json,
                CreateOptions(),
                "interaction envelope",
                out envelope,
                out var error))
        {
            return Error(
                DiagnosticCode(error.Failure),
                error.Message,
                error.Location);
        }

        return InteractionEnvelopeValidator.Validate(envelope!, contracts, graph, schemaCompatibility);
    }

    static DocumentValidationResult? ValidateImplementedSchemaBeforeTypedRead(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || StrictDocumentJson.TryFindDuplicateProperty(root, string.Empty, out _)
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.String
                || schemaVersion.GetString() is not { } version
                || string.IsNullOrWhiteSpace(version)
                || CurrentSchemaCompatibility.Supports(new(version)))
            {
                return null;
            }

            return Error(
                InteractionEnvelopeDiagnosticCodes.SchemaVersionUnsupported,
                $"Interaction envelope schema '{version}' is not implemented by this reader.",
                "/schemaVersion");
        }
    }

    static string DiagnosticCode(StrictDocumentJsonReadFailure failure) =>
        failure switch
        {
            StrictDocumentJsonReadFailure.Empty => InteractionEnvelopeJsonDiagnosticCodes.JsonEmpty,
            StrictDocumentJsonReadFailure.InvalidJson => InteractionEnvelopeJsonDiagnosticCodes.JsonInvalid,
            StrictDocumentJsonReadFailure.RootInvalid => InteractionEnvelopeJsonDiagnosticCodes.RootInvalid,
            StrictDocumentJsonReadFailure.DuplicateProperty =>
                InteractionEnvelopeJsonDiagnosticCodes.DuplicateProperty,
            StrictDocumentJsonReadFailure.DeserializationInvalid =>
                InteractionEnvelopeJsonDiagnosticCodes.DeserializationInvalid,
            StrictDocumentJsonReadFailure.DeserializationNull =>
                InteractionEnvelopeJsonDiagnosticCodes.DeserializationNull,
            StrictDocumentJsonReadFailure.WireNonCanonical =>
                InteractionEnvelopeJsonDiagnosticCodes.WireNonCanonical,
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown strict JSON read failure.")
        };

    static JsonException ValidationException(DocumentValidationResult validation)
    {
        var diagnostic = validation.Diagnostics.FirstOrDefault();
        return diagnostic is null
            ? new JsonException("Interaction-envelope validation failed.")
            : new JsonException($"{diagnostic.Code} at {diagnostic.Location ?? "$"}: {diagnostic.Message}");
    }

    static DocumentValidationResult Error(string code, string message, string location) =>
        DocumentValidationResult.FromDiagnostics([
            new(code, DiagnosticSeverity.Error, message, location)
        ]);
}
