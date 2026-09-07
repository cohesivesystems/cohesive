using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Scenarios;

/// <summary>One portable outcome associated with an exact scheduled scenario action.</summary>
public sealed record ScenarioActionOutcome
{
    /// <summary>Creates a retained action outcome.</summary>
    /// <param name="actionId">Stable identity of the action that produced the outcome.</param>
    /// <param name="output">Portable output or semantic failure evidence returned by its interpreter.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="actionId"/> or <paramref name="output"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="actionId"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ScenarioActionOutcome(string actionId, PortableValue output)
    {
        ActionId = Guard.RequireNotNullOrWhiteSpace(actionId);
        Output = Guard.RequireNotNull(output);
    }

    /// <summary>Gets the stable identity of the action that produced the outcome.</summary>
    public string ActionId { get; }

    /// <summary>Gets the portable output or semantic failure evidence.</summary>
    public PortableValue Output { get; }
}

/// <summary>Versioned deterministic identity of exact retained scenario execution content.</summary>
public sealed record ScenarioExecutionTraceFingerprint
{
    /// <summary>Cryptographic hash algorithm used by the current trace profile.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current trace fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-simulation-scenario-trace/v1-c14n/v1";

    /// <summary>Creates scenario execution trace fingerprint metadata.</summary>
    /// <param name="algorithm">Hash-algorithm identity.</param>
    /// <param name="canonicalization">Canonical scenario-trace profile identity.</param>
    /// <param name="value">Lowercase hexadecimal fingerprint value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white-space.</exception>
    [JsonConstructor]
    public ScenarioExecutionTraceFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Gets the hash-algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the canonical scenario-trace profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal fingerprint value.</summary>
    public string Value { get; }
}

/// <summary>Portable self-validating record of one complete canonical scenario execution.</summary>
/// <remarks>
/// <see cref="Scenario"/> remains the complete source authority. Outcomes are required to correspond one-for-one to
/// its canonical schedule, and each output is revalidated against the selected operation contract. The interpreter
/// identity is retained as execution-policy attribution; executable handler code never enters the document.
/// </remarks>
public sealed record ScenarioExecutionTraceDocument
{
    /// <summary>Current portable scenario execution trace schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-scenario-trace/v1";

    /// <summary>Creates or restores one complete scenario execution trace.</summary>
    /// <param name="schemaVersion">Exact portable scenario-trace schema.</param>
    /// <param name="scenario">Exact fingerprint-verified scenario that was interpreted.</param>
    /// <param name="interpreter">Exact action-interpreter identity and version.</param>
    /// <param name="outcomes">One outcome per action in canonical execution order.</param>
    /// <param name="fingerprint">Persisted fingerprint of exact retained trace content.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="scenario"/>, <paramref name="interpreter"/>, or
    /// <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported; the interpreter is empty; outcomes are missing, reordered, duplicated, or invalid
    /// for their operation contracts; or the fingerprint does not match current canonical content.
    /// </exception>
    [JsonConstructor]
    public ScenarioExecutionTraceDocument(
        string schemaVersion,
        ScenarioDefinitionDocument scenario,
        string interpreter,
        ImmutableArray<ScenarioActionOutcome> outcomes,
        ScenarioExecutionTraceFingerprint fingerprint)
        : this(ValidateAndNormalize(schemaVersion, scenario, interpreter, outcomes, fingerprint))
    {
    }

    ScenarioExecutionTraceDocument((
        string SchemaVersion,
        ScenarioDefinitionDocument Scenario,
        string Interpreter,
        ImmutableArray<ScenarioActionOutcome> Outcomes,
        ScenarioExecutionTraceFingerprint Fingerprint) state)
    {
        SchemaVersion = state.SchemaVersion;
        Scenario = state.Scenario;
        Interpreter = state.Interpreter;
        Outcomes = state.Outcomes;
        Fingerprint = state.Fingerprint;
    }

    /// <summary>Gets the exact portable scenario-trace schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the complete fingerprint-verified scenario authority interpreted by this trace.</summary>
    public ScenarioDefinitionDocument Scenario { get; }

    /// <summary>Gets the exact action-interpreter identity and version.</summary>
    public string Interpreter { get; }

    /// <summary>Gets one outcome per action in canonical execution order.</summary>
    public ImmutableArray<ScenarioActionOutcome> Outcomes { get; }

    /// <summary>Gets the fingerprint of exact retained trace content.</summary>
    public ScenarioExecutionTraceFingerprint Fingerprint { get; }

    /// <summary>Creates a current-version trace from exact scenario execution outcomes.</summary>
    /// <param name="scenario">Exact fingerprint-verified scenario that was interpreted.</param>
    /// <param name="interpreter">Exact action-interpreter identity and version.</param>
    /// <param name="outcomes">One outcome per action in canonical execution order.</param>
    /// <returns>A complete fingerprint-verified scenario execution trace.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scenario"/> or <paramref name="interpreter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The interpreter is empty, or outcomes do not correspond exactly to the scenario schedule and contracts.
    /// </exception>
    public static ScenarioExecutionTraceDocument FromOutcomes(
        ScenarioDefinitionDocument scenario,
        string interpreter,
        ImmutableArray<ScenarioActionOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        interpreter = Guard.RequireNotNullOrWhiteSpace(interpreter);
        outcomes = ValidateOutcomes(scenario, outcomes);
        return new(CreateState(scenario, interpreter, outcomes));
    }

    static (
        string SchemaVersion,
        ScenarioDefinitionDocument Scenario,
        string Interpreter,
        ImmutableArray<ScenarioActionOutcome> Outcomes,
        ScenarioExecutionTraceFingerprint Fingerprint) ValidateAndNormalize(
        string schemaVersion,
        ScenarioDefinitionDocument scenario,
        string interpreter,
        ImmutableArray<ScenarioActionOutcome> outcomes,
        ScenarioExecutionTraceFingerprint fingerprint)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Scenario execution trace schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(scenario);
        interpreter = Guard.RequireNotNullOrWhiteSpace(interpreter);
        ArgumentNullException.ThrowIfNull(fingerprint);
        outcomes = ValidateOutcomes(scenario, outcomes);
        var state = CreateState(scenario, interpreter, outcomes);
        if (fingerprint != state.Fingerprint)
        {
            throw new ArgumentException(
                "The supplied scenario execution trace fingerprint does not match canonical retained content.",
                nameof(fingerprint));
        }

        return state;
    }

    static ImmutableArray<ScenarioActionOutcome> ValidateOutcomes(
        ScenarioDefinitionDocument scenario,
        ImmutableArray<ScenarioActionOutcome> outcomes)
    {
        outcomes = outcomes.IsDefault ? [] : outcomes;
        var plan = scenario.Compile();
        var actions = plan.Definition.Actions;
        if (outcomes.Length != actions.Length)
        {
            throw new ArgumentException(
                $"A complete scenario trace requires {actions.Length} outcomes, but {outcomes.Length} were supplied.",
                nameof(outcomes));
        }

        for (var index = 0; index < outcomes.Length; index++)
        {
            var outcome = outcomes[index]
                ?? throw new ArgumentException("A scenario trace cannot contain a null outcome.", nameof(outcomes));
            var action = actions[index];
            if (!string.Equals(outcome.ActionId, action.Id, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Outcome {index} names action '{outcome.ActionId}', but canonical execution order requires "
                    + $"'{action.Id}'.",
                    nameof(outcomes));
            }

            var outputContract = plan.GetOperation(action.OperationId).Output;
            if (outcome.Output.Contract != outputContract)
            {
                throw new ArgumentException(
                    $"Outcome for action '{action.Id}' does not carry operation '{action.OperationId}'s output contract.",
                    nameof(outcomes));
            }

            var validation = PortableExecutionValidator.Validate(outcome.Output);
            if (!validation.IsValid)
            {
                var errors = validation.Diagnostics
                    .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}");
                throw new ArgumentException(
                    $"Outcome for action '{action.Id}' is not portable: {string.Join(" | ", errors)}",
                    nameof(outcomes));
            }
        }

        return outcomes;
    }

    static (
        string SchemaVersion,
        ScenarioDefinitionDocument Scenario,
        string Interpreter,
        ImmutableArray<ScenarioActionOutcome> Outcomes,
        ScenarioExecutionTraceFingerprint Fingerprint) CreateState(
        ScenarioDefinitionDocument scenario,
        string interpreter,
        ImmutableArray<ScenarioActionOutcome> outcomes) =>
        (
            CurrentSchemaVersion,
            scenario,
            interpreter,
            outcomes,
            new(
                ScenarioExecutionTraceFingerprint.CurrentAlgorithm,
                ScenarioExecutionTraceFingerprint.CurrentCanonicalization,
                ScenarioExecutionTraceCanonicalizer.ComputeFingerprint(scenario, interpreter, outcomes)));
}

/// <summary>Strict deterministic JSON boundary for portable scenario execution traces.</summary>
public static class ScenarioExecutionTraceJsonSerializer
{
    const string ContractName = "scenario execution trace";

    /// <summary>Creates strict serializer options for the closed scenario-trace wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict case-sensitive portable-document options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one verified portable scenario execution trace.</summary>
    /// <param name="trace">Trace to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable scenario execution trace JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Trace content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Trace content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Trace content contains an unsupported runtime type.</exception>
    public static string Serialize(
        ScenarioExecutionTraceDocument trace,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(trace))
            : JsonSerializer.Serialize(trace, CreateOptions(formatting));
    }

    /// <summary>Gets canonical UTF-8 JSON for one complete scenario execution trace.</summary>
    /// <param name="trace">Trace to serialize.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Trace content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Trace content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Trace content contains an unsupported runtime type.</exception>
    public static byte[] GetCanonicalBytes(ScenarioExecutionTraceDocument trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return StrictDocumentJson.GetCanonicalBytes(trace, CreateOptions());
    }

    /// <summary>Deserializes and validates one current-version scenario execution trace.</summary>
    /// <param name="json">Persisted scenario execution trace JSON.</param>
    /// <returns>A normalized fingerprint-verified trace.</returns>
    /// <exception cref="JsonException">
    /// JSON is empty, malformed, duplicated, noncanonical, unsupported, invalid, or fingerprint-inconsistent.
    /// </exception>
    public static ScenarioExecutionTraceDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var trace);
        if (validation.IsValid && trace is not null)
            return trace;

        throw new JsonException(string.Join(
            " | ",
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}")));
    }

    /// <summary>Attempts to deserialize and validate one scenario execution trace.</summary>
    /// <param name="json">Persisted scenario execution trace JSON.</param>
    /// <param name="trace">Receives the validated trace when successful; otherwise <see langword="null"/>.</param>
    /// <returns>Structured wire, schema, scenario, outcome, and fingerprint diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out ScenarioExecutionTraceDocument? trace)
    {
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                ContractName,
                out trace,
                out var error))
        {
            return DocumentValidationResult.Valid;
        }

        trace = null;
        return StrictDocumentJson.Error(
            error.Failure switch
            {
                StrictDocumentJsonReadFailure.Empty => "simulation.scenario.trace.jsonEmpty",
                StrictDocumentJsonReadFailure.InvalidJson => "simulation.scenario.trace.jsonInvalid",
                StrictDocumentJsonReadFailure.RootInvalid => "simulation.scenario.trace.rootInvalid",
                StrictDocumentJsonReadFailure.DuplicateProperty => "simulation.scenario.trace.duplicateProperty",
                StrictDocumentJsonReadFailure.DeserializationInvalid => "simulation.scenario.trace.contentInvalid",
                StrictDocumentJsonReadFailure.DeserializationNull => "simulation.scenario.trace.contentMissing",
                StrictDocumentJsonReadFailure.WireNonCanonical => "simulation.scenario.trace.wireNonCanonical",
                _ => "simulation.scenario.trace.unknown"
            },
            error.Message,
            error.Location);
    }
}

static class ScenarioExecutionTraceCanonicalizer
{
    public static string ComputeFingerprint(
        ScenarioDefinitionDocument scenario,
        string interpreter,
        ImmutableArray<ScenarioActionOutcome> outcomes)
    {
        using SimulationFingerprintWriter writer = new();
        writer.Append(ScenarioExecutionTraceFingerprint.CurrentCanonicalization);
        writer.Append(scenario.SchemaVersion);
        writer.Append(scenario.Definition.Id);
        writer.Append(scenario.Definition.Revision);
        writer.Append(scenario.Fingerprint.Algorithm);
        writer.Append(scenario.Fingerprint.Canonicalization);
        writer.Append(scenario.Fingerprint.Value);
        writer.Append(interpreter);
        writer.Append(outcomes.Length);
        var options = ScenarioExecutionTraceJsonSerializer.CreateOptions();
        foreach (var outcome in outcomes)
        {
            writer.Append(outcome.ActionId);
            writer.Append(StrictDocumentJson.GetCanonicalBytes(outcome.Output, options));
        }

        return writer.Complete();
    }
}
