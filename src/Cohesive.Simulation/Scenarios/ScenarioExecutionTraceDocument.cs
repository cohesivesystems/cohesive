using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Scenarios;

/// <summary>Portable initial materialization of one scenario actor retained by an execution trace.</summary>
/// <remarks>
/// Actor and exemplar definitions remain authoritative in the retained scenario. This record retains only the
/// interpreter-produced state and identity needed to verify and reconstruct the execution's state chain.
/// </remarks>
public sealed record ScenarioActorInitialState
{
    /// <summary>Creates one retained initial actor state.</summary>
    /// <param name="actorId">Stable identity of the scenario actor.</param>
    /// <param name="entityId">Canonical entity identity assigned by the world interpreter.</param>
    /// <param name="observation">Complete initial actor observation.</param>
    /// <param name="originReplayToken">Opaque evidence for replaying the exact initial observation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="actorId"/>, <paramref name="observation"/>, or <paramref name="originReplayToken"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The actor identity, entity identity, or origin replay token is empty or white-space.
    /// </exception>
    [JsonConstructor]
    public ScenarioActorInitialState(
        string actorId,
        EntityId entityId,
        Observation observation,
        string originReplayToken)
    {
        ActorId = Guard.RequireNotNullOrWhiteSpace(actorId);
        if (string.IsNullOrWhiteSpace(entityId.Value))
            throw new ArgumentException("An initial actor state requires an entity identity.", nameof(entityId));

        EntityId = entityId;
        Observation = Guard.RequireNotNull(observation);
        OriginReplayToken = Guard.RequireNotNullOrWhiteSpace(originReplayToken);
    }

    /// <summary>Gets the stable identity of the scenario actor.</summary>
    public string ActorId { get; }

    /// <summary>Gets the canonical entity identity assigned by the world interpreter.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the complete initial actor observation.</summary>
    public Observation Observation { get; }

    /// <summary>Gets opaque evidence for replaying the exact initial observation.</summary>
    public string OriginReplayToken { get; }
}

/// <summary>One portable outcome associated with an exact scheduled scenario action.</summary>
public sealed record ScenarioActionOutcome
{
    /// <summary>Creates a retained action outcome.</summary>
    /// <param name="actionId">Stable identity of the action that produced the outcome.</param>
    /// <param name="output">Portable output or semantic failure evidence returned by its interpreter.</param>
    /// <param name="stateChanges">Explicit actor observation replacements produced by the action.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="actionId"/> or <paramref name="output"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="actionId"/> is empty or white-space, or state changes contain one actor more than once.
    /// </exception>
    [JsonConstructor]
    public ScenarioActionOutcome(
        string actionId,
        PortableValue output,
        ImmutableArray<ScenarioActorStateChange> stateChanges = default)
    {
        ActionId = Guard.RequireNotNullOrWhiteSpace(actionId);
        Output = Guard.RequireNotNull(output);
        StateChanges = ScenarioActionResult.NormalizeStateChanges(stateChanges);
    }

    /// <summary>Gets the stable identity of the action that produced the outcome.</summary>
    public string ActionId { get; }

    /// <summary>Gets the portable output or semantic failure evidence.</summary>
    public PortableValue Output { get; }

    /// <summary>Gets explicit actor observation replacements in canonical actor-identity order.</summary>
    public ImmutableArray<ScenarioActorStateChange> StateChanges { get; }
}

/// <summary>Versioned deterministic identity of exact retained scenario execution content.</summary>
public sealed record ScenarioExecutionTraceFingerprint
{
    /// <summary>Cryptographic hash algorithm used by the current trace profile.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current trace fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-simulation-scenario-trace/v2-c14n/v1";

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
/// <see cref="Scenario"/> remains the complete source authority. Initial actor materializations retain the execution's
/// starting evidence, while every action outcome retains explicit before/after state changes. The complete chain is
/// revalidated on restoration. The interpreter identity is execution-policy attribution; executable handler code
/// never enters the document.
/// </remarks>
public sealed record ScenarioExecutionTraceDocument
{
    /// <summary>Current portable scenario execution trace schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-scenario-trace/v2";

    /// <summary>Creates or restores one complete scenario execution trace.</summary>
    /// <param name="schemaVersion">Exact portable scenario-trace schema.</param>
    /// <param name="scenario">Exact fingerprint-verified scenario that was interpreted.</param>
    /// <param name="interpreter">Exact action-interpreter identity and version.</param>
    /// <param name="initialActors">One initial materialization per scenario actor in canonical actor order.</param>
    /// <param name="outcomes">One outcome per action in canonical execution order.</param>
    /// <param name="fingerprint">Persisted fingerprint of exact retained trace content.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="scenario"/>, <paramref name="interpreter"/>, or
    /// <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported; the interpreter is empty; initial actors or outcomes are incomplete or reordered;
    /// outputs violate their operation contracts; state changes do not form a valid chain; or the fingerprint does
    /// not match current canonical content.
    /// </exception>
    [JsonConstructor]
    public ScenarioExecutionTraceDocument(
        string schemaVersion,
        ScenarioDefinitionDocument scenario,
        string interpreter,
        ImmutableArray<ScenarioActorInitialState> initialActors,
        ImmutableArray<ScenarioActionOutcome> outcomes,
        ScenarioExecutionTraceFingerprint fingerprint)
        : this(ValidateAndNormalize(schemaVersion, scenario, interpreter, initialActors, outcomes, fingerprint))
    {
    }

    ScenarioExecutionTraceDocument((
        string SchemaVersion,
        ScenarioDefinitionDocument Scenario,
        string Interpreter,
        ImmutableArray<ScenarioActorInitialState> InitialActors,
        ImmutableArray<ScenarioActionOutcome> Outcomes,
        ScenarioExecutionTraceFingerprint Fingerprint) state)
    {
        SchemaVersion = state.SchemaVersion;
        Scenario = state.Scenario;
        Interpreter = state.Interpreter;
        InitialActors = state.InitialActors;
        Outcomes = state.Outcomes;
        Fingerprint = state.Fingerprint;
    }

    /// <summary>Gets the exact portable scenario-trace schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the complete fingerprint-verified scenario authority interpreted by this trace.</summary>
    public ScenarioDefinitionDocument Scenario { get; }

    /// <summary>Gets the exact action-interpreter identity and version.</summary>
    public string Interpreter { get; }

    /// <summary>Gets one retained initial state per scenario actor in canonical actor-identity order.</summary>
    public ImmutableArray<ScenarioActorInitialState> InitialActors { get; }

    /// <summary>Gets one outcome per action in canonical execution order.</summary>
    public ImmutableArray<ScenarioActionOutcome> Outcomes { get; }

    /// <summary>Gets the fingerprint of exact retained trace content.</summary>
    public ScenarioExecutionTraceFingerprint Fingerprint { get; }

    /// <summary>Creates a current-version trace from exact scenario execution outcomes.</summary>
    /// <param name="world">Exact scenario and complete initial actor materialization that were interpreted.</param>
    /// <param name="interpreter">Exact action-interpreter identity and version.</param>
    /// <param name="outcomes">One outcome per action in canonical execution order.</param>
    /// <returns>A complete fingerprint-verified scenario execution trace.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="world"/> or <paramref name="interpreter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The interpreter is empty, outcomes do not correspond exactly to the scenario schedule and contracts, or state
    /// changes do not form a valid chain from <paramref name="world"/>.
    /// </exception>
    public static ScenarioExecutionTraceDocument FromOutcomes(
        ScenarioWorldSnapshot world,
        string interpreter,
        ImmutableArray<ScenarioActionOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(world);
        interpreter = Guard.RequireNotNullOrWhiteSpace(interpreter);
        var initialActors = ToInitialActors(world);
        outcomes = ValidateOutcomes(world.Scenario, initialActors, outcomes);
        return new(CreateState(world.Scenario, interpreter, initialActors, outcomes));
    }

    /// <summary>Reconstructs the complete actor world after every retained action outcome has been applied.</summary>
    /// <returns>An immutable final-world snapshot retaining the scenario's actor and exemplar definitions.</returns>
    /// <exception cref="ArgumentException">
    /// Retained initial state or state changes no longer form a valid chain. Verified instances do not throw this
    /// exception unless modified through unsupported means.
    /// </exception>
    public ScenarioWorldSnapshot ToFinalWorldSnapshot()
    {
        var finalObservations = BuildFinalObservations(InitialActors, Outcomes);
        var definition = Scenario.Definition;
        var actors = ImmutableArray.CreateBuilder<ScenarioActorSnapshot>(definition.Actors.Length);
        for (var index = 0; index < definition.Actors.Length; index++)
        {
            var actor = definition.Actors[index];
            var initial = InitialActors[index];
            actors.Add(new(
                actor,
                definition.InitialWorld.GetExemplar(actor.ExemplarId),
                initial.EntityId,
                finalObservations[actor.Id],
                initial.OriginReplayToken));
        }

        return ScenarioWorldSnapshot.Create(Scenario, actors.MoveToImmutable());
    }

    static (
        string SchemaVersion,
        ScenarioDefinitionDocument Scenario,
        string Interpreter,
        ImmutableArray<ScenarioActorInitialState> InitialActors,
        ImmutableArray<ScenarioActionOutcome> Outcomes,
        ScenarioExecutionTraceFingerprint Fingerprint) ValidateAndNormalize(
        string schemaVersion,
        ScenarioDefinitionDocument scenario,
        string interpreter,
        ImmutableArray<ScenarioActorInitialState> initialActors,
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
        initialActors = ValidateInitialActors(scenario, initialActors);
        outcomes = ValidateOutcomes(scenario, initialActors, outcomes);
        var state = CreateState(scenario, interpreter, initialActors, outcomes);
        if (fingerprint != state.Fingerprint)
        {
            throw new ArgumentException(
                "The supplied scenario execution trace fingerprint does not match canonical retained content.",
                nameof(fingerprint));
        }

        return state;
    }

    static ImmutableArray<ScenarioActorInitialState> ToInitialActors(ScenarioWorldSnapshot world)
    {
        var actors = ImmutableArray.CreateBuilder<ScenarioActorInitialState>(world.Actors.Length);
        foreach (var actor in world.Actors)
        {
            actors.Add(new(
                actor.Actor.Id,
                actor.EntityId,
                actor.Observation,
                actor.OriginReplayToken));
        }

        return actors.MoveToImmutable();
    }

    static ImmutableArray<ScenarioActorInitialState> ValidateInitialActors(
        ScenarioDefinitionDocument scenario,
        ImmutableArray<ScenarioActorInitialState> initialActors)
    {
        if (initialActors.IsDefault)
            throw new ArgumentException("Initial actor states must be initialized.", nameof(initialActors));

        var actors = scenario.Definition.Actors;
        if (initialActors.Length != actors.Length)
        {
            throw new ArgumentException(
                $"A complete scenario trace requires {actors.Length} initial actor states, but "
                + $"{initialActors.Length} were supplied.",
                nameof(initialActors));
        }

        for (var index = 0; index < actors.Length; index++)
        {
            var initial = initialActors[index]
                ?? throw new ArgumentException("Initial actor states cannot contain null.", nameof(initialActors));
            if (!string.Equals(initial.ActorId, actors[index].Id, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Initial actor state {index} names '{initial.ActorId}', but canonical actor order requires "
                    + $"'{actors[index].Id}'.",
                    nameof(initialActors));
            }
        }

        return initialActors;
    }

    static ImmutableArray<ScenarioActionOutcome> ValidateOutcomes(
        ScenarioDefinitionDocument scenario,
        ImmutableArray<ScenarioActorInitialState> initialActors,
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

        _ = BuildFinalObservations(initialActors, outcomes);

        return outcomes;
    }

    static Dictionary<string, Observation> BuildFinalObservations(
        ImmutableArray<ScenarioActorInitialState> initialActors,
        ImmutableArray<ScenarioActionOutcome> outcomes)
    {
        Dictionary<string, Observation> current = new(initialActors.Length, StringComparer.Ordinal);
        foreach (var actor in initialActors)
            current.Add(actor.ActorId, actor.Observation);

        for (var outcomeIndex = 0; outcomeIndex < outcomes.Length; outcomeIndex++)
        {
            foreach (var change in outcomes[outcomeIndex].StateChanges)
            {
                if (!current.TryGetValue(change.ActorId, out var before))
                {
                    throw new ArgumentException(
                        $"Outcome {outcomeIndex} changes unknown actor '{change.ActorId}'.",
                        nameof(outcomes));
                }
                if (!before.Equals(change.Before))
                {
                    throw new ArgumentException(
                        $"Outcome {outcomeIndex} carries stale before-state evidence for actor '{change.ActorId}'.",
                        nameof(outcomes));
                }

                current[change.ActorId] = change.After;
            }
        }

        return current;
    }

    static (
        string SchemaVersion,
        ScenarioDefinitionDocument Scenario,
        string Interpreter,
        ImmutableArray<ScenarioActorInitialState> InitialActors,
        ImmutableArray<ScenarioActionOutcome> Outcomes,
        ScenarioExecutionTraceFingerprint Fingerprint) CreateState(
        ScenarioDefinitionDocument scenario,
        string interpreter,
        ImmutableArray<ScenarioActorInitialState> initialActors,
        ImmutableArray<ScenarioActionOutcome> outcomes) =>
        (
            CurrentSchemaVersion,
            scenario,
            interpreter,
            initialActors,
            outcomes,
            new(
                ScenarioExecutionTraceFingerprint.CurrentAlgorithm,
                ScenarioExecutionTraceFingerprint.CurrentCanonicalization,
                ScenarioExecutionTraceCanonicalizer.ComputeFingerprint(
                    scenario,
                    interpreter,
                    initialActors,
                    outcomes)));
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
        ImmutableArray<ScenarioActorInitialState> initialActors,
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
        writer.Append(initialActors.Length);
        var options = ScenarioExecutionTraceJsonSerializer.CreateOptions();
        foreach (var actor in initialActors)
        {
            writer.Append(actor.ActorId);
            writer.Append(actor.EntityId.Value);
            writer.Append(StrictDocumentJson.GetCanonicalBytes(actor.Observation, options));
            writer.Append(actor.OriginReplayToken);
        }

        writer.Append(outcomes.Length);
        foreach (var outcome in outcomes)
        {
            writer.Append(outcome.ActionId);
            writer.Append(StrictDocumentJson.GetCanonicalBytes(outcome.Output, options));
            writer.Append(outcome.StateChanges.Length);
            foreach (var change in outcome.StateChanges)
            {
                writer.Append(change.ActorId);
                writer.Append(StrictDocumentJson.GetCanonicalBytes(change.Before, options));
                writer.Append(StrictDocumentJson.GetCanonicalBytes(change.After, options));
            }
        }

        return writer.Complete();
    }
}
