using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Strict canonical JSON serialization for portable materialization definitions.</summary>
public static class MaterializationJsonSerializer
{
    const string DocumentReadStage = "materialization-document-read";
    const string DocumentValidationStage = "materialization-document-validation";
    const string DocumentSubject = "materialization-document";

    /// <summary>Creates strict JSON options including every canonical Relations converter used by an embedded request.</summary>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Strict case-sensitive closed-contract serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        if (!Enum.IsDefined(formatting))
        {
            throw new ArgumentOutOfRangeException(
                nameof(formatting),
                formatting,
                "Unsupported portable-document JSON formatting.");
        }

        return RelationQueryJsonSerializer.CreateOptions(formatting == PortableDocumentJsonFormatting.Indented);
    }

    /// <summary>Serializes a validated current-version materialization document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Deterministic materialization document JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The document fails schema, semantic, plan-link, or fingerprint validation.</exception>
    /// <exception cref="JsonException">The document cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical JSON representation.</exception>
    public static string Serialize(
        MaterializationDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validation = ValidateDocument(document);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join(" ", validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(document));
        }

        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(document))
            : JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Gets the unique canonical compact UTF-8 representation of a validated document.</summary>
    /// <param name="document">Document to encode.</param>
    /// <returns>Canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The document fails schema, semantic, plan-link, or fingerprint validation.</exception>
    /// <exception cref="JsonException">The document cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(MaterializationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validation = ValidateDocument(document);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join(" ", validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(document));
        }

        return StrictDocumentJson.GetCanonicalBytes(document, CreateOptions());
    }

    /// <summary>Deserializes and validates one current-version canonical materialization document.</summary>
    /// <param name="json">Persisted document JSON.</param>
    /// <returns>The exact validated materialization document.</returns>
    /// <exception cref="JsonException">The wire, schema, semantic linkage, or fingerprint is invalid.</exception>
    public static MaterializationDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var document);
        if (validation.IsValid && document is not null)
        {
            return document;
        }

        throw new JsonException(
            validation.Diagnostics.IsDefaultOrEmpty
                ? "Failed to deserialize a materialization document."
                : string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    /// <summary>Strictly reads, links, and validates one canonical materialization document.</summary>
    /// <param name="json">Persisted document JSON.</param>
    /// <param name="document">Typed document when wire projection succeeds, even if later validation fails.</param>
    /// <returns>Structured deterministic wire, schema, linkage, and fingerprint diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(string json, out MaterializationDocument? document)
    {
        document = null;
        if (TryReadUnsupportedSchemaVersion(json, out var unsupportedVersion))
        {
            return unsupportedVersion;
        }

        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                "materialization document",
                out MaterializationDocument? parsed,
                out var wireError))
        {
            return MaterializationContract.ErrorResult(
                $"materialization.json.{WireCode(wireError.Failure)}",
                wireError.Message,
                wireError.Location,
                DocumentReadStage,
                DocumentSubject,
                [MaterializationDocument.CurrentSchemaVersion],
                "canonical closed-contract materialization JSON",
                wireError.Failure.ToString());
        }

        document = parsed;
        try
        {
            return ValidateDocument(parsed!);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or JsonException)
        {
            return MaterializationContract.ErrorResult(
                "materialization.document.validationFailed",
                exception.Message,
                "$",
                DocumentValidationStage,
                DocumentSubject,
                [MaterializationDocument.CurrentSchemaVersion],
                "valid current-version materialization document",
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    static bool TryReadUnsupportedSchemaVersion(
        string json,
        out DocumentValidationResult validation)
    {
        validation = DocumentValidationResult.Valid;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || StrictDocumentJson.TryFindDuplicateProperty(root, string.Empty, out _)
                || !root.TryGetProperty("schemaVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var version = versionElement.GetString();
            if (string.Equals(version, MaterializationDocument.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return false;
            }

            var observed = string.IsNullOrEmpty(version) ? "<empty>" : version;
            validation = MaterializationContract.ErrorResult(
                "materialization.schemaVersion.unsupported",
                $"Unsupported materialization schema version '{version}'.",
                "/schemaVersion",
                DocumentReadStage,
                DocumentSubject,
                [MaterializationDocument.CurrentSchemaVersion],
                MaterializationDocument.CurrentSchemaVersion,
                observed);
            return true;
        }
    }

    static DocumentValidationResult ValidateDocument(MaterializationDocument document)
    {
        List<DocumentValidationDiagnostic> diagnostics = [];
        if (!string.Equals(
                document.SchemaVersion,
                MaterializationDocument.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            diagnostics.Add(MaterializationContract.CreateDiagnostic(
                "materialization.schemaVersion.unsupported",
                DiagnosticSeverity.Error,
                $"Unsupported materialization schema version '{document.SchemaVersion}'.",
                "/schemaVersion",
                DocumentValidationStage,
                document.Definition.Id.Value,
                [document.Definition.Provenance.Source.Reference],
                MaterializationDocument.CurrentSchemaVersion,
                document.SchemaVersion));
        }

        if (!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.AddRange(MaterializationDefinitionValidator.Validate(document.Definition).Diagnostics);
        }

        var computed = MaterializationDefinitionFingerprinter.Compute(document.Definition);
        if (!Equals(computed, document.DefinitionFingerprint))
        {
            diagnostics.Add(MaterializationContract.CreateDiagnostic(
                "materialization.fingerprint.mismatch",
                DiagnosticSeverity.Error,
                "The persisted materialization definition fingerprint does not match canonical content.",
                "/definitionFingerprint",
                DocumentValidationStage,
                document.Definition.Id.Value,
                [document.Definition.Provenance.Source.Reference],
                computed.Value,
                document.DefinitionFingerprint.Value));
        }

        var normalized = MaterializationContract.NormalizeDiagnostics(
            [.. diagnostics.Distinct()],
            nameof(diagnostics));
        return normalized.IsDefaultOrEmpty
            ? DocumentValidationResult.Valid
            : new DocumentValidationResult(normalized);
    }

    static string WireCode(StrictDocumentJsonReadFailure failure) => failure switch
    {
        StrictDocumentJsonReadFailure.Empty => "empty",
        StrictDocumentJsonReadFailure.InvalidJson => "invalid",
        StrictDocumentJsonReadFailure.RootInvalid => "rootInvalid",
        StrictDocumentJsonReadFailure.DuplicateProperty => "duplicateProperty",
        StrictDocumentJsonReadFailure.DeserializationInvalid => "deserializationInvalid",
        StrictDocumentJsonReadFailure.DeserializationNull => "deserializationNull",
        StrictDocumentJsonReadFailure.WireNonCanonical => "nonCanonical",
        _ => "unknown"
    };
}
