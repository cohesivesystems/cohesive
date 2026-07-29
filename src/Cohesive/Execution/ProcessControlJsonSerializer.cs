using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Strict canonical JSON serialization for portable Process-control commands, state, and decisions.
/// </summary>
/// <remarks>
/// Reading rejects duplicate object properties recursively, unknown members and polymorphic variants,
/// unsupported schema versions, and representations that do not project back to the same canonical typed wire.
/// </remarks>
public static class ProcessControlJsonSerializer
{
    /// <summary>Creates serializer options for the closed Process-control wire contracts.</summary>
    /// <param name="formatting">Compact or human-readable JSON formatting.</param>
    /// <returns>Case-sensitive strict portable-document serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one canonical Process-control command.</summary>
    /// <param name="command">Command to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output formatting.</param>
    /// <returns>Strict versioned Process-control command JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The command violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The command contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The command has no canonical JSON encoding.</exception>
    public static string Serialize(
        ProcessControlCommand command,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(command, formatting);

    /// <summary>Serializes complete portable Process-control state.</summary>
    /// <param name="state">State to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output formatting.</param>
    /// <returns>Strict versioned Process-control state JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The state violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The state contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The state has no canonical JSON encoding.</exception>
    public static string Serialize(
        ProcessControlState state,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(state, formatting);

    /// <summary>Serializes one portable Process-control decision result.</summary>
    /// <param name="decision">Decision to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable output formatting.</param>
    /// <returns>Strict versioned Process-control decision JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The decision violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The decision contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The decision has no canonical JSON encoding.</exception>
    public static string Serialize(
        ProcessControlDecision decision,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        SerializeCore(decision, formatting);

    /// <summary>Gets deterministic canonical UTF-8 JSON for one Process-control command.</summary>
    /// <param name="command">Command to encode.</param>
    /// <returns>Canonical exact-decimal JSON bytes preserving complete command semantics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The command violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The command contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The command has no canonical JSON encoding.</exception>
    public static byte[] GetCanonicalBytes(ProcessControlCommand command) =>
        GetCanonicalBytesCore(command);

    /// <summary>Gets deterministic canonical UTF-8 JSON for complete Process-control state.</summary>
    /// <param name="state">State to encode.</param>
    /// <returns>Canonical exact-decimal JSON bytes preserving complete persisted state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The state violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The state contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The state has no canonical JSON encoding.</exception>
    public static byte[] GetCanonicalBytes(ProcessControlState state) =>
        GetCanonicalBytesCore(state);

    /// <summary>Gets deterministic canonical UTF-8 JSON for one Process-control decision result.</summary>
    /// <param name="decision">Decision to encode.</param>
    /// <returns>Canonical exact-decimal JSON bytes preserving the complete result contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The decision violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The decision contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">The decision has no canonical JSON encoding.</exception>
    public static byte[] GetCanonicalBytes(ProcessControlDecision decision) =>
        GetCanonicalBytesCore(decision);

    /// <summary>Deserializes a current-version canonical Process-control command.</summary>
    /// <param name="json">Persisted Process-control command JSON.</param>
    /// <param name="contracts">
    /// Exact interaction contracts and shape graph used to link Signals and validate all portable values.
    /// </param>
    /// <returns>The canonical command represented by <paramref name="json"/>.</returns>
    /// <exception cref="JsonException">
    /// The JSON is malformed, duplicated, noncanonical, uses an unknown member or variant, or declares an
    /// unsupported command schema version.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="contracts"/> is <see langword="null"/>.</exception>
    public static ProcessControlCommand DeserializeCommand(
        string json,
        InteractionContractCatalog contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        return DeserializeCore<ProcessControlCommand>(
            json,
            "Process-control command",
            command =>
            {
                if (command.SchemaVersion != ProcessControlCommand.CurrentSchemaVersion)
                {
                    throw new JsonException($"Unsupported Process-control command schema '{command.SchemaVersion.Value}'.");
                }

                command.EnsureDeclaredVariant();
                ValidateCommand(command, contracts, string.Empty);
            });
    }

    /// <summary>Deserializes complete current-version canonical Process-control state.</summary>
    /// <param name="json">Persisted Process-control state JSON.</param>
    /// <param name="contracts">
    /// Exact interaction contracts and shape graph used to link Signals and validate all portable values.
    /// </param>
    /// <returns>The self-validating portable state represented by <paramref name="json"/>.</returns>
    /// <exception cref="JsonException">
    /// The JSON is malformed, duplicated, noncanonical, uses an unknown member, or violates state invariants.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="contracts"/> is <see langword="null"/>.</exception>
    public static ProcessControlState DeserializeState(
        string json,
        InteractionContractCatalog contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        return DeserializeCore<ProcessControlState>(
            json,
            "Process-control state",
            state => ValidateState(state, contracts));
    }

    /// <summary>Deserializes a current-version canonical Process-control decision result.</summary>
    /// <param name="json">Persisted Process-control decision JSON.</param>
    /// <param name="contracts">
    /// Exact interaction contracts and shape graph used to link Signals and validate all portable values.
    /// </param>
    /// <returns>The portable decision represented by <paramref name="json"/>.</returns>
    /// <exception cref="JsonException">
    /// The JSON is malformed, duplicated, noncanonical, uses an unknown member or intent variant, declares an
    /// unsupported decision schema version, or violates decision/state invariants.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="contracts"/> is <see langword="null"/>.</exception>
    public static ProcessControlDecision DeserializeDecision(
        string json,
        InteractionContractCatalog contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        return DeserializeCore<ProcessControlDecision>(
            json,
            "Process-control decision",
            decision => ValidateDecision(decision, contracts));
    }

    static string SerializeCore<T>(T value, PortableDocumentJsonFormatting formatting)
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

    static T DeserializeCore<T>(
        string json,
        string contractName,
        Action<T>? validate = null)
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
        catch (Exception exception) when (IsWireFailure(exception))
        {
            throw new JsonException($"Invalid {contractName}: {exception.Message}", exception);
        }

        return value!;
    }

    static void ValidateDecision(
        ProcessControlDecision decision,
        InteractionContractCatalog contracts)
    {
        ValidateState(decision.State, contracts);
        switch (decision.Intent)
        {
            case ProcessSignalAdmissionIntent signal:
                RequireValid(
                    InteractionEnvelopeValidator.Validate(
                        signal.Admission.Signal,
                        contracts,
                        contracts.ShapeGraph),
                    "/intent/admission/signal");
                break;
            case ProcessCancellationIntent cancellation:
                ValidateReason(cancellation.Reason, contracts.ShapeGraph, "/intent/reason");
                break;
            case ProcessTerminationIntent termination:
                ValidateReason(termination.Reason, contracts.ShapeGraph, "/intent/reason");
                break;
        }
    }

    static void ValidateState(
        ProcessControlState state,
        InteractionContractCatalog contracts)
    {
        for (var attemptIndex = 0; attemptIndex < state.Attempts.Length; attemptIndex++)
        {
            var bindings = state.Attempts[attemptIndex].AffinityBindings;
            for (var bindingIndex = 0; bindingIndex < bindings.Length; bindingIndex++)
            {
                RequireValid(
                    PortableExecutionValidator.Validate(
                        bindings[bindingIndex].Affinity.Value,
                        contracts.ShapeGraph),
                    $"/attempts/{attemptIndex}/affinityBindings/{bindingIndex}/affinity/value");
            }
        }

        for (var receiptIndex = 0; receiptIndex < state.Receipts.Length; receiptIndex++)
        {
            ValidateCommand(
                state.Receipts[receiptIndex].Command,
                contracts,
                $"/receipts/{receiptIndex}/command");
        }
    }

    static void ValidateCommand(
        ProcessControlCommand command,
        InteractionContractCatalog contracts,
        string location)
    {
        switch (command)
        {
            case SignalProcessCommand signal:
                RequireValid(
                    InteractionEnvelopeValidator.Validate(
                        signal.Signal,
                        contracts,
                        contracts.ShapeGraph),
                    location + "/signal");
                break;
            case RestartProcessAttemptCommand restart:
                ValidateReason(restart.Plan.Reason, contracts.ShapeGraph, location + "/plan/reason");
                break;
            case CancelProcessCommand cancellation:
                ValidateReason(cancellation.Reason, contracts.ShapeGraph, location + "/reason");
                break;
            case TerminateProcessCommand termination:
                ValidateReason(termination.Reason, contracts.ShapeGraph, location + "/reason");
                break;
        }
    }

    static void ValidateReason(
        ProcessControlReason reason,
        ShapeGraph? graph,
        string location)
    {
        if (reason.Detail is { } detail)
        {
            RequireValid(PortableExecutionValidator.Validate(detail, graph), location + "/detail");
        }
    }

    static void RequireValid(DocumentValidationResult validation, string location)
    {
        if (validation.IsValid)
        {
            return;
        }

        var diagnostic = validation.Diagnostics[0];
        var nestedLocation = diagnostic.Location;
        throw new JsonException(
            $"{diagnostic.Code} at {location}"
            + (string.IsNullOrEmpty(nestedLocation) || nestedLocation == "$"
                ? string.Empty
                : nestedLocation![0] == '/'
                    ? nestedLocation
                    : "/" + nestedLocation)
            + $": {diagnostic.Message}");
    }

    static bool IsWireFailure(Exception exception) =>
        exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or FormatException
            or OverflowException;
}
