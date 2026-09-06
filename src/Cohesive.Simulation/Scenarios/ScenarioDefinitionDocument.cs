using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Scenarios;

/// <summary>Versioned deterministic identity of canonical scenario-definition content.</summary>
public sealed record ScenarioDefinitionFingerprint
{
    /// <summary>Cryptographic hash algorithm used by the current scenario-definition profile.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current scenario-definition fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-simulation-scenario/v1-c14n/v1";

    /// <summary>Creates scenario-definition fingerprint metadata.</summary>
    /// <param name="algorithm">Hash-algorithm identity.</param>
    /// <param name="canonicalization">Canonical scenario-definition profile identity.</param>
    /// <param name="value">Lowercase hexadecimal fingerprint value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white-space.</exception>
    [JsonConstructor]
    public ScenarioDefinitionFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Gets the hash-algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the canonical scenario-definition profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal fingerprint value.</summary>
    public string Value { get; }
}

/// <summary>Portable self-validating envelope for one canonical deterministic scenario.</summary>
/// <remarks>
/// Operation and actor declarations are normalized by identity. Scheduled actions are normalized by virtual UTC
/// instant and then identity, so wire order cannot retain a second non-semantic authoring order.
/// </remarks>
public sealed record ScenarioDefinitionDocument
{
    /// <summary>Current portable scenario-definition document schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-scenario/v1";

    /// <summary>Creates or restores one portable scenario-definition document.</summary>
    /// <param name="schemaVersion">Exact portable scenario-definition schema.</param>
    /// <param name="definition">Canonical provider-neutral scenario definition.</param>
    /// <param name="fingerprint">Persisted fingerprint of the exact semantic content.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="definition"/>, or <paramref name="fingerprint"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported, the definition does not compile, or the fingerprint does not match current
    /// canonical semantic content.
    /// </exception>
    [JsonConstructor]
    public ScenarioDefinitionDocument(
        string schemaVersion,
        ScenarioDefinition definition,
        ScenarioDefinitionFingerprint fingerprint)
        : this(ValidateAndNormalize(schemaVersion, definition, fingerprint))
    {
    }

    ScenarioDefinitionDocument(
        (string SchemaVersion, ScenarioDefinition Definition, ScenarioDefinitionFingerprint Fingerprint) state)
    {
        SchemaVersion = state.SchemaVersion;
        Definition = state.Definition;
        Fingerprint = state.Fingerprint;
    }

    /// <summary>Gets the exact portable scenario-definition schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the normalized provider-neutral scenario definition.</summary>
    public ScenarioDefinition Definition { get; }

    /// <summary>Gets the fingerprint of exact scenario semantic content.</summary>
    public ScenarioDefinitionFingerprint Fingerprint { get; }

    /// <summary>Creates a current-version portable document from one valid scenario definition.</summary>
    /// <param name="definition">Scenario definition to validate, normalize, and persist.</param>
    /// <returns>A current-version document with a computed semantic fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> does not compile successfully.</exception>
    public static ScenarioDefinitionDocument FromDefinition(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(CreateState(RequirePlan(definition)));
    }

    /// <summary>Compiles the validated document into an immutable normalized scenario schedule.</summary>
    /// <returns>A plan for the exact persisted scenario definition.</returns>
    /// <exception cref="InvalidOperationException">
    /// The retained definition no longer satisfies the document construction invariant.
    /// </exception>
    public CompiledScenarioPlan Compile()
    {
        var result = ScenarioCompiler.Compile(Definition);
        return result.Plan ?? throw new InvalidOperationException(
            $"Validated scenario-definition document '{Definition.Id}' could not be recompiled.");
    }

    static (
        string SchemaVersion,
        ScenarioDefinition Definition,
        ScenarioDefinitionFingerprint Fingerprint) ValidateAndNormalize(
        string schemaVersion,
        ScenarioDefinition definition,
        ScenarioDefinitionFingerprint fingerprint)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Scenario-definition schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(fingerprint);
        var state = CreateState(RequirePlan(definition));
        if (fingerprint != state.Fingerprint)
        {
            throw new ArgumentException(
                "The supplied scenario-definition fingerprint does not match canonical semantic content.",
                nameof(fingerprint));
        }

        return state;
    }

    static (
        string SchemaVersion,
        ScenarioDefinition Definition,
        ScenarioDefinitionFingerprint Fingerprint) CreateState(CompiledScenarioPlan plan) =>
        (
            CurrentSchemaVersion,
            plan.Definition,
            new(
                plan.FingerprintAlgorithm,
                plan.FingerprintCanonicalization,
                plan.Fingerprint));

    static CompiledScenarioPlan RequirePlan(ScenarioDefinition definition)
    {
        var result = ScenarioCompiler.Compile(definition);
        if (result.Plan is not null)
            return result.Plan;

        var errors = result.Validation.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}");
        throw new ArgumentException(
            $"Scenario definition does not compile: {string.Join(" | ", errors)}",
            nameof(definition));
    }
}

/// <summary>Strict deterministic JSON boundary for portable scenario-definition documents.</summary>
public static class ScenarioDefinitionJsonSerializer
{
    const string ContractName = "scenario-definition document";

    /// <summary>Creates strict serializer options for the closed scenario-definition wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict case-sensitive portable-document options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one verified portable scenario-definition document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable scenario-definition JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Document content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Document content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Document content contains an unsupported runtime type.</exception>
    public static string Serialize(
        ScenarioDefinitionDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(document);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(document))
            : JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Validates, normalizes, and serializes one canonical scenario definition.</summary>
    /// <param name="definition">Scenario definition to persist.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable scenario-definition JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> does not compile successfully.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Definition content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime type.</exception>
    public static string Serialize(
        ScenarioDefinition definition,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        Serialize(ScenarioDefinitionDocument.FromDefinition(definition), formatting);

    /// <summary>Gets canonical UTF-8 JSON for one complete scenario-definition document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Document content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Document content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Document content contains an unsupported runtime type.</exception>
    public static byte[] GetCanonicalBytes(ScenarioDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictDocumentJson.GetCanonicalBytes(document, CreateOptions());
    }

    /// <summary>Deserializes and validates one current-version scenario-definition document.</summary>
    /// <param name="json">Persisted scenario-definition JSON.</param>
    /// <returns>A normalized fingerprint-verified document ready to compile.</returns>
    /// <exception cref="JsonException">
    /// JSON is empty, malformed, duplicated, noncanonical, unsupported, invalid, or fingerprint-inconsistent.
    /// </exception>
    public static ScenarioDefinitionDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var document);
        if (validation.IsValid && document is not null)
            return document;

        throw new JsonException(string.Join(
            " | ",
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}")));
    }

    /// <summary>Attempts to deserialize and validate one scenario-definition document.</summary>
    /// <param name="json">Persisted scenario-definition JSON.</param>
    /// <param name="document">Receives the validated document when successful; otherwise <see langword="null"/>.</param>
    /// <returns>Structured wire, schema, compilation, and fingerprint diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out ScenarioDefinitionDocument? document)
    {
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                ContractName,
                out document,
                out var error))
        {
            return DocumentValidationResult.Valid;
        }

        document = null;
        return StrictDocumentJson.Error(
            error.Failure switch
            {
                StrictDocumentJsonReadFailure.Empty => "simulation.scenario.document.jsonEmpty",
                StrictDocumentJsonReadFailure.InvalidJson => "simulation.scenario.document.jsonInvalid",
                StrictDocumentJsonReadFailure.RootInvalid => "simulation.scenario.document.rootInvalid",
                StrictDocumentJsonReadFailure.DuplicateProperty => "simulation.scenario.document.duplicateProperty",
                StrictDocumentJsonReadFailure.DeserializationInvalid => "simulation.scenario.document.contentInvalid",
                StrictDocumentJsonReadFailure.DeserializationNull => "simulation.scenario.document.contentMissing",
                StrictDocumentJsonReadFailure.WireNonCanonical => "simulation.scenario.document.wireNonCanonical",
                _ => "simulation.scenario.document.unknown"
            },
            error.Message,
            error.Location);
    }
}
