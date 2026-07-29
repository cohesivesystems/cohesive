using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

/// <summary>Strict canonical JSON serialization for portable Control definitions, evidence, state, and decisions.</summary>
/// <remarks>
/// Reading rejects duplicate properties recursively, unknown members, unsupported schema versions, and input that
/// does not project back to the same unique canonical typed wire representation.
/// </remarks>
public static class ControlJsonSerializer
{
    /// <summary>Creates strict case-sensitive serializer options for the closed Control wire contracts.</summary>
    /// <param name="formatting">Compact or human-readable JSON formatting.</param>
    /// <returns>Strict portable-document serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes a canonical control-loop definition.</summary>
    /// <param name="value">Definition to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output.</param>
    /// <returns>Strict versioned Control JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The definition cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The definition contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The definition has no canonical JSON representation.</exception>
    public static string Serialize(ControlLoopDefinition value, PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(value, formatting);

    /// <summary>Serializes a typed control observation.</summary>
    /// <param name="value">Observation to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output.</param>
    /// <returns>Strict versioned Control JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The observation cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The observation contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The observation has no canonical JSON representation.</exception>
    public static string Serialize(ControlObservation value, PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(value, formatting);

    /// <summary>Serializes a non-authoritative recommendation.</summary>
    /// <param name="value">Recommendation to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output.</param>
    /// <returns>Strict Control JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The recommendation cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The recommendation contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The recommendation has no canonical JSON representation.</exception>
    public static string Serialize(ControlRecommendation value, PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(value, formatting);

    /// <summary>Serializes complete durable AIMD state.</summary>
    /// <param name="value">State to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output.</param>
    /// <returns>Strict versioned Control JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The state cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The state contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The state has no canonical JSON representation.</exception>
    public static string Serialize(AimdControlState value, PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(value, formatting);

    /// <summary>Serializes a pure controller decision.</summary>
    /// <param name="value">Decision to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output.</param>
    /// <returns>Strict versioned Control JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The decision cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The decision contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The decision has no canonical JSON representation.</exception>
    public static string Serialize(ControlDecision value, PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(value, formatting);

    /// <summary>Serializes a generic safe application point.</summary>
    /// <param name="value">Application point to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output.</param>
    /// <returns>Strict versioned Control JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The application point cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The application point contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The application point has no canonical JSON representation.</exception>
    public static string Serialize(ControlApplicationPoint value, PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(value, formatting);

    /// <summary>Serializes an applied actuation receipt.</summary>
    /// <param name="value">Actuation to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output.</param>
    /// <returns>Strict Control JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The actuation cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The actuation contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The actuation has no canonical JSON representation.</exception>
    public static string Serialize(ControlActuation value, PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(value, formatting);

    /// <summary>Serializes a safe-point actuation result.</summary>
    /// <param name="value">Result to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output.</param>
    /// <returns>Strict versioned Control JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The result cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The result contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The result has no canonical JSON representation.</exception>
    public static string Serialize(ControlActuationResult value, PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(value, formatting);

    /// <summary>Gets deterministic canonical UTF-8 JSON for a control-loop definition.</summary>
    /// <param name="value">Definition to encode.</param>
    /// <returns>Canonical exact fixed-point JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The definition cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The definition contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The definition has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(ControlLoopDefinition value) => GetCanonicalBytesCore(value);

    /// <summary>Gets deterministic canonical UTF-8 JSON for a control observation.</summary>
    /// <param name="value">Observation to encode.</param>
    /// <returns>Canonical exact fixed-point JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The observation cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The observation contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The observation has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(ControlObservation value) => GetCanonicalBytesCore(value);

    /// <summary>Gets deterministic canonical UTF-8 JSON for complete controller state.</summary>
    /// <param name="value">State to encode.</param>
    /// <returns>Canonical exact fixed-point JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The state cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The state contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The state has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(AimdControlState value) => GetCanonicalBytesCore(value);

    /// <summary>Gets deterministic canonical UTF-8 JSON for a controller decision.</summary>
    /// <param name="value">Decision to encode.</param>
    /// <returns>Canonical exact fixed-point JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The decision cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The decision contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The decision has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(ControlDecision value) => GetCanonicalBytesCore(value);

    /// <summary>Gets deterministic canonical UTF-8 JSON for an actuation result.</summary>
    /// <param name="value">Result to encode.</param>
    /// <returns>Canonical exact fixed-point JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The result cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The result contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The result has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(ControlActuationResult value) => GetCanonicalBytesCore(value);

    /// <summary>Deserializes a current-version canonical control-loop definition.</summary>
    /// <param name="json">Persisted definition JSON.</param>
    /// <returns>The validated canonical definition.</returns>
    /// <exception cref="JsonException">The wire is malformed, duplicated, unknown, noncanonical, or unsupported.</exception>
    public static ControlLoopDefinition DeserializeDefinition(string json) =>
        DeserializeCore<ControlLoopDefinition>(
            json,
            "Control definition",
            static definition => RequireCurrent(definition.SchemaVersion.Value, "/schemaVersion"));

    /// <summary>Deserializes a current-version canonical control observation.</summary>
    /// <param name="json">Persisted observation JSON.</param>
    /// <returns>The typed canonical observation.</returns>
    /// <exception cref="JsonException">The wire is malformed, duplicated, unknown, noncanonical, or unsupported.</exception>
    public static ControlObservation DeserializeObservation(string json) =>
        DeserializeCore<ControlObservation>(
            json,
            "Control observation",
            static observation => RequireCurrent(observation.SchemaVersion.Value, "/schemaVersion"));

    /// <summary>Deserializes a canonical recommendation and validates it against a loop definition.</summary>
    /// <param name="json">Persisted recommendation JSON.</param>
    /// <param name="definition">Definition that owns the recommendation.</param>
    /// <returns>The validated recommendation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The wire or recommendation is invalid.</exception>
    public static ControlRecommendation DeserializeRecommendation(string json, ControlLoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return DeserializeCore<ControlRecommendation>(json, "Control recommendation", recommendation =>
            RequireRecommendation(definition, recommendation));
    }

    /// <summary>Deserializes current-version durable controller state and validates it against a definition.</summary>
    /// <param name="json">Persisted state JSON.</param>
    /// <param name="definition">Definition that owns the state.</param>
    /// <returns>The validated durable state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The wire or state is invalid.</exception>
    public static AimdControlState DeserializeState(string json, ControlLoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return DeserializeCore<AimdControlState>(json, "Control state", state =>
        {
            RequireCurrent(state.SchemaVersion.Value, "/schemaVersion");
            RequireValid(AimdControlReferenceRegulator.ValidateState(definition, state));
        });
    }

    /// <summary>Deserializes a current-version controller decision and validates its state.</summary>
    /// <param name="json">Persisted decision JSON.</param>
    /// <param name="definition">Definition that owns the decision.</param>
    /// <returns>The validated controller decision.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The wire or decision is invalid.</exception>
    public static ControlDecision DeserializeDecision(string json, ControlLoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return DeserializeCore<ControlDecision>(json, "Control decision", decision =>
        {
            RequireCurrent(decision.SchemaVersion.Value, "/schemaVersion");
            RequireValid(AimdControlReferenceRegulator.ValidateState(definition, decision.State));
        });
    }

    /// <summary>Deserializes a current-version generic safe application point.</summary>
    /// <param name="json">Persisted application-point JSON.</param>
    /// <returns>The canonical generic safe-point evidence.</returns>
    /// <exception cref="JsonException">The wire is malformed, duplicated, unknown, noncanonical, or unsupported.</exception>
    public static ControlApplicationPoint DeserializeApplicationPoint(string json) =>
        DeserializeCore<ControlApplicationPoint>(
            json,
            "Control application point",
            static point => RequireCurrent(point.SchemaVersion.Value, "/schemaVersion"));

    /// <summary>Deserializes an actuation receipt and validates its resulting point against a definition.</summary>
    /// <param name="json">Persisted actuation JSON.</param>
    /// <param name="definition">Definition that owns the actuation.</param>
    /// <returns>The validated actuation receipt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The wire or actuation is invalid.</exception>
    public static ControlActuation DeserializeActuation(string json, ControlLoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return DeserializeCore<ControlActuation>(json, "Control actuation", actuation =>
        {
            RequireCurrent(actuation.ApplicationPoint.SchemaVersion.Value, "/applicationPoint/schemaVersion");
            RequireValid(AimdControlReferenceRegulator.ValidateActuation(definition, actuation));
        });
    }

    /// <summary>Deserializes a current-version actuation result and validates its state against a definition.</summary>
    /// <param name="json">Persisted result JSON.</param>
    /// <param name="definition">Definition that owns the result.</param>
    /// <returns>The validated actuation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The wire or result is invalid.</exception>
    public static ControlActuationResult DeserializeActuationResult(string json, ControlLoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return DeserializeCore<ControlActuationResult>(json, "Control actuation result", result =>
        {
            RequireCurrent(result.SchemaVersion.Value, "/schemaVersion");
            RequireValid(AimdControlReferenceRegulator.ValidateState(definition, result.State));
            if (result.Actuation is not null)
                RequireRecommendation(definition, result.Actuation.Recommendation);
        });
    }

    static string SerializeCore<T>(
        T value,
        PortableDocumentJsonFormatting formatting)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytesCore(value))
            : JsonSerializer.Serialize(value, typeof(T), CreateOptions(formatting));
    }

    static byte[] GetCanonicalBytesCore<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return StrictDocumentJson.GetCanonicalBytes(value, CreateOptions());
    }

    static T DeserializeCore<T>(string json, string contractName, Action<T>? validate = null)
        where T : class
    {
        if (!StrictDocumentJson.TryReadCanonicalObject<T>(
                json,
                CreateOptions(),
                contractName,
                out var value,
                out var error))
        {
            throw new JsonException($"{error.Message} at {error.Location}");
        }

        try
        {
            validate?.Invoke(value!);
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or OverflowException)
        {
            throw new JsonException($"Invalid {contractName}: {exception.Message}", exception);
        }

        return value!;
    }

    static void RequireCurrent(string schemaVersion, string location)
    {
        if (!string.Equals(schemaVersion, ControlLoopDefinition.CurrentSchemaVersion.Value, StringComparison.Ordinal))
            throw new JsonException($"Unsupported Control schema version '{schemaVersion}' at {location}.");
    }

    static void RequireRecommendation(
        ControlLoopDefinition definition,
        ControlRecommendation recommendation)
        => RequireValid(AimdControlReferenceRegulator.ValidateRecommendation(definition, recommendation));

    static void RequireValid(DocumentValidationResult result)
    {
        if (result.IsValid)
            return;
        var diagnostic = result.Diagnostics[0];
        throw new JsonException($"{diagnostic.Code} at {diagnostic.Location ?? "$"}: {diagnostic.Message}");
    }
}
