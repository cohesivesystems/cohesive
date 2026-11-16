using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Scenarios;
using Cohesive.Storage;

namespace Cohesive.Simulation.Transitions;

/// <summary>Portable input to an explicitly versioned isolated-environment reconstruction profile.</summary>
/// <remarks>The profile owns reconstruction of initial entities, Process history, source receipts, exact policy
/// revisions and authorization. It must use only these inputs and pinned code/contracts. This is a recipe, not an
/// automatic repository snapshot or executable code. No credentials or live infrastructure belong in it.</remarks>
public sealed record ReactionScenarioEnvironment
{
    /// <summary>Validates a concrete typed reconstruction recipe.</summary>
    /// <param name="profile">Exact versioned reconstruction profile understood by the replay factory.</param>
    /// <param name="initialState">Concrete typed input containing every variable initial-state choice.</param>
    /// <exception cref="ArgumentException">The profile is blank or the input is not valid concrete portable data.</exception>
    /// <exception cref="ArgumentNullException">The input is null.</exception>
    public ReactionScenarioEnvironment(string profile, PortableValue initialState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentNullException.ThrowIfNull(initialState);
        if (initialState.Value is null || !PortableExecutionValidator.Validate(initialState).IsValid)
            throw new ArgumentException("Replay requires valid concrete initial-state data.", nameof(initialState));
        Profile = profile;
        InitialState = initialState;
    }

    /// <summary>Exact version of the caller-owned reconstruction semantics.</summary>
    public string Profile { get; }
    /// <summary>Complete typed input to that reconstruction profile.</summary>
    public PortableValue InitialState { get; }
    /// <summary>Canonical identity to pass to the reaction interpreter's environmentIdentity parameter.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Identity => "reaction-environment/v1/sha256/" + EntityStorageJson.Hash(
        StrictDocumentJson.GetCanonicalBytes(this, EntityStorageJson.Options));
}

/// <summary>Retains and re-executes a reaction trace with its exact portable environment recipe.</summary>
/// <remarks>Reuses the canonical scenario/trace and storage value codecs. A factory must create fresh isolated
/// stores and bind instrumentation on each invocation. Replay validates the resulting interpreter identity before
/// any scheduled action, then requires exact trace equality. This is deterministic execution replay, not proof of
/// liveness, a production restart, or a sandbox for arbitrary factories.</remarks>
public static class ReactionScenarioReplay
{
    const string Schema = "cohesive-reaction-scenario-replay/v1";
    sealed record Envelope(string Schema, ReactionScenarioEnvironment Environment, string Trace);

    /// <summary>Captures a completed run and the recipe that was pinned into its interpreter.</summary>
    /// <param name="environment">Recipe used to reconstruct the initial runtime state.</param>
    /// <param name="interpreter">Interpreter that executed the trace.</param>
    /// <param name="trace">Completed canonical trace, including schedule, faults and original actor observations.</param>
    /// <returns>Canonical JSON replay artifact.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Environment or trace identity differs from the interpreter.</exception>
    public static string Capture(ReactionScenarioEnvironment environment, MachineReactionScenarioInterpreter interpreter,
        ScenarioExecutionTraceDocument trace)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(trace);
        if (environment.Identity != interpreter.EnvironmentIdentity || trace.Interpreter != interpreter.Identity)
            throw new ArgumentException("The trace and environment must belong to the supplied interpreter.");
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(
            new Envelope(Schema, environment, ScenarioExecutionTraceJsonSerializer.Serialize(trace)), EntityStorageJson.Options));
    }

    /// <summary>Reconstructs an isolated environment and verifies exact deterministic execution of retained intent.</summary>
    /// <param name="json">Artifact returned by Capture; untrusted documents are validated before the factory runs.</param>
    /// <param name="createInterpreter">Factory interpreting the recipe with fresh stores and pinned source/policy revisions.</param>
    /// <param name="cancellationToken">Cancels reconstruction or execution.</param>
    /// <returns>A freshly executed trace identical to the retained trace.</returns>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    /// <exception cref="JsonException">The envelope, recipe or embedded trace is malformed or unsupported.</exception>
    /// <exception cref="InvalidOperationException">Reconstruction identity or execution differs from retained evidence.</exception>
    /// <exception cref="OperationCanceledException">Replay is cancelled.</exception>
    public static async Task<ScenarioExecutionTraceDocument> ReplayAsync(string json,
        Func<ReactionScenarioEnvironment, CancellationToken, ValueTask<MachineReactionScenarioInterpreter>> createInterpreter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createInterpreter);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StrictDocumentJson.TryReadCanonicalObject(json, EntityStorageJson.Options, "reaction replay artifact",
                out Envelope? artifact, out var error) || artifact is null)
            throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        if (artifact.Schema != Schema || artifact.Environment is null || artifact.Trace is null)
            throw new JsonException("A replay artifact requires the current schema, environment and trace.");
        var expected = ScenarioExecutionTraceJsonSerializer.Deserialize(artifact.Trace);
        var interpreter = await createInterpreter(artifact.Environment, cancellationToken).ConfigureAwait(false);
        if (interpreter is null || interpreter.EnvironmentIdentity != artifact.Environment.Identity || interpreter.Identity != expected.Interpreter)
            throw new InvalidOperationException("The reconstructed reaction environment does not match the retained interpreter identity.");
        var actual = await ScenarioRunner.ExecuteAsync(expected.ToInitialWorldSnapshot(), interpreter, cancellationToken).ConfigureAwait(false);
        if (actual.Fingerprint != expected.Fingerprint)
            throw new InvalidOperationException("Reaction replay diverged from the retained execution trace.");
        return actual;
    }
}
